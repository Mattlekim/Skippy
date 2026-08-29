using Microsoft.Xna.Framework;

namespace Skippy.Core.Worlds;

/// <summary>Biome classification, derived from temperature + moisture (+ height).</summary>
public enum Biome : byte
{
    Ocean,
    ShallowWater,
    Beach,
    Grassland,
    Savanna,
    Desert,
    TemperateForest,
    Taiga,
    Tundra,
    Snow,
    Jungle,
    Swamp,
    Rock,
}

/// <summary>What (if anything) grows on a tile. Chosen from biome + climate.</summary>
public enum Vegetation : byte
{
    None,
    GrassTuft,
    Bush,
    PineTree,
    BroadleafTree,
    PalmTree,
    Cactus,
    Rock,
}

/// <summary>
/// One map cell. A value type so a 32×32 chunk is a single contiguous array with
/// no per-tile heap allocation. All climate fields are normalized to 0..1.
/// </summary>
public struct Tile
{
    /// <summary>Raw terrain height, 0..1 (continuous, before quantization).</summary>
    public float Height;

    /// <summary>Quantized elevation step, 0..<see cref="WorldConstants.MaxLevel"/>.</summary>
    public byte Level;

    /// <summary>Temperature 0..1, already reduced for altitude.</summary>
    public float Temperature;

    /// <summary>Moisture / wetness 0..1.</summary>
    public float Moisture;

    public Biome Biome;
    public Vegetation Vegetation;

    public readonly bool IsWater => Biome is Biome.Ocean or Biome.ShallowWater;

    /// <summary>Temperature expressed in degrees Celsius (from the normalized 0..1 field).</summary>
    public readonly float TemperatureCelsius =>
        WorldConstants.TempMinCelsius
        + Temperature * (WorldConstants.TempMaxCelsius - WorldConstants.TempMinCelsius);
}

/// <summary>Tunable constants for terrain shaping and the isometric look.</summary>
public static class WorldConstants
{
    /// <summary>Number of discrete elevation steps above sea level (AoE2-style stepped terrain).</summary>
    public const int MaxLevel = 15;

    /// <summary>Normalized height of the sea surface. Below this is water.</summary>
    public const float SeaLevel = 0.42f;

    /// <summary>Narrow band just above the sea that renders as beach.</summary>
    public const float BeachBand = 0.03f;

    /// <summary>Above this normalized height, land becomes rock/snow regardless of climate.</summary>
    public const float SnowLine = 0.78f;

    /// <summary>How strongly altitude cools temperature (per unit of height above sea).</summary>
    public const float TemperatureLapse = 0.9f;

    /// <summary>Degrees Celsius that the normalized temperature field maps to (0 → min, 1 → max).</summary>
    public const float TempMinCelsius = -20f;
    public const float TempMaxCelsius = 45f;
}
