namespace Skippy.Core.Worlds.Items;

/// <summary>
/// A world item that is not food: a tree or a rock. Material is the stuff of the world an
/// animal walks between rather than eats — structure and, in time, something to be
/// harvested and built from. It is never edible, so its <see cref="Food"/> is always
/// <see cref="FoodType.None"/> and there is no food to run down: a rock is a rock however
/// long you look at it.
///
/// A living bush is <em>not</em> material — it is greenery, and so a
/// <see cref="Vegetation"/> — even though it stands about like a tree does; the split is
/// by whether a thing can be eaten, not by how big it is.
/// </summary>
public sealed class Material : WorldItem
{
    public override WorldItemType Type { get; }

    /// <summary>Material is never food.</summary>
    public override FoodType Food => FoodType.None;

    /// <param name="type">Which material this is — <see cref="WorldItemType.Tree"/> or <see cref="WorldItemType.Rock"/>.</param>
    public Material(WorldItemType type) => Type = type;
}
