using Microsoft.Xna.Framework;

namespace Skippy.Core.Worlds.Biomes;

/// <summary>Presentation data for biomes: the base surface color used to tint tiles.</summary>
public static class BiomeRegistry
{
    /// <summary>
    /// The ground colour of one tile. Most biomes are a flat swatch, but grass is
    /// not one green: it burns to straw where it is dry, deepens where it is wet,
    /// and goes pale and olive where it is cold. Grassland and savanna are therefore
    /// coloured from the tile's own climate, which keeps big open country from
    /// reading as a single fill.
    /// </summary>
    public static Color SurfaceColor(in Tile tile) => tile.Biome switch
    {
        Biome.Grassland or Biome.Savanna => GrassColor(tile.Temperature, tile.Moisture),
        // Wooded ground is its own floor, not the canopy swatch, and it darkens
        // where the stand thickens (see ForestFloor).
        Biome.TemperateForest => ForestFloor(tile, new Color(98, 134, 72), new Color(52, 66, 40)),
        Biome.Jungle          => ForestFloor(tile, new Color(76, 130, 60), new Color(30, 70, 40)),
        Biome.Taiga           => ForestFloor(tile, new Color(102, 118, 92), new Color(58, 76, 58)),
        _ => TopColor(tile.Biome),
    };

    /// <summary>
    /// The floor of a wood rather than its canopy. Bare and light where the stand is
    /// thin, it deepens into shaded leaf-litter where the trees crowd in — so the
    /// ground answers to the same density that decides how many trees stand on it.
    /// The patch field then dapples it, so the litter reads as textured, not flat.
    /// </summary>
    private static Color ForestFloor(in Tile tile, Color open, Color dense)
    {
        float shade = Smooth01((tile.VegetationDensity - 0.15f) / 0.6f);
        Color floor = Color.Lerp(open, dense, shade);
        float dapple = 0.86f + 0.28f * tile.Patch;
        return new Color(
            (int)System.MathF.Min(255f, floor.R * dapple),
            (int)System.MathF.Min(255f, floor.G * dapple),
            (int)System.MathF.Min(255f, floor.B * dapple));
    }

    private static readonly Color DryGrass = new(206, 196, 88); // sun-bleached yellow
    private static readonly Color LushGrass = new(84, 150, 66); // well-watered green

    /// <summary>
    /// Grass runs along a single green-to-yellow line, never off it. Water drives it
    /// green; drought and heat both bleach it back towards yellow, so the colour
    /// still answers to both climate fields without ever leaving the two-colour ramp.
    /// </summary>
    private static Color GrassColor(float temperature, float moisture)
    {
        float green = Smooth01((moisture - 0.15f) / 0.50f);
        green *= 1f - 0.45f * Smooth01((temperature - 0.55f) / 0.40f);
        return Color.Lerp(DryGrass, LushGrass, green);
    }

    /// <summary>Clamped smoothstep, so the ramps have no hard edge where they start.</summary>
    private static float Smooth01(float t)
    {
        t = t < 0f ? 0f : t > 1f ? 1f : t;
        return t * t * (3f - 2f * t);
    }

    /// <summary>The flat swatch for a biome, ignoring any per-tile climate variation.</summary>
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
        Biome.Tundra          => new Color(196, 198, 194), // frozen ground, pale with frost
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
