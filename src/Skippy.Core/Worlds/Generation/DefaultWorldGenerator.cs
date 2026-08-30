using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Skippy.Core.Worlds.Noise;

namespace Skippy.Core.Worlds.Generation;

/// <summary>
/// The stock terrain generator. For every tile it evaluates three independent
/// noise fields — height, base temperature, moisture — then couples them the way
/// a real climate does:
///
///   • temperature drops with altitude (lapse rate), so mountains are cold even
///     in warm latitudes;
///   • land near/below the water table reads wetter.
///
/// Biome and vegetation are then derived from the resulting climate.
/// </summary>
public sealed class DefaultWorldGenerator : IWorldGenerator
{
    private readonly PerlinNoise _height;
    private readonly PerlinNoise _temperature;
    private readonly PerlinNoise _moisture;
    private readonly PerlinNoise _vegetation;
    private readonly PerlinNoise _patch;
    private readonly PerlinNoise _mountains;
    private readonly PerlinNoise _coast;
    private readonly RiverNetwork _rivers;
    private readonly List<RiverNetwork.RiverPath> _riverScratch = new();

    // Lower = larger features. Height varies fastest; temperature the slowest
    // so climate bands are broad and biomes form coherent regions.
    private const float HeightScale = 0.010f;

    // Climate is deliberately the slowest-moving thing on the map: hot must never
    // border cold, nor wet border dry. <see cref="Spread"/> multiplies the field's
    // spatial gradient about fourfold — that is the price of an even distribution —
    // so these frequencies are set low enough to pay it and still leave a climate
    // transition tens of tiles wide.
    private const float TemperatureScale = 0.0035f;
    private const float MoistureScale = 0.0030f;

    /// <summary>
    /// Octaves for the climate fields. Kept low on purpose: every extra octave adds
    /// higher-frequency wiggle, and on a climate field that reads as one tile being
    /// noticeably warmer or wetter than the one beside it.
    /// </summary>
    private const int ClimateOctaves = 2;

    /// <summary>
    /// Frequency of the surface-detail field. Far higher than the climate fields —
    /// patches want to be a few tiles across, so they read as patches at close
    /// range instead of as another continent-wide gradient.
    /// </summary>
    private const float PatchScale = 0.22f;

    public DefaultWorldGenerator(int seed)
    {
        _height = new PerlinNoise(seed);
        _temperature = new PerlinNoise(seed * 31 + 7);
        _moisture = new PerlinNoise(seed * 17 + 91);
        _vegetation = new PerlinNoise(seed * 7 + 53);
        _patch = new PerlinNoise(seed * 13 + 41);
        _mountains = new PerlinNoise(seed * 101 + 5);
        _coast = new PerlinNoise(seed * 211 + 13);
        _rivers = new RiverNetwork(seed, HeightAt);
    }

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
    /// Terrain height with mountain uplift folded in — the one field both the world
    /// and its rivers are built from, so they agree on where the high ground is.
    /// </summary>
    private float TerrainHeight(int wx, int wy)
    {
        float h = ShapeHeight(_height.Fractal01(wx * HeightScale, wy * HeightScale, octaves: 5));
        float inRange = Smooth01((_mountains.Fractal01(wx * MountainScale, wy * MountainScale, octaves: 4)
                                  - MountainThreshold) / MountainBand);
        float highland = Smooth01((h - MountainFoot) / 0.25f);
        // Ridged detail so a range rises as crags and clefts rather than a flat cap:
        // it is the clefts biting down between crests that give the flanks the steep,
        // rock-baring faces the renderer draws as cliffs. Squared to sharpen them.
        float ridged = Ridged(wx * RidgeScale, wy * RidgeScale, octaves: 4);
        ridged *= ridged;
        h = Clamp01(h + MountainHeight * inRange * highland * ridged);
        return ErodeCoast(wx, wy, h);
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

    private float ErodeCoast(int wx, int wy, float h)
    {
        float d = h - WorldConstants.SeaLevel;
        float mask = MathF.Exp(-(d * d) / (2f * CoastBand * CoastBand));
        if (mask < 0.01f)
            return h; // deep water, inland or mountain — nothing to carve

        float meander = _coast.Fractal01(wx * CoastScale, wy * CoastScale, octaves: 3) - 0.5f;
        return Clamp01(h + meander * (CoastAmp * mask));
    }

    /// <summary>Ridged fractal noise in 0..1: sharp crests where the base noise crosses zero.</summary>
    private float Ridged(float x, float y, int octaves)
    {
        float sum = 0f, amp = 1f, freq = 1f, norm = 0f;
        for (int i = 0; i < octaves; i++)
        {
            float r = 1f - MathF.Abs(_mountains.Sample(x * freq, y * freq));
            sum += r * r * amp;
            norm += amp;
            amp *= 0.5f;
            freq *= 2f;
        }
        return sum / norm;
    }

    /// <summary>The terrain height at a world tile — the field rivers run down.</summary>
    private float HeightAt(int wx, int wy) => TerrainHeight(wx, wy);

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
        for (int ly = 0; ly < Chunk.Size; ly++)
        {
            for (int lx = 0; lx < Chunk.Size; lx++)
            {
                int wx = chunk.OriginX + lx;
                int wy = chunk.OriginY + ly;

                float h = CleanCoast(wx, wy);

                float baseTemp = Spread(_temperature.Fractal01(wx * TemperatureScale, wy * TemperatureScale, ClimateOctaves));
                float moisture = Spread(_moisture.Fractal01(wx * MoistureScale, wy * MoistureScale, ClimateOctaves));

                // Altitude cooling: only land above the sea gets colder with height.
                float aboveSea = MathF.Max(0f, h - WorldConstants.SeaLevel);
                float temperature = Clamp01(baseTemp - aboveSea * WorldConstants.TemperatureLapse);

                // Ground near the water table is damp. This used to be a flat bonus
                // applied below sea level, which put a hard wet/dry step right along
                // every shoreline; it now fades out with height instead.
                moisture = Clamp01(moisture + CoastalDamp * Smooth01(
                    (WorldConstants.SeaLevel + DampBand - h) / (2f * DampBand)));

                var tile = new Tile
                {
                    Height = h,
                    Temperature = temperature,
                    Moisture = moisture,
                    Level = QuantizeLevel(h),
                    Biome = BiomeClassifier.Classify(h, temperature, moisture),
                    // Low-frequency clumping field: how densely this tile is planted.
                    VegetationDensity = _vegetation.Sample(wx * 0.06f, wy * 0.06f) * 0.5f + 0.5f,
                    Patch = _patch.Fractal01(wx * PatchScale, wy * PatchScale, octaves: 3),
                };
                tile.Vegetation = VegetationPicker.Pick(tile, _vegetation, wx, wy);

                chunk.TileAt(lx, ly) = tile;
            }
        }

        CarveRivers(chunk);
        chunk.Generated = true;
    }

    /// <summary>
    /// Stamps in every river crossing this chunk, after the climate pass — a river
    /// is a path across tiles, so it cannot be decided one tile at a time.
    /// </summary>
    private void CarveRivers(Chunk chunk)
    {
        float half = WorldConstants.RiverWidth * 0.5f;
        int reach = (int)MathF.Ceiling(half);

        _riverScratch.Clear();
        _rivers.Collect(chunk.OriginX - reach, chunk.OriginY - reach,
                        chunk.OriginX + Chunk.Size + reach, chunk.OriginY + Chunk.Size + reach,
                        _riverScratch);

        float halfSq = half * half;
        foreach (RiverNetwork.RiverPath path in _riverScratch)
        {
            foreach (Vector2 point in path.Points)
            {
                int px = (int)point.X, py = (int)point.Y;
                for (int dy = -reach; dy <= reach; dy++)
                {
                    for (int dx = -reach; dx <= reach; dx++)
                    {
                        if (dx * dx + dy * dy > halfSq)
                            continue; // keep the channel round, not square

                        int lx = px + dx - chunk.OriginX;
                        int ly = py + dy - chunk.OriginY;
                        if (lx < 0 || ly < 0 || lx >= Chunk.Size || ly >= Chunk.Size)
                            continue;

                        ref Tile t = ref chunk.TileAt(lx, ly);
                        if (t.IsWater)
                            continue; // already sea

                        // A river runs along the ground it crosses; it does not drop
                        // to sea level. Cutting the bed to level 0 put a channel
                        // hundreds of pixels below its own banks, and the shared
                        // corner vertices stretched the quads into spikes.
                        // The bed sits a riverbed's depth below the banks; the rest of
                        // the terrain keeps its shape and the water descends with it.
                        // In the now four-times-finer levels that depth is a few steps,
                        // not one, so the channel stays as deep as it always was.
                        t.Level -= (byte)Math.Min(t.Level, RiverbedDepth);

                        t.Height = MathF.Max(WorldConstants.SeaLevel, t.Height - 0.01f);
                        t.Moisture = Clamp01(t.Moisture + 0.25f);
                        t.Biome = Biome.ShallowWater;
                        t.Vegetation = Vegetation.None;
                    }
                }
            }
        }
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
    private const float ClimateSigma = 0.10f;

    /// <summary>How much damper ground at the water table is, and the height band it fades over.</summary>
    private const float CoastalDamp = 0.30f;
    private const float DampBand = 0.07f;

    /// <summary>Elevation steps a river cuts below its banks (scaled with the level count).</summary>
    private const byte RiverbedDepth = 4;

    /// <summary>Clamped smoothstep, so a blend has no hard edge where it begins or ends.</summary>
    private static float Smooth01(float t)
    {
        t = Clamp01(t);
        return t * t * (3f - 2f * t);
    }

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
