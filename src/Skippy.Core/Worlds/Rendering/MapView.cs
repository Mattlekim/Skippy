namespace Skippy.Core.Worlds.Rendering;

/// <summary>
/// What the terrain is coloured by. <see cref="Biome"/> is the game view; the rest
/// paint one of the generator's raw input fields straight onto the ground, which is
/// how you see why a biome landed where it did.
/// </summary>
public enum MapView
{
    /// <summary>Normal play: biome colours, shaded by elevation and slope.</summary>
    Biome,

    /// <summary>Raw terrain height, 0..1, dark (deep) to white (peaks).</summary>
    Height,

    /// <summary>Temperature after the altitude lapse, cold (blue) to hot (red).</summary>
    Temperature,

    /// <summary>Moisture, dry (tan) to wet (blue).</summary>
    Moisture,
}

public static class MapViews
{
    /// <summary>Short upper-case label for the debug read-out.</summary>
    public static string Name(this MapView view) => view switch
    {
        MapView.Height => "HEIGHT",
        MapView.Temperature => "TEMP",
        MapView.Moisture => "MOISTURE",
        _ => "BIOME",
    };

    public static MapView Next(this MapView view) => view switch
    {
        MapView.Biome => MapView.Height,
        MapView.Height => MapView.Temperature,
        MapView.Temperature => MapView.Moisture,
        _ => MapView.Biome,
    };
}
