using System;

namespace Skippy.Core.Worlds.Mobs;

public enum MobGender : byte
{
    Male,
    Female,
}

/// <summary>Where a mob is in its life, read from its age against its species' own clock.</summary>
public enum LifeStage : byte
{
    Young,
    Adult,
    Old,
}

/// <summary>
/// What a mob is doing with itself this frame, which is what its body answers to: walking
/// costs it, standing still costs it less, and eating or resting pays some of it back.
/// </summary>
public enum MobActivity : byte
{
    /// <summary>Standing about. Still tiring, just slowly.</summary>
    Idle,

    /// <summary>Covering ground.</summary>
    Moving,

    /// <summary>Sleeping or otherwise off its feet — the fastest way back to rested.</summary>
    Resting,

    /// <summary>Eating. Grazing counts as resting too — a sheep with its head down is not walking.</summary>
    Feeding,
}

/// <summary>
/// The things that can be wrong with a mob. Flags, because they stack: a beast can be
/// starving, exhausted and lame all at once, and each is drawing on the same health.
///
/// <see cref="Starving"/>, <see cref="Exhausted"/> and <see cref="Frail"/> are read off the
/// body every frame and clear themselves the moment the mob is fed, rested or (never) young
/// again. <see cref="Injured"/> follows the wounds it has taken. <see cref="Sick"/> is the
/// one nothing here sets — it is there for whatever gives an animal a disease.
/// </summary>
[Flags]
public enum MobAilment : ushort
{
    None = 0,
    Starving = 1 << 0,
    Exhausted = 1 << 1,
    Injured = 1 << 2,
    Sick = 1 << 3,
    Frail = 1 << 4,
}

/// <summary>
/// A mob's body: how hurt it is, how hungry, how tired, how old, and what is wrong with it.
///
/// Everything runs on **world days** rather than real seconds, so a body keeps the same pace
/// against the sun whatever the day length is set to — turn the clock up and a sheep gets
/// hungry faster in real time, but no faster in the day it is living through.
///
/// The rates are all expressed as "days to", which is the only form of this that stays
/// readable: <see cref="DaysToStarve"/> is how long an animal that never eats takes to go
/// from full to starving, not an opaque rate per second. A species sets whichever of them it
/// disagrees with when it builds its body, and leaves the rest.
/// </summary>
public sealed class Vitals
{
    // ---- what the species is like ---------------------------------------------

    public float MaxHealth { get; }

    /// <summary>Days from fed to starving, eating nothing.</summary>
    public float DaysToStarve { get; init; } = 2.5f;

    /// <summary>Days from rested to exhausted, walking the whole time.</summary>
    public float DaysToTire { get; init; } = 1.2f;

    /// <summary>Days of rest to go from exhausted back to fresh.</summary>
    public float DaysToRest { get; init; } = 0.3f;

    /// <summary>Days of feeding to go from starving back to full.</summary>
    public float DaysToFeed { get; init; } = 0.2f;

    /// <summary>Age at which it is grown; below this it is <see cref="LifeStage.Young"/> and still growing.</summary>
    public float MaturityDays { get; init; } = 20f;

    /// <summary>How long the species lives. Old age is the last <see cref="FrailFraction"/> of it.</summary>
    public float LifespanDays { get; init; } = 240f;

    /// <summary>Days of unbroken starvation it takes to kill a mob outright, from full health.</summary>
    public float DaysToStarveToDeath { get; init; } = 4f;

    /// <summary>Days of being fed and rested it takes to heal from nothing back to full.</summary>
    public float DaysToHeal { get; init; } = 3f;

    // ---- what this particular animal is ---------------------------------------

    public MobGender Gender { get; }

    public float Health { get; private set; }

    /// <summary>0 fed, 1 starving.</summary>
    public float Hunger { get; private set; }

    /// <summary>0 fresh, 1 dead on its feet.</summary>
    public float Tiredness { get; private set; }

    /// <summary>Age in world days — one per turn of the sun, however long that takes in real time.</summary>
    public float AgeDays { get; private set; }

    public MobAilment Ailments { get; private set; }

    public Vitals(float maxHealth, MobGender gender, float ageDays)
    {
        MaxHealth = maxHealth;
        Health = maxHealth;
        Gender = gender;
        AgeDays = ageDays;
    }

    // ---- thresholds -----------------------------------------------------------

    /// <summary>Hunger at which an animal counts as starving, and the level it must eat back down to.</summary>
    private const float StarvingAt = 0.85f, FedEnough = 0.6f;

    /// <summary>Tiredness at which it counts as exhausted, and the level it must sleep back down to.</summary>
    private const float ExhaustedAt = 0.95f, RestedEnough = 0.8f;

    /// <summary>Health below this share of its maximum reads as an injury, and above it as healed.</summary>
    private const float InjuredBelow = 0.5f;

    /// <summary>The share of its lifespan after which a mob is old, and its body starts to fail.</summary>
    private const float FrailFraction = 0.85f;

    /// <summary>How much slower standing about tires a mob than walking does.</summary>
    private const float IdleTireRate = 0.3f;

    /// <summary>Health each of the wasting ailments costs per day, as a share of what starving costs.</summary>
    private const float ExhaustionToll = 0.35f, FrailtyToll = 0.25f;

    // ---- reading it -----------------------------------------------------------

    public bool IsDead => Health <= 0f;
    public bool IsAlive => Health > 0f;

    public bool Has(MobAilment ailment) => (Ailments & ailment) != 0;

    public LifeStage Stage =>
        AgeDays < MaturityDays ? LifeStage.Young
        : AgeDays >= LifespanDays * FrailFraction ? LifeStage.Old
        : LifeStage.Adult;

    /// <summary>
    /// How big this mob is drawn, as a share of its grown size: a young one starts at
    /// <see cref="NewbornScale"/> and grows into itself by <see cref="MaturityDays"/>. Nothing
    /// shrinks with old age — a worn-out sheep is still a sheep-sized sheep.
    /// </summary>
    public float Growth => AgeDays >= MaturityDays
        ? 1f
        : NewbornScale + (1f - NewbornScale) * (AgeDays / MaturityDays);

    private const float NewbornScale = 0.55f;

    /// <summary>Health as 0..1, for anything that wants to draw or compare it.</summary>
    public float HealthFraction => MaxHealth <= 0f ? 0f : Health / MaxHealth;

    // ---- living ---------------------------------------------------------------

    /// <summary>
    /// One step of being alive, in world days. Hunger and age only ever climb; what the mob
    /// is doing decides how fast it tires and whether it is paying any of that back.
    /// </summary>
    public void Advance(float days, MobActivity activity)
    {
        if (days <= 0f || IsDead)
            return;

        AgeDays += days;

        // Hunger runs whatever it is doing; feeding pays it back faster than it accrues.
        Hunger += days / DaysToStarve;
        if (activity == MobActivity.Feeding)
            Hunger -= days / DaysToFeed;

        // Tiredness likewise: walking costs the full rate, standing about a fraction of it,
        // and anything with its head down or its eyes shut is paying it back instead.
        Tiredness += days / DaysToTire * (activity == MobActivity.Moving ? 1f : IdleTireRate);
        if (activity is MobActivity.Resting or MobActivity.Feeding)
            Tiredness -= days / DaysToRest;

        Hunger = Clamp01(Hunger);
        Tiredness = Clamp01(Tiredness);

        UpdateAilments();
        UpdateHealth(days);
    }

    /// <summary>
    /// Reads the wasting ailments off the body. They are recomputed rather than remembered,
    /// so an animal that eats stops starving the moment it has eaten enough — with a gap
    /// between the level that starts an ailment and the level that ends it, or a mob sitting
    /// exactly on the line would flicker in and out of it every frame.
    /// </summary>
    private void UpdateAilments()
    {
        Set(MobAilment.Starving, Hunger >= StarvingAt, Hunger < FedEnough);
        Set(MobAilment.Exhausted, Tiredness >= ExhaustedAt, Tiredness < RestedEnough);
        Set(MobAilment.Injured, HealthFraction < InjuredBelow, HealthFraction >= InjuredBelow);
        Set(MobAilment.Frail, Stage == LifeStage.Old, Stage != LifeStage.Old);
    }

    /// <summary>Raises an ailment when <paramref name="onset"/>, drops it when <paramref name="recovered"/>, otherwise leaves it as it was.</summary>
    private void Set(MobAilment ailment, bool onset, bool recovered)
    {
        if (onset) Ailments |= ailment;
        else if (recovered) Ailments &= ~ailment;
    }

    /// <summary>
    /// What the ailments cost, or what a body with none of them wins back. Starvation is the
    /// killer — <see cref="DaysToStarveToDeath"/> of it and the mob is gone — with exhaustion
    /// and old age wearing at it more slowly, and all of them stacking.
    /// </summary>
    private void UpdateHealth(float days)
    {
        float starveToll = MaxHealth / DaysToStarveToDeath;
        float toll = 0f;
        if (Has(MobAilment.Starving)) toll += starveToll;
        if (Has(MobAilment.Exhausted)) toll += starveToll * ExhaustionToll;
        if (Has(MobAilment.Frail)) toll += starveToll * FrailtyToll;

        // Nothing heals while it is still going wrong, and nothing heals a disease — that is
        // for whatever gave the animal one to cure.
        if (toll > 0f || Has(MobAilment.Sick))
            Health = MathF.Max(0f, Health - toll * days);
        else
            Health = MathF.Min(MaxHealth, Health + MaxHealth / DaysToHeal * days);
    }

    // ---- being acted on -------------------------------------------------------

    public void Damage(float amount)
    {
        if (amount <= 0f) return;
        Health = MathF.Max(0f, Health - amount);
        UpdateAilments();
    }

    public void Heal(float amount)
    {
        if (amount <= 0f) return;
        Health = MathF.Min(MaxHealth, Health + amount);
        UpdateAilments();
    }

    /// <summary>Gives an ailment nothing here raises on its own — a disease, say.</summary>
    public void Afflict(MobAilment ailment) => Ailments |= ailment;

    /// <summary>Clears one. The wasting ailments will simply come back if the body still has them.</summary>
    public void Cure(MobAilment ailment) => Ailments &= ~ailment;

    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
}
