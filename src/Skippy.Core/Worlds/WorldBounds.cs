using System;
using Microsoft.Xna.Framework;

namespace Skippy.Core.Worlds;

/// <summary>
/// The shape of the world: a band that wraps round like a planet.
///
/// The map is isometric, so the tile grid's own x and y axes both run diagonally on
/// screen — a rectangle in tile space would be drawn as a diamond, with its edges
/// heading off in two directions at once. So the world is not bounded in x and y. It is
/// bounded in the two axes the player actually sees:
///
///   • <see cref="V"/> = x + y — straight down the screen. This is the world's
///     north–south axis, and it is the one with ends: <see cref="Span"/> tiles from
///     pole to pole, with nothing (black) beyond either.
///   • <see cref="U"/> = x − y — straight across the screen. This is east–west, and it
///     has no ends at all: the world repeats exactly every <see cref="Circumference"/>
///     of it, which is twice the span, so walking east far enough brings you back to
///     where you started.
///
/// The east–west wrap is not a teleport and there is no seam to hide. A step of
/// <see cref="Circumference"/> in u is the tile-space move (x, y) → (x + Span, y − Span),
/// and every field the generator is built from is periodic under exactly that move
/// (see <c>DefaultWorldGenerator</c>), so the ground a lap to the east *is* the same
/// ground, generated from the same numbers. Coordinates simply keep counting up; the
/// land under them comes round again.
/// </summary>
public readonly struct WorldBounds
{
    /// <summary>Tiles from pole to pole, measured along <see cref="V"/>.</summary>
    public readonly int Span;

    public WorldBounds(int span) => Span = span;

    /// <summary>How far east–west it is all the way round. Twice the span, by definition.</summary>
    public int Circumference => Span * 2;

    /// <summary>Screen-vertical (north–south) coordinate of a tile: 0 at one pole, <see cref="Span"/> at the other.</summary>
    public static int V(int x, int y) => x + y;

    /// <summary>Screen-horizontal (east–west) coordinate of a tile. Wraps every <see cref="Circumference"/>.</summary>
    public static int U(int x, int y) => x - y;

    public static float V(float x, float y) => x + y;
    public static float U(float x, float y) => x - y;

    /// <summary>Whether a tile is part of the world at all, rather than the black beyond a pole.</summary>
    public bool Contains(int x, int y) => (uint)(x + y) < (uint)Span;

    /// <summary>As <see cref="Contains(int, int)"/>, for a row that is already in v.</summary>
    public bool ContainsRow(int v) => (uint)v < (uint)Span;

    /// <summary>
    /// How far from the equator a row lies: 0 on the equator, 1 at either pole. What the
    /// generator's climate is banded by, so the poles come out cold and the middle hot.
    /// </summary>
    public float Latitude(float v) => MathF.Min(1f, MathF.Abs(2f * v / Span - 1f));

    /// <summary>The equator, as a tile position — where a new world opens on.</summary>
    public Vector2 Equator => new(Span * 0.25f, Span * 0.25f);

    /// <summary>
    /// The one tile position out of the endless laps that stands for a piece of ground:
    /// its u folded into the first lap, with v left alone.
    ///
    /// The noise fields close on themselves without needing this. It is for the things
    /// that cannot — the per-tile hashes, like the one that decides where each tree
    /// stands inside its tile. Hash the canonical position instead of the raw one and
    /// they come round with everything else.
    /// </summary>
    public void Canonical(int x, int y, out int cx, out int cy)
    {
        int v = x + y;
        int u = x - y;

        // u and v are always the same parity (u + v = 2x), and the circumference is even,
        // so folding u keeps them in step and the halves below stay whole.
        int c = Circumference;
        u %= c;
        if (u < 0) u += c;

        cx = (u + v) / 2;
        cy = (v - u) / 2;
    }

    /// <summary>A stable 0..1 value per tile, the same on every lap. See <see cref="Canonical"/>.</summary>
    public float Hash01(int x, int y)
    {
        Canonical(x, y, out int cx, out int cy);
        unchecked
        {
            uint h = (uint)(cx * 374761393 + cy * 668265263);
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFF) / 65535f;
        }
    }

    /// <summary>
    /// Pulls a focus point back inside the band, so the camera cannot be driven off the
    /// end of the world and lost in the black. Only north–south is held: east–west has
    /// no ends to hold it against.
    ///
    /// It moves the point along v alone (both coordinates by half the shortfall), so a
    /// camera pushed against a pole still slides freely east and west along it.
    /// </summary>
    public Vector2 ClampFocus(Vector2 tile)
    {
        float v = V(tile.X, tile.Y);
        float clamped = Math.Clamp(v, 0f, Span);
        if (clamped == v)
            return tile;

        float shift = (clamped - v) * 0.5f;
        return new Vector2(tile.X + shift, tile.Y + shift);
    }
}
