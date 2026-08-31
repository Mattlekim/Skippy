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
/// The dials the whole world is generated from: how warm it runs, how wet it runs, and how
/// old â€” and so how worn down â€” it is. Each is a band either side of a neutral middle, and
/// each moves the entire map rather than adding a feature to it, so what changes is the
/// *kind* of world the same seed comes back as.
///
/// They are read live by <see cref="DefaultWorldGenerator"/>, which holds this very object:
/// turn a dial, call <see cref="World.Regenerate"/>, and the next chunk built uses it.
/// </summary>
public sealed class WorldSettings
{
    /// <summary>The band each dial sits at; <see cref="Middle"/> is the neutral one.</summary>
    private const int Middle = 2, Bands = 5;

    // ---- climate --------------------------------------------------------------

    /// <summary>
    /// How far one band shifts the raw climate noise. The noise has a standard deviation of
    /// <see cref="DefaultWorldGenerator.ClimateSigma"/>, so half of that per band moves the
    /// *typical* tile a good way up the scale without any band being so far out that the map
    /// loses its variety: at the ends the median tile sits around 0.15 or 0.85.
    ///
    /// The bias goes onto the *raw* noise, before it is spread out into a 0..1 field, not
    /// onto the finished field. Biasing the finished field and clamping would flatten one
    /// end of the map to a single value â€” a hot world where a third of the land reads
    /// exactly 1.0 and every temperature difference in it has been squashed out. Going in
    /// before the logistic slides the whole distribution instead: the map keeps its warm and
    /// cool country, and the whole of it is warmer.
    /// </summary>
    private const float ClimateBandStep = 0.05f;

    public WorldTemperature Temperature { get; set; } = WorldTemperature.Temperate;
    public WorldRainfall Rainfall { get; set; } = WorldRainfall.Moderate;

    /// <summary>Shift added to the raw temperature noise; negative is colder.</summary>
    public float TemperatureBias => ((int)Temperature - Middle) * ClimateBandStep;

    /// <summary>Shift added to the raw moisture noise; negative is drier.</summary>
    public float MoistureBias => ((int)Rainfall - Middle) * ClimateBandStep;

    // ---- age ------------------------------------------------------------------

    public PlanetAge Age { get; set; } = PlanetAge.Mature;

    /// <summary>
    /// The exponent land height is raised to, as a fraction of the way from sea level to the
    /// highest ground. Above 1 it pulls the middle of that range down towards the sea â€”
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

    /// <summary>Young to ancient. The middle band is 1 â€” the terrain exactly as authored.</summary>
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

    /// <summary>The band names, for the settings panel's 3Ã—5 font (which has no lower case).</summary>
    public string TemperatureName => Temperature.ToString().ToUpperInvariant();
    public string RainfallName => Rainfall.ToString().ToUpperInvariant();
    public string AgeName => Age.ToString().ToUpperInvariant();

    private static int Step(int band, int direction) => Math.Clamp(band + Math.Sign(direction), 0, Bands - 1);
}



