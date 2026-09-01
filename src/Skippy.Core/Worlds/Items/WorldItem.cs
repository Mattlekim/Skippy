namespace Skippy.Core.Worlds.Items;

/// <summary>
/// What a <see cref="WorldItem"/> is, in the sense the player and the world care about:
/// grass, flowers, a bush, a tree, a rock. It is deliberately coarse — one entry per kind
/// of thing rather than per painted form — because it is the handle everything else hangs
/// off (what an animal will graze, what a shadow is cast by, what a HUD reads out), and
/// none of those want to know which of six painted grasses a tuft happens to be.
///
/// It is the counterpart to <see cref="PlantKind"/>, which is about the <em>art</em> a
/// thing is drawn from; this is about <em>what it is</em>. The two are mapped in
/// <see cref="WorldItems"/>.
/// </summary>
public enum WorldItemType : byte
{
    Grass,
    Flowers,
    Bush,
    Tree,
    Rock,
}

public static class WorldItemTypeNames
{
    /// <summary>Short upper-case label for the debug read-out, as the biome and forest names are.</summary>
    public static string Name(this WorldItemType type) => type switch
    {
        WorldItemType.Grass => "GRASS",
        WorldItemType.Flowers => "FLOWERS",
        WorldItemType.Bush => "BUSH",
        WorldItemType.Tree => "TREE",
        _ => "ROCK",
    };
}

/// <summary>
/// One thing that stands in the world and is not alive: the plants an animal grazes and
/// the trees and rocks it walks between. It is the shared root of the two ways a thing
/// standing on the ground matters — as food you can eat (<see cref="Vegetation"/>) or as
/// material you cannot (<see cref="Material"/>) — so anything that handles "the stuff on
/// the ground" can hold a <see cref="WorldItem"/> and ask it what it is and whether it is
/// worth eating without caring which of the two it turned out to be.
///
/// A world item is a plain behaviour object, like a <see cref="Mobs.Mob"/> and unlike a
/// <see cref="Tile"/>: it knows nothing about the atlas it is drawn from or the mesh it
/// ends up in. Where its art comes from is <see cref="PlantKind"/>'s business, mapped in
/// <see cref="WorldItems"/>.
/// </summary>
public abstract class WorldItem
{
    /// <summary>What this item is — grass, a tree, a rock, and so on.</summary>
    public abstract WorldItemType Type { get; }

    /// <summary>
    /// The food this item provides, or <see cref="FoodType.None"/> if it is not food. A
    /// <see cref="Material"/> is always <see cref="FoodType.None"/>; a
    /// <see cref="Vegetation"/> is whatever greenery or fruit it grows.
    /// </summary>
    public abstract FoodType Food { get; }

    /// <summary>Is there anything to be gained from eating this?</summary>
    public bool IsEdible => Food != FoodType.None;

    /// <summary>What a unit of this item's food is worth (see <see cref="FoodTypes"/>).</summary>
    public FoodProfile Nutrition => FoodTypes.Profile(Food);
}
