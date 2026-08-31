using System;

namespace Skippy.Core.Worlds.Generation;

/// <summary>
/// Maps a tile's height, temperature and moisture to a <see cref="Biome"/> using
/// a Whittaker-style climate diagram. Water (by height) and bare rock (by height)
/// override the climate lookup; everything else is a temperature × moisture grid.
/// Snow is not among them — it falls on whatever is below freezing.
///
/// The grid is held as data rather than nested ifs so that
/// <see cref="ClimateEdgeDistance"/> can measure how far a tile sits from the
/// nearest boundary — which is what lets vegetation thin out towards a biome's
/// edge instead of stopping dead at it.
///
/// Open country is the default and forest is the exception: trees are held to the
/// wet end of each temperature band, so woodland reads as somewhere the map put
/// there rather than as the background everything else interrupts.
/// </summary>
public static class BiomeClassifier
{
    /// <summary>Temperature band edges: cold | temperate | warm | hot.</summary>
    private static readonly float[] TemperatureBands = { 0.28f, 0.52f, 0.76f };

    /// <summary>
    /// The wettest ground that may still come out as open dry country — desert or savanna.
    ///
    /// Heat alone does not make a plain; the want of rain does. Ground at 40 °C carrying
    /// half its moisture is rainforest, not savanna — the hot, wet corner of the diagram is
    /// where the jungles are, and the only reason the tropics hold open country at all is
    /// that the subtropical belt is dry (see the rainfall cells in
    /// <see cref="DefaultWorldGenerator"/>). The hot band used to let savanna run to 74%,
    /// so a tile at 37 °C and 73% came out as plain — visibly wrong ground for its climate.
    ///
    /// Below this, the dry country still grades properly: desert at its driest, savanna
    /// between, jungle beyond — which is the Sahel's own order as you walk south.
    /// </summary>
    private const float DryCountryLimit = 0.50f;

    /// <summary>The two moisture thresholds within each temperature band, driest first.</summary>
    private static readonly float[][] MoistureBands =
    {
        new[] { 0.50f, 0.80f },           // cold
        new[] { 0.62f, 0.86f },           // temperate
        new[] { 0.30f, 0.78f },           // warm — savanna already ends at 30%
        new[] { 0.34f, DryCountryLimit }, // hot  — desert, then savanna, then jungle
    };

    /// <summary>The biome for each (temperature band, moisture band) cell.</summary>
    private static readonly Biome[][] ClimateBiomes =
    {
        new[] { Biome.Tundra,    Biome.Taiga,           Biome.Taiga },
        new[] { Biome.Grassland, Biome.TemperateForest, Biome.Swamp },
        new[] { Biome.Savanna,   Biome.Grassland,       Biome.Jungle },
        new[] { Biome.Desert,    Biome.Savanna,         Biome.Jungle },
    };

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

        // High altitude: bare rock. Whether it wears a white cap is not decided here —
        // snow lies on whatever is below freezing, and a peak is colder than the
        // country around it because of the lapse rate, so the caps come out on their own.
        if (height > WorldConstants.RockLine)
            return Biome.Rock;

        int band = TemperatureBandOf(temperature);
        float[] wet = MoistureBands[band];
        int column = moisture < wet[0] ? 0 : moisture < wet[1] ? 1 : 2;
        return ClimateBiomes[band][column];
    }

    /// <summary>
    /// How far a tile's climate sits from the nearest boundary that would land it in
    /// a different biome, in climate units (temperature and moisture share a 0..1
    /// scale, so the smaller of the two distances is the answer).
    /// </summary>
    public static float ClimateEdgeDistance(float temperature, float moisture)
    {
        float nearest = float.MaxValue;

        foreach (float edge in TemperatureBands)
            nearest = MathF.Min(nearest, MathF.Abs(temperature - edge));

        foreach (float edge in MoistureBands[TemperatureBandOf(temperature)])
            nearest = MathF.Min(nearest, MathF.Abs(moisture - edge));

        return nearest;
    }

    private static int TemperatureBandOf(float temperature)
        => temperature < TemperatureBands[0] ? 0
         : temperature < TemperatureBands[1] ? 1
         : temperature < TemperatureBands[2] ? 2
         : 3;
}
