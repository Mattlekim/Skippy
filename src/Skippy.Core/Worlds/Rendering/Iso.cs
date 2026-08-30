using System;
using Microsoft.Xna.Framework;

namespace Skippy.Core.Worlds.Rendering;

/// <summary>
/// Isometric projection maths. Tiles are drawn as diamonds (see <see cref="TileAspect"/>);
/// elevation lifts a tile straight up the screen by <see cref="HeightStep"/> pixels per level.
///
/// <see cref="TileWidth"/> is the single knob the whole world is drawn from, and
/// it can be changed while the game runs (see the controls in docs/WORLD.md) so a
/// size can be picked by eye. Cached geometry watches <see cref="Version"/> to
/// know when it has to be rebuilt.
///
/// A tile is an amount of *ground*, not a zoom level: what stands on it — trees,
/// rocks, eventually units — keeps its own absolute size, so a bigger tile is
/// simply more room and fits more of them. Use the camera zoom to make everything
/// bigger on screen.
/// </summary>
public static class Iso
{
    /// <summary>The tile size the elevation step and scatter densities were authored against.</summary>
    public const int BaseTileWidth = 64;

    /// <summary>
    /// Screen pixels one elevation level lifts a tile by, per <see cref="BaseTileWidth"/>.
    /// This and <see cref="TileAspect"/> together set the camera's apparent pitch: a
    /// tall step over a foreshortened ground plane is a low, tilted-back view in which
    /// hills and mountains stand up off the map, rather than a near-top-down one that
    /// flattens them. Turned up from the old 19 so a mountain reads as a mountain.
    /// </summary>
    private const float BaseHeightStep = 30f;

    /// <summary>
    /// The ground diamond's height as a fraction of its width. 0.5 is a textbook 2:1
    /// isometric (~27° pitch); dropping it foreshortens the ground the way tilting the
    /// camera back toward the horizon would, which is what makes vertical relief — the
    /// mountains — read as taller against it.
    /// </summary>
    private const float TileAspect = 0.44f;

    public const int MinTileWidth = 16;
    public const int MaxTileWidth = 512;

    private static int _tileWidth = 312;

    /// <summary>
    /// Width of a tile diamond in iso pixels. Clamped to
    /// [<see cref="MinTileWidth"/>, <see cref="MaxTileWidth"/>] and kept even so
    /// <see cref="TileHeight"/> stays a whole number.
    /// </summary>
    public static int TileWidth
    {
        get => _tileWidth;
        set
        {
            int w = Math.Clamp(value, MinTileWidth, MaxTileWidth) & ~1;
            if (w == _tileWidth) return;
            _tileWidth = w;
            Version++;
        }
    }

    /// <summary>Height of a tile diamond. Follows the width through <see cref="TileAspect"/>.</summary>
    public static int TileHeight => (int)MathF.Round(_tileWidth * TileAspect);

    /// <summary>Screen pixels one elevation level lifts a tile by, in the lowlands.</summary>
    public static float HeightStep => _tileWidth * (BaseHeightStep / BaseTileWidth);

    /// <summary>
    /// The elevation level above which extra lift is piled on. Below it — coasts,
    /// plains, gentle hills — one level is one <see cref="HeightStep"/>, so lowland
    /// terrain keeps its calm, readable relief.
    /// </summary>
    private const float HighlandStart = 4f;

    /// <summary>
    /// How much extra lift each level above <see cref="HighlandStart"/> gets, as a
    /// multiple of <see cref="HeightStep"/>. At 2.2 a highland level rises 3.2× as far
    /// as a lowland one, so the upper layers of a mountain pull apart from each other
    /// and from the ground below — the peak gains height and, more to the point,
    /// definition between its tiers instead of stacking flat, evenly-spaced shelves.
    /// </summary>
    private const float HighlandGain = 2.2f;

    /// <summary>
    /// Screen pixels a tile at <paramref name="level"/> is lifted up the screen. Linear
    /// through the lowlands, then progressively steeper above <see cref="HighlandStart"/>
    /// so high ground stands tall and its layers stay distinct. This is the single
    /// place elevation becomes height, so terrain, culling and the camera all agree.
    /// </summary>
    public static float ElevationLift(float level)
        => (level + HighlandGain * MathF.Max(0f, level - HighlandStart)) * HeightStep;

    /// <summary>How many times wider a tile is than <see cref="BaseTileWidth"/>.</summary>
    public static float TileScale => _tileWidth / (float)BaseTileWidth;

    /// <summary>
    /// How many times more ground a tile covers than at <see cref="BaseTileWidth"/>.
    /// Anything scattered per tile (vegetation today) multiplies its count by this,
    /// so the amount of stuff per unit of ground stays put as tiles resize.
    /// </summary>
    public static float TileAreaScale => TileScale * TileScale;

    /// <summary>Bumped every time the tile size changes; cached meshes rebuild when it moves.</summary>
    public static int Version { get; private set; }

    /// <summary>World tile (+ elevation level) → iso-space pixel (center of the diamond top).</summary>
    public static Vector2 TileToIso(float tileX, float tileY, float level) => new(
        (tileX - tileY) * (TileWidth / 2f),
        (tileX + tileY) * (TileHeight / 2f) - ElevationLift(level));

    /// <summary>Iso-space pixel → world tile, ignoring elevation (used for picking / culling).</summary>
    public static Vector2 IsoToTile(float isoX, float isoY)
    {
        float a = isoX / (TileWidth / 2f);
        float b = isoY / (TileHeight / 2f);
        return new Vector2((a + b) / 2f, (b - a) / 2f);
    }
}
