using System;
using Microsoft.Xna.Framework;

namespace Skippy.Core.Worlds.Mobs;

/// <summary>
/// A sheep: it grazes where it stands, and every so often ambles a short way to
/// somewhere else in its patch.
///
/// The wander is deliberately a graze-walk-graze cycle rather than a constant drift.
/// A flock that never stops reads as a crowd being pushed about; one that stands
/// still most of the time and then moves a few tiles reads as animals with their own
/// minds — and because each sheep rolls its own rest out of its own RNG, they never
/// set off together.
/// </summary>
public sealed class Sheep : Mob
{
    public override MobKind Kind => MobKind.Sheep;

    /// <summary>
    /// An unhurried amble. Slow in tiles per second because a sheep is a small thing on a
    /// big tile: at anything brisker it skates across the ground rather than walking it.
    /// </summary>
    protected override float WalkSpeed => 0.16f;

    /// <summary>
    /// How far from where it was put down a sheep will wander. Targets are picked around
    /// that fixed spot rather than around wherever it currently is, so a flock stays a
    /// flock: a random walk with no anchor drifts off over the horizon given long enough.
    /// </summary>
    private const float Range = 3f;

    /// <summary>Seconds spent grazing between walks.</summary>
    private const float MinRest = 1.5f, MaxRest = 6f;

    /// <summary>Spots tried before a sheep accepts it has nowhere to go this time.</summary>
    private const int TargetTries = 8;

    /// <summary>
    /// How much forage a grazing sheep crops per world day. Set so a sheep working a tuft of
    /// grass runs it down over a good part of a day — long enough that a patch is worth
    /// stopping at, short enough that a flock left on one spot eats it bare and has to spread
    /// out, which is what makes them drift across a meadow rather than huddle on one tile.
    /// </summary>
    private const float GrazeRatePerDay = 12f;

    /// <summary>
    /// A sheep's body. It is a grazing animal built to eat almost constantly, so it feeds
    /// back far faster than it starves — a flock left alone stays fed, and hunger only
    /// becomes a problem for one that cannot get its head down.
    /// </summary>
    private const float SheepHealth = 18f;
    private const float SheepMaturityDays = 20f, SheepLifespanDays = 240f;

    private readonly Vector2 _home;
    private Vector2 _target;
    private float _rest;

    public Sheep(Vector2 position, int seed) : base(position, seed)
    {
        _home = position;
        _target = position;

        // Stagger the flock: dropped in together, they would otherwise all head off on
        // the very same frame and move as one block.
        _rest = Rand(0f, MaxRest);
    }

    /// <summary>
    /// A flock turns up as a flock: mostly grown sheep of assorted ages, with the odd lamb
    /// among them — which is drawn smaller, since size follows growth.
    /// </summary>
    protected override Vitals CreateVitals(MobGender gender) => new(SheepHealth, gender,
        ageDays: Rand(SheepMaturityDays * 0.25f, SheepLifespanDays * 0.6f))
    {
        MaturityDays = SheepMaturityDays,
        LifespanDays = SheepLifespanDays,
        DaysToStarve = 2.5f,
        DaysToFeed = 0.2f,
        DaysToTire = 1.2f,
        DaysToRest = 0.3f,
    };

    protected override void Think(World world, float dt)
    {
        if (_rest > 0f)
        {
            // A stopped sheep is a grazing sheep: this is where it eats and gets its rest,
            // which is why a flock left to itself never goes hungry.
            Activity = MobActivity.Feeding;

            // ...and grazing actually eats the greenery underfoot: it takes a bite from the
            // tile's forage, which runs the patch down as it goes (see WorldForage). On ground
            // it has already cropped bare there is nothing left to take, so it gives up the
            // spot early and moves on to look for better rather than standing on empty.
            int tx = (int)MathF.Floor(Position.X), ty = (int)MathF.Floor(Position.Y);
            if (world.TryGetTile(tx, ty, out Tile here))
            {
                float days = dt / MathF.Max(1f, world.Sky.DayLength);
                float eaten = world.Forage.Graze(tx, ty, here.Vegetation, GrazeRatePerDay * days);
                if (eaten <= 0f && world.Forage.Fullness(tx, ty) <= 0f)
                    _rest = MathF.Min(_rest, Rand(0.2f, 0.6f));
            }

            _rest -= dt;
            if (_rest <= 0f)
                PickTarget(world);
            return;
        }

        switch (WalkToward(world, _target, dt))
        {
            // A tired or hungry sheep stops for longer, so a flock that has been on the move
            // settles down to graze rather than trailing back and forth on empty.
            case MoveResult.Arrived:
                _rest = Rand(MinRest, MaxRest) * (1f + Vitals.Tiredness + Vitals.Hunger);
                break;

            // Walked into the water's edge, a cliff or the edge of the loaded map. Pause
            // briefly and pick somewhere else — the short rest is what stops a penned-in
            // sheep hammering the pathing every frame.
            case MoveResult.Blocked:
                _rest = Rand(0.3f, 1.2f);
                break;
        }
    }

    /// <summary>Picks somewhere in the patch it can actually get to, or gives up for a moment.</summary>
    private void PickTarget(World world)
    {
        for (int i = 0; i < TargetTries; i++)
        {
            float angle = Rand(0f, MathHelper.TwoPi);

            // sqrt spreads the picks evenly over the disc; without it they bunch up
            // around the middle and the sheep never uses the outside of its patch.
            float radius = Range * MathF.Sqrt(Rand(0f, 1f));
            Vector2 candidate = _home + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;

            if (CanStand(world, candidate))
            {
                _target = candidate;
                return;
            }
        }

        // Hemmed in — water all round, or the ground out there simply isn't loaded yet.
        // Stay put and try again shortly.
        _target = Position;
        _rest = Rand(1f, 2f);
    }
}
