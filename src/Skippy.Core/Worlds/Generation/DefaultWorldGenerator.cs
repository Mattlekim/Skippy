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
        _rivers = new RiverNetwork(seed, HeightAt);
    }

    /// <summary>The shaped terrain height at a world tile — the field rivers run down.</summary>
    private float HeightAt(int wx, int wy)
        => ShapeHeight(_height.Fractal01(wx * HeightScale, wy * HeightScale, octaves: 5));

    public void Generate(Chunk chunk)
    {
        for (int ly = 0; ly < Chunk.Size; ly++)
        {
            for (int lx = 0; lx < Chunk.Size; lx++)
            {
                int wx = chunk.OriginX + lx;
                int wy = chunk.OriginY + ly;

                float h = _height.Fractal01(wx * HeightScale, wy * HeightScale, octaves: 5);
                h = ShapeHeight(h);

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
                        // One elevation step down is a riverbed; the rest of the
                        // terrain keeps its shape and the water descends with it.
                        if (t.Level > 0)
                            t.Level--;

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
