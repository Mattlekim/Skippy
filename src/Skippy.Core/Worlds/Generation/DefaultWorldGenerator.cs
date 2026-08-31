using System;
using Skippy.Core.Worlds.Noise;

namespace Skippy.Core.Worlds.Generation;

/// <summary>
/// The stock terrain generator. For every tile it evaluates three independent
/// noise fields — height, base temperature, moisture — then couples them the way
/// a real climate does:
///
///   • the planet is banded by latitude: the equator is hot and the poles are frozen,
///     and the rain falls in Earth's own belts — wet on the equator, the deserts at
///     the subtropics, wet again at the mid-latitude storm track, dry at the poles;
///   • temperature drops with altitude (lapse rate), so mountains are cold even
///     in warm latitudes;
///   • land near/below the water table reads wetter.
///
/// Biome and vegetation are then derived from the resulting climate.
///
/// <para>
/// <b>The world wraps.</b> It is a band that closes on itself east–west (see
/// <see cref="WorldBounds"/>), and every field here is sampled so that it closes with
/// it. Two things make that work, and both are in <see cref="Field"/>: the noise is
/// sampled in the world's own screen-aligned frame (u across, v down) rather than in
/// raw tile x/y, and each field's frequency is nudged to the nearest one that fits a
/// whole number of noise cells around the world, so the lattice meets itself exactly.
/// Everything downstream — the coastal erosion, the spike cleanup, the lake flood — is
/// a pure function of position built on those fields, so it inherits the wrap for free.
/// There is no seam to stitch and no special case at the join: a lap east really is the
/// same ground again.
/// </para>
/// </summary>
public sealed class DefaultWorldGenerator : IWorldGenerator
{
    /// <summary>
    /// One noise field, sampled in the world's frame and closed around it.
    ///
    /// The scale is in noise cells per unit of u, and the period is how many of those
    /// cells fit around the world — a whole number, always, because that is the whole
    /// point: u and u + circumference then land on the same lattice cell and read the
    /// same value. <see cref="DefaultWorldGenerator.Local"/> and
    /// <see cref="DefaultWorldGenerator.Global"/> build them.
    /// </summary>
    private readonly struct Field
    {
        private readonly PerlinNoise _noise;
        private readonly float _scale;
        private readonly int _period;

        public Field(PerlinNoise noise, float scale, int period)
        {
            _noise = noise;
            _scale = scale;
            _period = period;
        }

        public float Fractal01(float u, float v, int octaves)
            => _noise.Fractal01(u * _scale, v * _scale, octaves, 2f, 0.5f, _period);

        /// <summary>
        /// One octave at <paramref name="octave"/> steps above the base frequency — for the
        /// ridged sum, which shapes each octave before adding it and so cannot use
        /// <see cref="Fractal01"/>. The period doubles per octave, exactly as fBm's does.
        /// </summary>
        public float Octave(float u, float v, int octave)
        {
            float freq = 1 << octave;
            return _noise.Sample(u * _scale * freq, v * _scale * freq, _period << octave);
        }
    }

    private readonly PerlinNoise _heightNoise;
    private readonly PerlinNoise _temperatureNoise;
    private readonly PerlinNoise _moistureNoise;
    private readonly PerlinNoise _vegetationNoise;
    private readonly PerlinNoise _standNoise;
    private readonly PerlinNoise _speciesNoise;
    private readonly PerlinNoise _patchNoise;
    private readonly PerlinNoise _mountainNoise;
    private readonly PerlinNoise _coastNoise;

    // The same fields, fitted to the world's current size. Rebuilt whenever the size
    // dial moves, since every frequency is snapped to the circumference.
    private Field _height, _temperature, _moisture, _vegetation;
    private Field _stand, _species, _patch, _mountains, _ridge, _coast, _clump;
    private int _fittedSpan;

    /// <summary>
    /// The world's dials, held live rather than copied: the settings panel turns them and
    /// asks the world to regenerate, and the next chunk built reads the new ones.
    /// </summary>
    private readonly WorldSettings _settings;

    /// <summary>
    /// Which bodies of water are small enough to be lakes. It measures the water by
    /// flooding this generator's own height field, so it agrees exactly with the tiles
    /// built from it — see <see cref="LakeFinder"/>.
    /// </summary>
    private readonly LakeFinder _lakes;

    // ---- field frequencies -------------------------------------------------
    // Lower = larger features. These are *local* scales: a wavelength in tiles, the
    // size a thing is on the ground. They do not follow the world size — a mountain
    // range is the size a mountain range is, whether the planet is small or vast, and
    // a big world simply has more of them.
    private const float HeightScale = 0.010f;

    /// <summary>
    /// Frequency of the surface-detail field. Far higher than anything else —
    /// patches want to be a few tiles across, so they read as patches at close
    /// range instead of as another continent-wide gradient.
    /// </summary>
    private const float PatchScale = 0.22f;

    /// <summary>
    /// Frequency of the two stand fields — which tree owns a wood, and which of that
    /// tree's painted forms the wood is grown from.
    ///
    /// A wood wants to be tens of tiles across: big enough to walk into and read as all
    /// one thing, small enough that a forest holds several of them. The species field runs
    /// a little faster, and off its own seed, so its patches cut across the kind's rather
    /// than lining up with them — one evergreen wood can then be spruce at one end and fir
    /// at the other, and every painted tree gets used somewhere instead of the sheet's
    /// first few taking every pure stand on the map.
    /// </summary>
    private const float StandScale = 0.013f;
    private const float SpeciesScale = 0.016f;

    /// <summary>
    /// Frequency of the two vegetation fields: the broad one deciding how densely a
    /// stretch of country is planted, and the tighter one that breaks a wood up into
    /// its thickets and clearings.
    /// </summary>
    private const float VegetationScale = 0.06f;
    private const float ClumpScale = 0.35f;

    // ---- climate --------------------------------------------------------------
    // The climate fields are the exception: they are *global*, sized as a fraction of
    // the planet rather than in tiles, so a tiny world gets the same handful of warm
    // and cool regions around it that a huge one does instead of one climate covering
    // the lot. These are counts of noise cells all the way round, so they scale with
    // the map size by construction.

    /// <summary>Regional warm/cool anomalies around the planet, and damp/dry ones.</summary>
    private const int TemperatureCells = 6;
    private const int MoistureCells = 7;

    /// <summary>
    /// Octaves for the climate fields. Kept low on purpose: every extra octave adds
    /// higher-frequency wiggle, and on a climate field that reads as one tile being
    /// noticeably warmer or wetter than the one beside it.
    /// </summary>
    private const int ClimateOctaves = 2;

    /// <summary>
    /// How much of the temperature is the regional anomaly, against the latitude band it
    /// sits in. Under a half, so latitude always wins: no amount of local warmth makes a
    /// pole temperate, but a region can still run a good way warmer or cooler than its
    /// neighbours at the same latitude, which is what keeps the bands from reading as
    /// stripes painted across the map.
    /// </summary>
    private const float TemperatureAnomaly = 0.42f;
    private const float MoistureAnomaly = 0.40f;

    /// <summary>
    /// The pole and equator ends of the temperature profile, at the coldest and hottest
    /// settings of the world temperature dial. The dial slides the whole profile between
    /// them: an icy world's poles are far below freezing and even its equator is only
    /// mild; a hot one's equator saturates and its poles thaw to something habitable.
    ///
    /// The ends deliberately sit outside 0..1. The band a tile lands in is this profile
    /// plus its regional anomaly, so a pole that aims below zero is what makes a genuine
    /// ice cap — ground that is at the floor no matter which way the anomaly went.
    /// </summary>
    private const float PoleTempCold = -0.15f, PoleTempHot = 0.40f;
    private const float EquatorTempCold = 0.60f, EquatorTempHot = 1.15f;

    /// <summary>The dry and wet ends of the rainfall profile, as <see cref="PoleTempCold"/>.</summary>
    private const float DryLow = -0.10f, DryHigh = 0.28f;
    private const float WetLow = 0.66f, WetHigh = 1.08f;

    public DefaultWorldGenerator(int seed) : this(seed, new WorldSettings()) { }

    public DefaultWorldGenerator(int seed, WorldSettings settings)
    {
        _settings = settings;
        _heightNoise = new PerlinNoise(seed);
        _temperatureNoise = new PerlinNoise(seed * 31 + 7);
        _moistureNoise = new PerlinNoise(seed * 17 + 91);
        _vegetationNoise = new PerlinNoise(seed * 7 + 53);
        _standNoise = new PerlinNoise(seed * 37 + 23);
        _speciesNoise = new PerlinNoise(seed * 149 + 29);
        _patchNoise = new PerlinNoise(seed * 13 + 41);
        _mountainNoise = new PerlinNoise(seed * 101 + 5);
        _coastNoise = new PerlinNoise(seed * 211 + 13);
        Fit();

        // The finished height — cleaned coast and all — is what the tiles themselves are
        // classified from, so the flood has to read that and not the raw field, or a body
        // of water could come out a different shape than the one on screen. Beyond a pole
        // it reads as dry: there is no world out there for the water to go on into, and
        // without this a lake lying against the edge would flood off the map and be
        // measured as an ocean.
        _lakes = new LakeFinder((x, y) => Bounds.Contains(x, y) && CleanCoast(x, y) < WorldConstants.SeaLevel);
    }

    private WorldBounds Bounds => _settings.Bounds;

    /// <summary>
    /// Drops what was worked out about the world that has just been thrown away. The dials
    /// have moved, so the ground is about to come out differently shaped and last world's
    /// lakes are no guide to this one's.
    /// </summary>
    public void Reset()
    {
        _lakes.Clear();
        Fit();
    }

    // ---- fitting the fields to the world -----------------------------------

    /// <summary>
    /// Rotates a tile step into a noise step. The world's u/v axes run at 45° to the tile
    /// grid and are √2 longer per tile, so a field asked for a wavelength of so many
    /// *tiles* has to be sampled that much more slowly in u/v to come out that size on
    /// the ground.
    /// </summary>
    private const float TilesToBand = 0.70710678f; // 1/√2

    /// <summary>
    /// Fewest noise cells a field may be squeezed to around the world. A field whose
    /// authored wavelength is longer than the whole planet cannot have it — one cell
    /// around would make it a single smooth blob with no features at all — so it is
    /// given a few instead. Only the very lowest-frequency fields on the very smallest
    /// worlds ever hit this.
    /// </summary>
    private const int MinPeriod = 3;

    private void Fit()
    {
        int span = _settings.Span;
        if (span == _fittedSpan)
            return;

        _height = Local(_heightNoise, HeightScale);
        _mountains = Local(_mountainNoise, MountainScale);
        _ridge = Local(_mountainNoise, RidgeScale);
        _coast = Local(_coastNoise, CoastScale);
        _patch = Local(_patchNoise, PatchScale);
        _stand = Local(_standNoise, StandScale);
        _species = Local(_speciesNoise, SpeciesScale);
        _vegetation = Local(_vegetationNoise, VegetationScale);
        _clump = Local(_vegetationNoise, ClumpScale);

        _temperature = Global(_temperatureNoise, TemperatureCells);
        _moisture = Global(_moistureNoise, MoistureCells);

        _fittedSpan = span;
    }

    /// <summary>
    /// A field whose features are a fixed size *in tiles*, closed around the world by
    /// rounding its frequency to the nearest one that fits a whole number of cells. The
    /// rounding is what makes the two sides of the map meet, and it costs nothing worth
    /// having: at these frequencies it is a fraction of a percent off the wavelength that
    /// was asked for.
    /// </summary>
    private Field Local(PerlinNoise noise, float tileScale)
    {
        int circumference = _settings.Circumference;
        int period = Math.Max(MinPeriod, (int)MathF.Round(circumference * tileScale * TilesToBand));
        return new Field(noise, period / (float)circumference, period);
    }

    /// <summary>
    /// A field whose features are a fixed fraction *of the world*: so many of them all the
    /// way round, whatever size the world is. No rounding to do — the count is the period.
    /// </summary>
    private Field Global(PerlinNoise noise, int cellsAround)
        => new(noise, cellsAround / (float)_settings.Circumference, cellsAround);

    // ---- mountains ---------------------------------------------------------
    // A low-frequency mask marks out mountain ranges; inside them, ground that is
    // already high is lifted hard into peaks. So ranges rise where the land was
    // climbing anyway — leaving the lowlands and their biomes untouched — and their
    // flanks come out steep, which is what the renderer paints as cliff rock.
    private const float MountainScale = 0.006f;   // where ranges sit
    private const float RidgeScale = 0.045f;      // crest/cleft spacing within a range
    private const float MountainThreshold = 0.58f;
    private const float MountainBand = 0.22f;
    private const float MountainFoot = 0.55f;     // only ground above this is uplifted
    // How hard the crests are pushed up. Kept well below the headroom to the cap on
    // purpose: if the uplift saturated the height at 1.0, every crest would quantize
    // to the same top elevation level and the range would read as one flat plateau.
    // At this strength a crest lands several levels above its neighbouring clefts, so
    // the range comes out as stepped crags with steep faces instead of a mesa.
    private const float MountainHeight = 0.42f;

    /// <summary>
    /// Terrain height with mountain uplift folded in — the one field the whole world is
    /// built from, so everything reading it agrees on where the high ground is.
    /// </summary>
    private float TerrainHeight(int wx, int wy)
    {
        float u = WorldBounds.U(wx, wy), v = WorldBounds.V(wx, wy);

        // The world's age weathers the land before anything is built on it, so everything
        // downstream follows from it: an old world's ground rarely climbs to the mountain
        // foot, so it grows fewer ranges as well as lower ones.
        float h = Weather(ShapeHeight(_height.Fractal01(u, v, octaves: 5)));
        float inRange = Smooth01((_mountains.Fractal01(u, v, octaves: 4) - MountainThreshold) / MountainBand);
        float highland = Smooth01((h - MountainFoot) / 0.25f);
        // Ridged detail so a range rises as crags and clefts rather than a flat cap:
        // it is the clefts biting down between crests that give the flanks the steep,
        // rock-baring faces the renderer draws as cliffs. Squared to sharpen them.
        float ridged = Ridged(u, v, octaves: 4);
        ridged *= ridged;
        h = Clamp01(h + MountainHeight * _settings.MountainUplift * inRange * highland * ridged);
        return ErodeCoast(u, v, h);
    }

    /// <summary>
    /// Ages the land: a curve on how far up from sea level the ground stands, so an old
    /// world's hills have been worn down into plains and a young one's slopes stand as
    /// highland. See <see cref="WorldSettings.ReliefGamma"/> for why it is a curve on the
    /// land fraction rather than a multiplier on the height.
    ///
    /// The sea floor is left alone — how deep the water is under its own surface is not what
    /// "hilly" means, and the shore is drawn from the waterline either way.
    /// </summary>
    private float Weather(float h)
    {
        float gamma = _settings.ReliefGamma;
        if (gamma == 1f || h <= WorldConstants.SeaLevel)
            return h;

        float land = (h - WorldConstants.SeaLevel) / (1f - WorldConstants.SeaLevel);
        return WorldConstants.SeaLevel + MathF.Pow(land, gamma) * (1f - WorldConstants.SeaLevel);
    }

    // ---- coastal erosion ---------------------------------------------------
    // A straight-run, grid-aligned coastline is the giveaway of a smooth height field
    // meeting a flat sea. This pass carves it: near the waterline the height is nudged
    // up or down by a fractal field, so the shore wanders into bays and headlands the
    // way an eroded coast does. It is masked to a band around sea level, so deep water,
    // inland ground and the mountains are left exactly as they were; and it is a pure
    // function of position, so neighbouring chunks carve the same coast with no seam.
    private const float CoastScale = 0.085f;  // base wavelength of the bays/headlands, in tiles
    private const float CoastAmp = 0.34f;     // how far the waterline is pushed, in height units
    private const float CoastBand = 0.08f;    // height band either side of sea level it acts over

    private float ErodeCoast(float u, float v, float h)
    {
        float d = h - WorldConstants.SeaLevel;
        float mask = MathF.Exp(-(d * d) / (2f * CoastBand * CoastBand));
        if (mask < 0.01f)
            return h; // deep water, inland or mountain — nothing to carve

        float meander = _coast.Fractal01(u, v, octaves: 3) - 0.5f;
        return Clamp01(h + meander * (CoastAmp * mask));
    }

    /// <summary>Ridged fractal noise in 0..1: sharp crests where the base noise crosses zero.</summary>
    private float Ridged(float u, float v, int octaves)
    {
        float sum = 0f, amp = 1f, norm = 0f;
        for (int i = 0; i < octaves; i++)
        {
            float r = 1f - MathF.Abs(_ridge.Octave(u, v, i));
            sum += r * r * amp;
            norm += amp;
            amp *= 0.5f;
        }
        return sum / norm;
    }

    /// <summary>
    /// The final coastal cleanup: after the meander has carved the shore, a lone land
    /// tile jutting into the sea is sunk and a lone sea tile ringed by land is filled,
    /// so the coast has no one-tile spikes or pinholes — the "no points" part of an
    /// eroded coastline. It only looks at tiles near the waterline (elsewhere it returns
    /// the height untouched), and reads the already-eroded field, so neighbouring chunks
    /// resolve the same coast with no seam.
    /// </summary>
    private float CleanCoast(int wx, int wy)
    {
        float h = TerrainHeight(wx, wy);
        if (MathF.Abs(h - WorldConstants.SeaLevel) > 2f * CoastBand)
            return h; // well inland or well offshore — not a shore tile

        int sea = 0, land = 0;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                if (TerrainHeight(wx + dx, wy + dy) < WorldConstants.SeaLevel) sea++; else land++;
            }

        bool selfSea = h < WorldConstants.SeaLevel;
        if (!selfSea && sea >= 6) return WorldConstants.SeaLevel - 0.02f; // spike → sink into the shallows
        if (selfSea && land >= 6) return WorldConstants.SeaLevel + 0.01f; // pinhole → fill to the beach
        return h;
    }

    public void Generate(Chunk chunk)
    {
        // The size dial may have moved since the last chunk was built, and every field's
        // frequency is cut to fit the world it is closing around.
        Fit();
        WorldBounds bounds = Bounds;

        for (int ly = 0; ly < Chunk.Size; ly++)
        {
            for (int lx = 0; lx < Chunk.Size; lx++)
            {
                int wx = chunk.OriginX + lx;
                int wy = chunk.OriginY + ly;
                float u = WorldBounds.U(wx, wy), v = WorldBounds.V(wx, wy);

                float h = CleanCoast(wx, wy);

                // Where this tile stands between the equator and the pole. Everything the
                // climate does hangs off it — see <see cref="ZonalTemperature"/>.
                float latitude = bounds.Latitude(v);

                // The band the latitude puts it in, then its own regional anomaly on top.
                // The anomaly is spread across 0..1 before it is added — see
                // <see cref="Spread"/> — so warm regions and cool ones are equally common
                // instead of everything bunching around the band's own value.
                // Held back over the cap, so nothing local can put a warm region nearer
                // the pole than a cold one — see <see cref="PolarCalm"/>.
                float anomalyWeight = TemperatureAnomaly
                                    * (1f - PolarCalm * Smooth01((latitude - PolarCalmFrom) / (1f - PolarCalmFrom)));

                float baseTemp = ZonalTemperature(latitude)
                               + (Spread(_temperature.Fractal01(u, v, ClimateOctaves)) - 0.5f) * anomalyWeight;

                float moisture = ZonalMoisture(latitude)
                               + (Spread(_moisture.Fractal01(u, v, ClimateOctaves)) - 0.5f) * MoistureAnomaly;

                // Altitude cooling: only land above the sea gets colder with height.
                float aboveSea = MathF.Max(0f, h - WorldConstants.SeaLevel);
                float temperature = Clamp01(baseTemp - aboveSea * WorldConstants.TemperatureLapse);

                // Ground near the water table is damp. This used to be a flat bonus
                // applied below sea level, which put a hard wet/dry step right along
                // every shoreline; it now fades out with height instead.
                moisture = Clamp01(moisture + CoastalDamp * Smooth01(
                    (WorldConstants.SeaLevel + DampBand - h) / (2f * DampBand)));

                Biome biome = BiomeClassifier.Classify(h, temperature, moisture);
                bool inWorld = bounds.Contains(wx, wy);

                // Deep or shallow is a matter of how far down the bed is; lake or sea is a
                // matter of how much water there is, which no single tile can answer. So it
                // is asked here, of the body as a whole, rather than in the classifier.
                // Not asked past a pole: there is no body out there to measure, and the
                // answer would only fill the flood's cache with water nobody will see.
                if (inWorld && biome is Biome.Ocean or Biome.ShallowWater && _lakes.IsLake(wx, wy))
                    biome = Biome.Lake;

                // Which wood stands here. Spread for the same reason the climate anomalies
                // are: raw fBm bunches around the middle, and a stand field that never
                // reaches its ends would grow nothing but mixed woods.
                float stand = Spread(_stand.Fractal01(u, v, ClimateOctaves));

                var tile = new Tile
                {
                    Height = h,
                    Temperature = temperature,
                    Moisture = moisture,
                    Level = QuantizeLevel(h),
                    Biome = biome,
                    Forest = Forests.Classify(biome, temperature, stand),
                    ForestVariant = Spread(_species.Fractal01(u, v, ClimateOctaves)),
                    // Low-frequency clumping field: how densely this tile is planted.
                    VegetationDensity = _vegetation.Fractal01(u, v, octaves: 1),
                    Patch = _patch.Fractal01(u, v, octaves: 3),

                    // Past a pole there is no world. The tile is still worked out in full —
                    // the ground the last row in the world stands on has to be lit and
                    // shaped against real neighbours, not against a wall of nothing — it is
                    // simply never drawn, and nothing may walk onto it.
                    IsVoid = !inWorld,
                };
                tile.Vegetation = VegetationPicker.Pick(
                    tile, _clump.Fractal01(u, v, octaves: 1), bounds.Hash01(wx, wy));

                chunk.TileAt(lx, ly) = tile;
            }
        }

        chunk.Generated = true;
    }

    // ---- the zonal climate -------------------------------------------------

    /// <summary>
    /// The temperature a latitude runs at before anything local happens to it: hot on the
    /// equator, falling away to the poles.
    ///
    /// Earth's own annual zonal mean, pinned at every ten degrees and normalised: flat
    /// across the tropics, where the sun is nearly overhead all year and 10° north of the
    /// equator is no cooler than the equator itself; then a long, steady fall through the
    /// mid-latitudes; and — this is the part that matters — <em>still falling at the
    /// pole</em>. The last stretch of the map has to keep getting colder, or latitude
    /// stops saying anything there and the regional anomaly takes over, which reads on
    /// the map as the world warming up again as you reach the top of it.
    ///
    /// The two ends are set by the world's temperature dial, so a hot world moves the
    /// whole profile up rather than clipping the top off it (see
    /// <see cref="WorldSettings.Warmth"/>).
    /// </summary>
    private float ZonalTemperature(float latitude)
    {
        float warmth = _settings.Warmth;
        float pole = Lerp(PoleTempCold, PoleTempHot, warmth);
        float equator = Lerp(EquatorTempCold, EquatorTempHot, warmth);
        return Lerp(pole, equator, Profile(latitude, TempLatitudes, TempProfile));
    }

    /// <summary>
    /// Earth's annual zonal mean surface temperature, every 20° from the equator to the
    /// pole, scaled so the equator is 1 and the pole is 0. Roughly +26 °C down to −23 °C.
    /// </summary>
    private static readonly float[] TempLatitudes = { 0f, 0.22f, 0.44f, 0.67f, 0.89f, 1f };
    private static readonly float[] TempProfile = { 1.00f, 0.97f, 0.75f, 0.44f, 0.12f, 0f };

    /// <summary>
    /// How much the regional anomaly is damped as the latitude climbs, and where it
    /// starts being damped.
    ///
    /// The zonal profile is at its steepest through the mid-latitudes and shallower
    /// again over the cap, so without this the anomaly could still out-argue it in the
    /// last stretch and put a warm region north of a cold one. Held back over the cap,
    /// the pole is the coldest ground on the map wherever you stand on it — which is
    /// what an ice cap is.
    /// </summary>
    private const float PolarCalm = 0.6f, PolarCalmFrom = 0.7f;

    /// <summary>
    /// The rainfall a latitude runs at, following the circulation cells that put Earth's
    /// deserts where they are: air rises wet over the equator, comes down dry over the
    /// subtropics — which is the belt nearly every great desert on Earth sits in — rises
    /// again along the mid-latitude storm track, and the poles, being cold, are deserts
    /// too however much ice is on them.
    ///
    /// So the map bands: rainforest on the equator, a ring of desert either side of it,
    /// then temperate forest, then tundra. Local anomaly and coastal damp break the bands
    /// up afterwards, so they read as a climate rather than as stripes.
    /// </summary>
    private float ZonalMoisture(float latitude)
    {
        float wet = Profile(latitude, BandLatitudes, BandWetness);
        float dryEnd = Lerp(DryLow, DryHigh, _settings.Wetness);
        float wetEnd = Lerp(WetLow, WetHigh, _settings.Wetness);
        return Lerp(dryEnd, wetEnd, wet);
    }

    /// <summary>Latitudes the rainfall profile is pinned at, equator first, and its value there.</summary>
    private static readonly float[] BandLatitudes = { 0f, 0.30f, 0.58f, 1f };
    private static readonly float[] BandWetness = { 1.00f, 0.08f, 0.72f, 0.20f };

    /// <summary>
    /// A profile read off its pinned latitudes, smoothed between them so it has no
    /// corners. Both the temperature and the rainfall bands are shaped this way.
    /// </summary>
    private static float Profile(float latitude, float[] latitudes, float[] values)
    {
        for (int i = 1; i < latitudes.Length; i++)
        {
            if (latitude > latitudes[i] && i < latitudes.Length - 1)
                continue;

            float t = (latitude - latitudes[i - 1]) / (latitudes[i] - latitudes[i - 1]);
            return Lerp(values[i - 1], values[i], Smooth01(t));
        }
        return values[^1];
    }

    /// <summary>
    /// Spreads a climate field into an even 0..1 distribution.
    ///
    /// fBm sums octaves, so its output is near-Gaussian and bunched hard around the
    /// middle: measured over land, the raw temperature and moisture fields only
    /// spanned about 0.40..0.69. The dry and hot corners of the climate diagram were
    /// therefore unreachable — grassland generated at literally 0% of tiles and
    /// every land tile came out forest. Pushing the field through a logistic CDF,
    /// with sigma matched to the field's measured spread, stretches it smoothly
    /// across the full range and leaves none of the clipped plateaus a plain gain
    /// would.
    /// </summary>
    private static float Spread(float v)
        => 1f / (1f + MathF.Exp(-1.702f * (v - 0.5f) / ClimateSigma));

    /// <summary>Standard deviation of the raw fBm climate fields, measured over land.</summary>
    internal const float ClimateSigma = 0.10f;

    /// <summary>How much damper ground at the water table is, and the height band it fades over.</summary>
    private const float CoastalDamp = 0.30f;
    private const float DampBand = 0.07f;

    /// <summary>Clamped smoothstep, so a blend has no hard edge where it begins or ends.</summary>
    private static float Smooth01(float t)
    {
        t = Clamp01(t);
        return t * t * (3f - 2f * t);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>
    /// Reshapes raw noise into a terrain profile with real relief: keep broad
    /// oceans/lowlands, but let the high end rise into mountains so peaks reach
    /// the snow line. A gentle S-curve that lifts the upper range.
    /// </summary>
    private static float ShapeHeight(float h)
    {
        // Emphasize highlands: values above mid get pushed up toward 1.
        float m = (h - 0.5f) * 2f;                 // -1..1
        float shaped = 0.5f + 0.5f * MathF.Sign(m) * MathF.Pow(MathF.Abs(m), 0.78f);
        return Clamp01(shaped);
    }

    /// <summary>Maps continuous height to a discrete elevation step. Water is flat at 0.</summary>
    private static byte QuantizeLevel(float h)
    {
        if (h < WorldConstants.SeaLevel) return 0;
        float t = (h - WorldConstants.SeaLevel) / (1f - WorldConstants.SeaLevel);
        int level = (int)MathF.Round(t * WorldConstants.MaxLevel);
        return (byte)Math.Clamp(level, 0, WorldConstants.MaxLevel);
    }

    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
}
