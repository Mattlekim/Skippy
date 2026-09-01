namespace Skippy.Core.Worlds.Items;

/// <summary>
/// What kind of food a <see cref="WorldItem"/> is, if it is food at all. It is the
/// category rather than the plant: a berry off a bush and a berry off a tree are the
/// same <see cref="Fruit"/> to the animal that eats them, and carry the same nutrition
/// (see <see cref="FoodTypes"/>). <see cref="None"/> is the inedible case — bare wood,
/// stone — and is what a <see cref="Material"/> always is.
/// </summary>
public enum FoodType : byte
{
    /// <summary>Not food: nothing to be gained from eating it.</summary>
    None,

    /// <summary>Leaf and blade — grass, flowers, the greenery of a bush. Bulky, filling, thin on energy.</summary>
    Vegetation,

    /// <summary>Berries and fruit. Richer and sweeter than greenery, and it stays with you longer.</summary>
    Fruit,
}

/// <summary>
/// What one unit of a food is worth to the animal that eats it. The three nutrients are
/// what the food gives; <see cref="FullnessDays"/> is how long that unit keeps hunger off,
/// so a rich food both feeds better and lasts longer than the same weight of greenery.
///
/// The nutrients are held apart rather than rolled into one "calories" number so a diet
/// can later be judged on more than bulk — an animal living on grass alone is full but
/// short of protein, which grazers make up by eating a great deal of it.
/// </summary>
public readonly struct FoodProfile
{
    /// <summary>Raw energy released by the food — what powers moving about and staying warm.</summary>
    public readonly float Energy;

    /// <summary>Protein: what an animal is built and repaired from. Scarce in greenery, better in fruit.</summary>
    public readonly float Protein;

    /// <summary>Carbohydrate: quick energy, plentiful in leaf and sugar-rich fruit alike.</summary>
    public readonly float Carbs;

    /// <summary>How long one unit of this food holds hunger off, in world days.</summary>
    public readonly float FullnessDays;

    public FoodProfile(float energy, float protein, float carbs, float fullnessDays)
    {
        Energy = energy;
        Protein = protein;
        Carbs = carbs;
        FullnessDays = fullnessDays;
    }

    /// <summary>Nothing gained and nothing kept — the profile of <see cref="FoodType.None"/>.</summary>
    public static readonly FoodProfile Nothing = new(0f, 0f, 0f, 0f);
}

/// <summary>
/// The one place a <see cref="FoodType"/>'s nutrition lives. A food carries its worth here
/// rather than on every item that happens to be made of it, so all the grass in the world
/// is fed from a single set of numbers and changing what greenery is worth is a one-line
/// change.
/// </summary>
public static class FoodTypes
{
    /// <summary>
    /// What each food is worth, per unit eaten. Greenery is filling but thin — a lot of
    /// bulk for little protein, and it passes quickly; fruit is the richer meal on every
    /// count and sits longer. <see cref="FoodType.None"/> is worth nothing at all.
    /// </summary>
    public static FoodProfile Profile(FoodType food) => food switch
    {
        FoodType.Vegetation => new FoodProfile(energy: 3f, protein: 1f, carbs: 4f, fullnessDays: 0.15f),
        FoodType.Fruit => new FoodProfile(energy: 6f, protein: 1.5f, carbs: 8f, fullnessDays: 0.30f),
        _ => FoodProfile.Nothing,
    };
}
