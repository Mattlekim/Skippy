using System;
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

    // Lower = larger features. Height varies fastest; temperature the slowest
    // so climate bands are broad and biomes form coherent regions.
    private const float HeightScale = 0.010f;
    private const float TemperatureScale = 0.004f;
    private const float MoistureScale = 0.006f;

    public DefaultWorldGenerator(int seed)
    {
        _height = new PerlinNoise(seed);
        _temperature = new PerlinNoise(seed * 31 + 7);
        _moisture = new PerlinNoise(seed * 17 + 91);
        _vegetation = new PerlinNoise(seed * 7 + 53);
    }

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

                float baseTemp = _temperature.Fractal01(wx * TemperatureScale, wy * TemperatureScale, octaves: 3);
                float moisture = _moisture.Fractal01(wx * MoistureScale, wy * MoistureScale, octaves: 4);

                // Altitude cooling: only land above the sea gets colder with height.
                float aboveSea = MathF.Max(0f, h - WorldConstants.SeaLevel);
                float temperature = Clamp01(baseTemp - aboveSea * WorldConstants.TemperatureLapse);

                // Water and shorelines are damp.
                if (h < WorldConstants.SeaLevel)
                    moisture = Clamp01(moisture + 0.3f);

                var tile = new Tile
                {
                    Height = h,
                    Temperature = temperature,
                    Moisture = moisture,
                    Level = QuantizeLevel(h),
                    Biome = BiomeClassifier.Classify(h, temperature, moisture),
                    // Low-frequency clumping field: how densely this tile is planted.
                    VegetationDensity = _vegetation.Sample(wx * 0.06f, wy * 0.06f) * 0.5f + 0.5f,
                };
                tile.Vegetation = VegetationPicker.Pick(tile, _vegetation, wx, wy);

                chunk.TileAt(lx, ly) = tile;
            }
        }

        chunk.Generated = true;
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
