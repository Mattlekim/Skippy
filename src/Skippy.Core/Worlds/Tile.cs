using Microsoft.Xna.Framework;

namespace Skippy.Core.Worlds;

/// <summary>
/// Biome classification, derived from temperature + moisture (+ height).
///
/// There is no snow biome: snow is weather, not a place. It lies wherever the ground
/// is below freezing (see the snow constants in <c>WorldRenderer</c>), so a tundra, a
/// taiga or a bare mountain top all wear it when they are cold enough — and the same
/// ground carries none of it in a warm year's climate band.
/// </summary>
public enum Biome : byte
{
    Ocean,
    ShallowWater,

    /// <summary>Water with land all the way round it, and little enough of it to walk around — see <see cref="Generation.LakeFinder"/>.</summary>
    Lake,

    Beach,
    Grassland,
    Savanna,
    Desert,
    TemperateForest,
    Taiga,
    Tundra,
    Jungle,
    Swamp,
    Rock,
}

public static class BiomeNames
{
    /// <summary>
    /// Short upper-case label for the debug read-out. Upper case because the read-out's
    /// 3×5 font has no lower case, and short because the two-word names do not fit
    /// beside the numbers.
    /// </summary>
    public static string Name(this Biome biome) => biome switch
    {
        Biome.Ocean => "OCEAN",
        Biome.ShallowWater => "SHALLOWS",
        Biome.Lake => "LAKE",
        Biome.Beach => "BEACH",
        Biome.Grassland => "GRASSLAND",
        Biome.Savanna => "SAVANNA",
        Biome.Desert => "DESERT",
        Biome.TemperateForest => "FOREST",
        Biome.Taiga => "TAIGA",
        Biome.Tundra => "TUNDRA",
        Biome.Jungle => "JUNGLE",
        Biome.Swamp => "SWAMP",
        _ => "ROCK",
    };
}

/// <summary>
/// Which tree owns a wood — the one that has the canopy, with a second kind
/// growing through it in smaller numbers.
///
/// A real wood is not an even mix of everything that will grow there: one species
/// takes the light and the rest live under it, which is why a pine wood a mile from
/// an oak wood looks nothing like it. The name is the tree that dominates; "Mixed"
/// means the second kind is close behind rather than the odd stranger among them.
/// </summary>
public enum ForestKind : byte
{
    Conifer,
    ConiferMixed,
    BroadleafMixed,
    Broadleaf,
    Palm,
    PalmMixed,
}

public static class ForestNames
{
    /// <summary>Short upper-case label for the debug read-out, as <see cref="BiomeNames"/>.</summary>
    public static string Name(this ForestKind kind) => kind switch
    {
        ForestKind.Conifer => "EVERGREEN",
        ForestKind.ConiferMixed => "EVERGREEN-MIX",
        ForestKind.BroadleafMixed => "BROADLEAF-MIX",
        ForestKind.Broadleaf => "BROADLEAF",
        ForestKind.Palm => "PALM",
        _ => "PALM-MIX",
    };
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

    /// <summary>Which tree owns the wood this tile stands in — see <see cref="Generation.Forests"/>.</summary>
    public ForestKind Forest;

    /// <summary>
    /// Which of its kind's painted trees that wood is grown from, 0..1. A wood is not
    /// only one kind of tree but, mostly, one *tree*: this picks its form out of the art
    /// for that kind, so the pines in one valley are all the same pine and the next
    /// valley's are all a different one.
    /// </summary>
    public float ForestVariant;

    /// <summary>
    /// This tile lies past a pole, outside the world (see <see cref="WorldBounds"/>).
    /// It carries real terrain all the same — the last row of ground inside the world has
    /// to be shaped and lit against genuine neighbours rather than against a wall of
    /// nothing — but it is never drawn and nothing may walk onto it. What shows there is
    /// the black beyond the edge of the map.
    ///
    /// False on a default tile, which is what an unloaded lookup returns, so code that
    /// only cares about loaded ground reads the same as it always did.
    /// </summary>
    public bool IsVoid;

    public readonly bool IsWater => Biome is Biome.Ocean or Biome.ShallowWater or Biome.Lake;

    /// <summary>Water that is part of the sea, rather than a lake shut off from it.</summary>
    public readonly bool IsSea => Biome is Biome.Ocean or Biome.ShallowWater;

    /// <summary>Temperature expressed in degrees Celsius (from the normalized 0..1 field).</summary>
    public readonly float TemperatureCelsius => WorldConstants.Celsius(Temperature);
}

/// <summary>Tunable constants for terrain shaping and the isometric look.</summary>
public static class WorldConstants
{
    /// <summary>
    /// Number of discrete elevation steps above sea level. Set high so the steps are
    /// fine and the terrain reads as smooth slopes rather than a few tall shelves; the
    /// per-level height step (Iso.HeightStep) is scaled down to match, so the tallest
    /// peak reaches the same height as before — there are just far more, smaller steps
    /// on the way up.
    /// </summary>
    public const int MaxLevel = 60;

    /// <summary>Normalized height of the sea surface. Below this is water.</summary>
    public const float SeaLevel = 0.42f;

    /// <summary>Narrow band just above the sea that renders as beach.</summary>
    public const float BeachBand = 0.03f;

    /// <summary>Above this normalized height, land is bare rock regardless of climate.</summary>
    public const float RockLine = 0.78f;

    /// <summary>
    /// How strongly altitude cools temperature (per unit of height above sea).
    ///
    /// Kept gentle on purpose. At 0.9 the cooling was an order of magnitude larger
    /// than the temperature field's own variation, so altitude decided the climate
    /// on its own: a third of all land clamped to freezing and the map was nothing
    /// but taiga and tundra. It is still what makes a peak colder than the country
    /// it stands in, and so what puts snow on it.
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

    /// <summary>The normalized 0..1 temperature field in degrees Celsius.</summary>
    public static float Celsius(float temperature)
        => TempMinCelsius + temperature * (TempMaxCelsius - TempMinCelsius);

    /// <summary>The temperature field value that a temperature in degrees Celsius sits at.</summary>
    public static float Normalized(float celsius)
        => (celsius - TempMinCelsius) / (TempMaxCelsius - TempMinCelsius);
}
