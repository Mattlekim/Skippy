using System;
using Microsoft.Xna.Framework;

namespace Skippy.Core.Worlds.Mobs;

/// <summary>
/// Which art a mob is drawn with. The renderer switches on this exactly the way it
/// switches on <see cref="PlantKind"/> for plants, so a mob class stays pure
/// behaviour and knows nothing about the atlas it ends up in.
/// </summary>
public enum MobKind : byte
{
    Sheep,
}

/// <summary>How a step of walking went — the brain reads it to decide what to do next.</summary>
public enum MoveResult
{
    /// <summary>Ground was covered this frame; the target is still ahead.</summary>
    Moving,

    /// <summary>Close enough to the target to call it reached.</summary>
    Arrived,

    /// <summary>Water, a cliff or unloaded ground every way it tried. It stood still.</summary>
    Blocked,
}

/// <summary>
/// A living thing that stands somewhere on the map and walks about on it.
///
/// A mob is a point in fractional world-tile space, not a tile occupant: it walks in
/// a straight line at its own speed, and the tile grid only ever comes up as a
/// question of what it may step onto (see <see cref="CanStand"/>). That keeps
/// movement smooth however big the tiles are drawn, and it is why nothing here
/// touches iso pixels — where a mob lands on screen is the renderer's business,
/// worked out from the ground under it every frame.
///
/// Subclasses supply how fast they walk and what they want (<see cref="Think"/>);
/// this class supplies the walking itself, the facing and the walk cycle.
/// </summary>
public abstract class Mob
{
    /// <summary>Where it stands, in fractional world-tile coordinates.</summary>
    public Vector2 Position { get; private set; }

    /// <summary>
    /// Which way it is looking, in *screen* terms: +1 for right, -1 for left, and the art
    /// is mirrored for the latter. Screen-space rather than tile-space because that is the
    /// only thing a flipped billboard can express — on an iso map, east and north both
    /// read as "off to the right".
    /// </summary>
    public float Facing { get; private set; } = 1f;

    /// <summary>Seeds this mob's own look, so a flock is not one animal stamped out six times.</summary>
    public int Seed { get; }

    /// <summary>
    /// This mob's body: its health, hunger, tiredness, gender, age and whatever is wrong with
    /// it. Everything that happens to a mob happens here; <see cref="Think"/> only decides
    /// what it does about it.
    /// </summary>
    public Vitals Vitals { get; }

    /// <summary>
    /// How big it is drawn: its own size jitter, times how far it has grown into itself. A
    /// lamb is a small sheep, not a different sprite.
    /// </summary>
    public float Scale => _scale * Vitals.Growth;

    /// <summary>Size jitter around 1, rolled once from the seed.</summary>
    private readonly float _scale;

    /// <summary>What it is doing this frame, which is what its body is charged for.</summary>
    public MobActivity Activity { get; protected set; }

    /// <summary>False once its health has run out; the world stops updating and drawing it.</summary>
    public bool IsAlive => Vitals.IsAlive;

    /// <summary>True on any frame the mob actually covered ground.</summary>
    public bool IsWalking { get; private set; }

    /// <summary>
    /// The walk cycle in radians, advanced by distance walked rather than by time, so a
    /// mob that stops mid-stride keeps that stride. The renderer bobs the sprite with it.
    /// </summary>
    public float WalkPhase { get; private set; }

    public abstract MobKind Kind { get; }

    /// <summary>How fast it walks, in tiles per second.</summary>
    protected abstract float WalkSpeed { get; }

    /// <summary>This mob's own RNG — deterministic from <see cref="Seed"/>, and its alone.</summary>
    protected Random Rng { get; }

    /// <summary>
    /// Full strides per tile walked; sets how quickly the bob bounces. A tile is a lot of
    /// ground next to an animal this size, so it takes a good many strides to cross one.
    /// </summary>
    private const float StridesPerTile = 7f;

    /// <summary>How close to the target counts as having got there.</summary>
    private const float ArriveRadius = 0.25f;

    /// <summary>
    /// Elevation levels a mob will climb in a single step. Levels are fine — there are
    /// <see cref="WorldConstants.MaxLevel"/> of them from sea to summit — so this is a
    /// hillside it can walk up rather than a mountain face it would walk through.
    /// </summary>
    private const float MaxClimb = 3f;

    /// <summary>
    /// Radians either side of straight ahead that a blocked mob will try before giving up.
    /// Enough to follow a shoreline round or squeeze past the foot of a cliff instead of
    /// stopping dead the moment the direct line stops working.
    /// </summary>
    private static readonly float[] Deflections = { 0f, 0.6f, -0.6f, 1.2f, -1.2f };

    protected Mob(Vector2 position, int seed)
    {
        Position = position;
        Seed = seed;
        Rng = new Random(seed);
        _scale = Rand(0.88f, 1.14f);
        Vitals = CreateVitals(Rng.Next(2) == 0 ? MobGender.Male : MobGender.Female);
    }

    /// <summary>
    /// The body this mob is born with: its species' tuning, and how old it is when it turns
    /// up. Called from the constructor, so an override may read its argument and
    /// <see cref="Rng"/> — both already set — but not its own fields, which are not yet.
    /// </summary>
    protected abstract Vitals CreateVitals(MobGender gender);

    /// <summary>One frame of life. Called by <see cref="World"/> every update.</summary>
    public void Update(World world, float dt)
    {
        IsWalking = false;
        Activity = MobActivity.Idle;

        Think(world, dt);

        // Walking is walking whatever the brain thought it was doing.
        if (IsWalking)
            Activity = MobActivity.Moving;

        // A body keeps time in days, not seconds, so it lives at the same pace against the
        // sun however fast the day is set to pass.
        Vitals.Advance(dt / MathF.Max(1f, world.Sky.DayLength), Activity);
    }

    /// <summary>
    /// What this mob wants this frame. Implementations drive themselves with
    /// <see cref="WalkToward"/>, and are free to do nothing at all — a grazing sheep is
    /// still a sheep.
    /// </summary>
    protected abstract void Think(World world, float dt);

    /// <summary>
    /// Walks up to one frame's worth of ground toward <paramref name="target"/>, straight
    /// at it if it can and at a slant if it can't. Never steps into water, off the loaded
    /// map, or up something too steep to climb — a mob that runs out of ways forward
    /// reports <see cref="MoveResult.Blocked"/>, and its brain picks somewhere else to be.
    /// </summary>
    protected MoveResult WalkToward(World world, Vector2 target, float dt)
    {
        Vector2 delta = target - Position;
        float distance = delta.Length();
        if (distance <= ArriveRadius)
            return MoveResult.Arrived;

        Vector2 straight = delta / distance;
        float step = MathF.Min(WalkSpeed * dt, distance);

        foreach (float turn in Deflections)
        {
            Vector2 way = Rotate(straight, turn);
            Vector2 next = Position + way * step;
            if (!CanWalk(world, Position, next))
                continue;

            MoveTo(next, way, step);
            return MoveResult.Moving;
        }

        return MoveResult.Blocked;
    }

    private void MoveTo(Vector2 next, Vector2 way, float step)
    {
        Position = next;
        IsWalking = true;
        WalkPhase = (WalkPhase + step * StridesPerTile * MathHelper.TwoPi) % MathHelper.TwoPi;

        // Screen x runs along (tileX - tileY), so east and north both carry a mob to the
        // right. A path that is near vertical on screen leaves the facing where it was,
        // rather than flicking it back and forth on rounding noise.
        float screenX = way.X - way.Y;
        if (MathF.Abs(screenX) > 0.15f)
            Facing = MathF.Sign(screenX);
    }

    /// <summary>
    /// Is there ground here a mob could be standing on? False over water, false past a
    /// pole where there is no world, and false over chunks that aren't loaded — out there
    /// nothing is known yet, and a mob that walked off into it could surface in the middle
    /// of an ocean once it generated.
    /// </summary>
    public static bool CanStand(World world, Vector2 at)
        => TryTile(world, at, out Tile tile) && !tile.IsWater && !tile.IsVoid;

    /// <summary>As <see cref="CanStand"/>, plus that the climb from where it is now is walkable.</summary>
    private static bool CanWalk(World world, Vector2 from, Vector2 to)
    {
        if (!TryTile(world, to, out Tile there) || there.IsWater || there.IsVoid)
            return false;

        // Standing on ground we can't read (it just unloaded underfoot) is no reason to
        // refuse a step onto ground we can: let it walk back into the known world.
        if (!TryTile(world, from, out Tile here))
            return true;

        return Math.Abs(there.Level - here.Level) <= MaxClimb;
    }

    private static bool TryTile(World world, Vector2 at, out Tile tile)
        => world.TryGetTile((int)MathF.Floor(at.X), (int)MathF.Floor(at.Y), out tile);

    /// <summary>A float in [min, max) from this mob's own RNG.</summary>
    protected float Rand(float min, float max) => min + (float)Rng.NextDouble() * (max - min);

    private static Vector2 Rotate(Vector2 v, float radians)
    {
        if (radians == 0f)
            return v;

        float c = MathF.Cos(radians), s = MathF.Sin(radians);
        return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }
}
