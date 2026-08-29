using Microsoft.Xna.Framework;

namespace Skippy.Core.Worlds.Biomes;

/// <summary>Presentation data for biomes: the base surface color used to tint tiles.</summary>
public static class BiomeRegistry
{
    public static Color TopColor(Biome biome) => biome switch
    {
        Biome.Ocean           => new Color(28, 58, 110),
        Biome.ShallowWater    => new Color(52, 110, 170),
        Biome.Beach           => new Color(214, 196, 140),
        Biome.Grassland       => new Color(112, 158, 74),
        Biome.Savanna         => new Color(170, 168, 86),
        Biome.Desert          => new Color(216, 196, 132),
        Biome.TemperateForest => new Color(78, 126, 66),
        Biome.Taiga           => new Color(74, 118, 104),
        Biome.Tundra          => new Color(150, 150, 138),
        Biome.Snow            => new Color(236, 240, 244),
        Biome.Jungle          => new Color(56, 128, 58),
        Biome.Swamp           => new Color(84, 104, 72),
        Biome.Rock            => new Color(120, 116, 112),
        _                     => Color.Magenta,
    };

    public static string DisplayName(Biome biome) => biome switch
    {
        Biome.ShallowWater => "Shallow Water",
        Biome.TemperateForest => "Temperate Forest",
        _ => biome.ToString(),
    };
}
