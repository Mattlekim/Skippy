using System;

namespace Skippy.Core.Worlds.Items;

/// <summary>
/// A world item that can be eaten: grass, flowers, a bush. Vegetation is the food side of
/// <see cref="WorldItem"/> — the greenery an animal grazes down and that grows back when
/// left alone.
///
/// Unlike a <see cref="Material"/>, a patch of vegetation holds a finite amount of food
/// and remembers how much of it is left. <see cref="Consume"/> takes a mouthful and
/// returns how much was actually there to take; <see cref="Regrow"/> puts it back over
/// time. That is what lets a flock eat a meadow bare and move on, and the meadow come back
/// once they have — the "max food / food remaining" an animal reads before deciding a
/// patch is worth stopping at.
/// </summary>
public class Vegetation : WorldItem
{
    public override WorldItemType Type { get; }

    /// <summary>The food this greenery grows — <see cref="FoodType.Vegetation"/> for leaf and blade.</summary>
    public override FoodType Food { get; }

    /// <summary>The most food this patch holds when fully grown, in food units.</summary>
    public float MaxFood { get; }

    /// <summary>How much food is left to graze right now, 0..<see cref="MaxFood"/>.</summary>
    public float FoodRemaining { get; private set; }

    /// <summary>Grazed to nothing — there is no point an animal stopping here until it grows back.</summary>
    public bool IsDepleted => FoodRemaining <= 0f;

    /// <summary>How full this patch is, 0 (bare) to 1 (untouched) — what an animal weighs when choosing where to feed.</summary>
    public float Fullness => MaxFood <= 0f ? 0f : FoodRemaining / MaxFood;

    /// <param name="type">Which greenery this is — grass, flowers or a bush.</param>
    /// <param name="maxFood">How much food it holds full-grown; it starts full.</param>
    /// <param name="food">The food it provides; greenery by default.</param>
    public Vegetation(WorldItemType type, float maxFood, FoodType food = FoodType.Vegetation)
    {
        Type = type;
        Food = food;
        MaxFood = MathF.Max(0f, maxFood);
        FoodRemaining = MaxFood;
    }

    /// <summary>
    /// Takes up to <paramref name="amount"/> of food from the patch and returns how much
    /// was actually there to take — less than asked once the patch is nearly bare, nothing
    /// at all once it is. The caller feeds the animal by exactly what it gets back, so two
    /// mouths at the same patch can never eat more food out of it than it held.
    /// </summary>
    public float Consume(float amount)
    {
        if (amount <= 0f)
            return 0f;

        float eaten = MathF.Min(amount, FoodRemaining);
        FoodRemaining -= eaten;
        return eaten;
    }

    /// <summary>
    /// Grows food back at <paramref name="perDay"/> units a day over <paramref name="days"/>
    /// days, never past <see cref="MaxFood"/>. Called as the world turns so a meadow left
    /// alone fills back in while one under a flock stays cropped.
    /// </summary>
    public void Regrow(float perDay, float days)
    {
        if (FoodRemaining >= MaxFood || perDay <= 0f || days <= 0f)
            return;

        FoodRemaining = MathF.Min(MaxFood, FoodRemaining + perDay * days);
    }
}
