using System;

namespace Skippy.Core.Worlds.Generation;

/// <summary>
/// Maps a tile's height, temperature and moisture to a <see cref="Biome"/> using
/// a Whittaker-style climate diagram. Water (by height) and rock/snow (by height)
/// override the climate lookup; everything else is a temperature × moisture grid.
/// </summary>
public static class BiomeClassifier
{
    public static Biome Classify(float height, float temperature, float moisture)
    {
        // Below sea level: open water, deeper = ocean, shallow rim = shallow water.
        if (height < WorldConstants.SeaLevel - 0.06f) return Biome.Ocean;
        if (height < WorldConstants.SeaLevel) return Biome.ShallowWater;

        // A dry, warm strip right at the shoreline reads as beach.
        if (height < WorldConstants.SeaLevel + WorldConstants.BeachBand
            && moisture < 0.6f && temperature > 0.3f)
        {
            return Biome.Beach;
        }

        // High altitude: bare rock, capped with snow where it's cold.
        if (height > WorldConstants.SnowLine)
            return temperature < 0.35f ? Biome.Snow : Biome.Rock;

        // Land climate grid: colder → warmer down the branches, drier → wetter across.
        if (temperature < 0.25f)
            return moisture < 0.35f ? Biome.Tundra
                 : moisture < 0.70f ? Biome.Taiga
                 : Biome.Snow;

        if (temperature < 0.55f)
            return moisture < 0.30f ? Biome.Grassland
                 : moisture < 0.65f ? Biome.TemperateForest
                 : Biome.Swamp;

        if (temperature < 0.78f)
            return moisture < 0.28f ? Biome.Savanna
                 : moisture < 0.60f ? Biome.Grassland
                 : Biome.Jungle;

        // Hot.
        return moisture < 0.30f ? Biome.Desert
             : moisture < 0.55f ? Biome.Savanna
             : Biome.Jungle;
    }
}
