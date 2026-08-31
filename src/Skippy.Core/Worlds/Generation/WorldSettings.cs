using System;

namespace Skippy.Core.Worlds.Generation;

/// <summary>How warm the world runs, as a whole.</summary>
public enum WorldTemperature : byte
{
    Icy,
    Cold,
    Temperate,
    Warm,
    Hot,
}

/// <summary>How wet the world runs, as a whole.</summary>
public enum WorldRainfall : byte
{
    Arid,
    Dry,
    Moderate,
    Wet,
    Lush,
}

/// <summary>
/// How long the world has been weathering, which is what decides how hilly it is: a young
/// world is all raw relief and mountains, an ancient one has had them ground down to
/// rolling country.
/// </summary>
public enum PlanetAge : byte
{
    Young,
    Youthful,
    Mature,
    Old,
    Ancient,
}

/// <summary>
/// How big the planet is. Each band doubles the distance from pole to pole, and so
/// quadruples the amount of ground — see <see cref="WorldSettings.Span"/>.
/// </summary>
public enum WorldSize : byte
{
    Tiny,
    Small,
    Medium,
    Large,
    Huge,
}

/// <summary>
/// The dials the whole world is generated from: how big it is, how warm it runs, how wet
/// it runs, and how old — and so how worn down — it is. Each is a band either side of a
/// neutral middle, and each moves the entire map rather than adding a feature to it, so
/// what changes is the *kind* of world the same seed comes back as.
///
/// They are read live by <see cref="DefaultWorldGenerator"/>, which holds this very object:
/// turn a dial, call <see cref="World.Regenerate"/>, and the next chunk built uses it.
/// </summary>
public sealed class WorldSettings
{
    /// <summary>The band each dial sits at; <see cref="Middle"/> is the neutral one.</summary>
    private const int Middle = 2, Bands = 5;

    // ---- size -----------------------------------------------------------------

    public WorldSize Size { get; set; } = WorldSize.Medium;

    /// <summary>
    /// Tiles from pole to pole. The world is a band that wraps east–west (see
    /// <see cref="WorldBounds"/>), so this one number fixes the whole planet: it is half
    /// the circumference, and the map holds span² tiles all told.
    ///
    /// Powers of two, and whole numbers, because the wrap is the tile-space move
    /// (x, y) → (x + span, y − span): it has to land on the lattice or the two sides
    /// could not meet.
    /// </summary>
    public int Span => Spans[(int)Size];

    /// <summary>Tiles all the way round, east to east. Twice the span.</summary>
    public int Circumference => Span * 2;

    /// <summary>Tiny to huge. About 65k tiles at the bottom and 17M at the top.</summary>
    private static readonly int[] Spans = { 256, 512, 1024, 2048, 4096 };

    /// <summary>The world's shape, for anything that needs to know where its edges are.</summary>
    public WorldBounds Bounds => new(Span);

    // ---- climate --------------------------------------------------------------

    public WorldTemperature Temperature { get; set; } = WorldTemperature.Temperate;
    public WorldRainfall Rainfall { get; set; } = WorldRainfall.Moderate;

    /// <summary>
    /// Where the temperature dial sits, 0 (icy) to 1 (hot), with the neutral band at 0.5.
    ///
    /// The generator uses this to slide the *ends* of its pole-to-equator temperature
    /// profile rather than to offset the finished field: a hot world has a hotter equator
    /// and milder poles, a cold one freezes the poles and cools the tropics. Shifting the
    /// profile keeps the whole spread of climates on the map, where adding an offset and
    /// clamping would flatten one end of it — a hot world where a third of the land reads
    /// exactly 1.0 and every temperature difference in it has been squashed out.
    /// </summary>
    public float Warmth => (int)Temperature / (float)(Bands - 1);

    /// <summary>Where the rainfall dial sits, 0 (arid) to 1 (lush). As <see cref="Warmth"/>.</summary>
    public float Wetness => (int)Rainfall / (float)(Bands - 1);

    // ---- age ------------------------------------------------------------------

    public PlanetAge Age { get; set; } = PlanetAge.Mature;

    /// <summary>
    /// The exponent land height is raised to, as a fraction of the way from sea level to the
    /// highest ground. Above 1 it pulls the middle of that range down towards the sea —
    /// hills become plains and mountains become hills, which is erosion's whole effect on a
    /// map; below 1 it pushes it up, so ground that would have been a slope stands as
    /// highland.
    ///
    /// It is a curve on the *land fraction* rather than a multiplier on the height for two
    /// reasons: sea level maps to itself, so the coastline stays exactly where it was and
    /// only the relief behind it changes; and the top of the range maps to itself too, so
    /// nothing can push height past its cap and quantize a summit into a flat plateau.
    /// </summary>
    public float ReliefGamma => ReliefGammas[(int)Age];

    /// <summary>How hard mountain ranges are pushed up, as a multiple of the authored uplift.</summary>
    public float MountainUplift => MountainUplifts[(int)Age];

    /// <summary>Young to ancient. The middle band is 1 — the terrain exactly as authored.</summary>
    private static readonly float[] ReliefGammas = { 0.68f, 0.85f, 1.00f, 1.35f, 1.85f };

    /// <summary>
    /// Young to ancient again, and deliberately not symmetric: the uplift already sits close
    /// to the height cap, so the young end is given a modest boost while the old end is free
    /// to take away as much as it likes.
    /// </summary>
    private static readonly float[] MountainUplifts = { 1.30f, 1.15f, 1.00f, 0.75f, 0.50f };

    // ---- stepping -------------------------------------------------------------

    /// <summary>Moves a dial one band, stopping at the ends rather than wrapping.</summary>
    public void StepTemperature(int direction)
        => Temperature = (WorldTemperature)Step((int)Temperature, direction);

    public void StepRainfall(int direction)
        => Rainfall = (WorldRainfall)Step((int)Rainfall, direction);

    public void StepAge(int direction)
        => Age = (PlanetAge)Step((int)Age, direction);

    public void StepSize(int direction)
        => Size = (WorldSize)Step((int)Size, direction);

    /// <summary>The band names, for the settings panel's 3×5 font (which has no lower case).</summary>
    public string TemperatureName => Temperature.ToString().ToUpperInvariant();
    public string RainfallName => Rainfall.ToString().ToUpperInvariant();
    public string AgeName => Age.ToString().ToUpperInvariant();
    public string SizeName => Size.ToString().ToUpperInvariant();

    private static int Step(int band, int direction) => Math.Clamp(band + Math.Sign(direction), 0, Bands - 1);
}
