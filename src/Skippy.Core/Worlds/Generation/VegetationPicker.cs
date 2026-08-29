using Skippy.Core.Worlds.Noise;

namespace Skippy.Core.Worlds.Generation;

/// <summary>
/// Chooses per-tile vegetation from the tile's biome and climate. A low-frequency
/// density field (from noise) creates clumps — dense forests, scattered savanna
/// trees — while a per-tile hash adds fine variation so clumps aren't uniform.
/// </summary>
public static class VegetationPicker
{
    public static Vegetation Pick(in Tile tile, PerlinNoise density, int worldX, int worldY)
    {
        if (tile.IsWater || tile.Biome == Biome.Beach)
            return Vegetation.None;

        // Clumping field (0..1) + fine per-tile jitter (0..1).
        float d = density.Sample(worldX * 0.35f, worldY * 0.35f) * 0.5f + 0.5f;
        float r = Hash01(worldX, worldY);

        return tile.Biome switch
        {
            Biome.TemperateForest => d > 0.55f ? Vegetation.BroadleafTree : r > 0.75f ? Vegetation.Bush : Vegetation.GrassTuft,
            Biome.Taiga           => d > 0.52f ? Vegetation.PineTree       : r > 0.82f ? Vegetation.Bush : Vegetation.None,
            Biome.Jungle          => d > 0.42f ? Vegetation.PalmTree       : r > 0.55f ? Vegetation.Bush : Vegetation.GrassTuft,
            Biome.Grassland       => d > 0.82f ? Vegetation.BroadleafTree  : r > 0.50f ? Vegetation.GrassTuft : Vegetation.None,
            Biome.Savanna         => d > 0.86f ? Vegetation.BroadleafTree  : r > 0.60f ? Vegetation.GrassTuft : Vegetation.None,
            Biome.Desert          => d > 0.93f ? Vegetation.Cactus         : Vegetation.None,
            Biome.Tundra          => r > 0.90f ? Vegetation.Bush           : Vegetation.None,
            Biome.Swamp           => d > 0.60f ? Vegetation.Bush           : r > 0.60f ? Vegetation.GrassTuft : Vegetation.None,
            Biome.Rock            => r > 0.85f ? Vegetation.Rock            : Vegetation.None,
            _                     => Vegetation.None,
        };
    }

    private static float Hash01(int x, int y)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263);
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFF) / 65535f;
        }
    }
}
