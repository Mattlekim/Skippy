namespace Skippy.Core.Worlds.Items;

/// <summary>
/// The bridge between the art a plant is drawn from (<see cref="PlantKind"/>) and what it
/// <em>is</em> in the world (<see cref="WorldItem"/>). Everything the world scatters comes
/// through here: a grass tuft and a clump of flowers become grazeable
/// <see cref="Vegetation"/>, a bush likewise; the trees and the rocks become inedible
/// <see cref="Material"/>.
///
/// It is the one place that decision is made, so the mesher (which knows only
/// <see cref="PlantKind"/>) and the animals (which want to know whether a thing is worth
/// eating) never have to spell out the mapping themselves.
/// </summary>
public static class WorldItems
{
    /// <summary>How much food a full patch of each greenery holds. Grass is the staple; a
    /// bush carries far more than a wisp of grass, flowers rather less.</summary>
    public const float GrassMaxFood = 6f;
    public const float FlowersMaxFood = 3f;
    public const float BushMaxFood = 10f;

    /// <summary>
    /// A fresh <see cref="WorldItem"/> for one plant of the given <paramref name="kind"/>, or
    /// <c>null</c> for <see cref="PlantKind.None"/> (bare ground grows nothing to hold). The
    /// vegetation kinds come back full of food; the materials carry none.
    /// </summary>
    public static WorldItem? Create(PlantKind kind) => kind switch
    {
        PlantKind.GrassTuft => new Vegetation(WorldItemType.Grass, GrassMaxFood),
        PlantKind.Flowers => new Vegetation(WorldItemType.Flowers, FlowersMaxFood),
        PlantKind.Bush => new Vegetation(WorldItemType.Bush, BushMaxFood),
        PlantKind.PineTree or PlantKind.BroadleafTree or PlantKind.PalmTree => new Material(WorldItemType.Tree),
        PlantKind.Rock => new Material(WorldItemType.Rock),
        _ => null,
    };

    /// <summary>Which <see cref="WorldItemType"/> a plant kind is, without building an item for it.</summary>
    public static WorldItemType TypeOf(PlantKind kind) => kind switch
    {
        PlantKind.GrassTuft => WorldItemType.Grass,
        PlantKind.Flowers => WorldItemType.Flowers,
        PlantKind.Bush => WorldItemType.Bush,
        PlantKind.PineTree or PlantKind.BroadleafTree or PlantKind.PalmTree => WorldItemType.Tree,
        _ => WorldItemType.Rock,
    };

    /// <summary>Is this kind grazeable greenery (grass, flowers, a bush) rather than material?</summary>
    public static bool IsVegetation(PlantKind kind)
        => kind is PlantKind.GrassTuft or PlantKind.Flowers or PlantKind.Bush;

    /// <summary>Is this kind inedible material — a tree or a rock?</summary>
    public static bool IsMaterial(PlantKind kind)
        => kind is PlantKind.PineTree or PlantKind.BroadleafTree or PlantKind.PalmTree or PlantKind.Rock;
}
