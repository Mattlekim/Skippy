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

    /// <summary>Vegetation clumping field 0..1 — drives how many plants a tile scatters.</summary>
    public float VegetationDensity;

    /// <summary>
    /// Fine-grained surface-detail field 0..1, varying over a few tiles rather than
    /// the hundreds the climate fields take. Used for things that should look
    /// patchy at close range — snow lying in drifts across tundra, for one.
    /// </summary>
    public float Patch;

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

    /// <summary>
    /// How wide a river channel is, in tiles. Mutable so it can be tuned in game
    /// (`,` / `.`); chunks already generated keep the width they were carved at
    /// until the world is regenerated.
    /// </summary>
    public static float RiverWidth = 2.5f;

    public const float MinRiverWidth = 1f;
    public const float MaxRiverWidth = 12f;

    /// <summary>Above this normalized height, land becomes rock/snow regardless of climate.</summary>
    public const float SnowLine = 0.78f;

    /// <summary>
    /// How strongly altitude cools temperature (per unit of height above sea).
    ///
    /// Kept gentle on purpose. At 0.9 the cooling was an order of magnitude larger
    /// than the temperature field's own variation, so altitude decided the climate
    /// on its own: a third of all land clamped to freezing and the map was nothing
    /// but taiga and tundra. Snow-capped peaks don't depend on this anyway —
    /// <see cref="SnowLine"/> caps high ground regardless of climate.
    /// </summary>
    public const float TemperatureLapse = 0.25f;

    /// <summary>
    /// Degrees Celsius that the normalized temperature field maps to (0 → min, 1 → max).
    ///
    /// The floor is deliberately mild. Temperature is spread evenly across 0..1, so
    /// the bottom of this range is as common as any other slice of it: at the old
    /// −20 °C floor, a quarter of all land sat below −5 °C. Lifting the floor makes
    /// hard cold rare (a few percent of land) without touching the climate bands, so
    /// tundra and taiga keep their share of the map — they simply aren't arctic.
    /// </summary>
    public const float TempMinCelsius = -6f;
    public const float TempMaxCelsius = 45f;
}
