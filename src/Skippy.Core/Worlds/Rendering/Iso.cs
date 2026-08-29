using Microsoft.Xna.Framework;

namespace Skippy.Core.Worlds.Rendering;

/// <summary>
/// Isometric projection maths. Tiles are drawn as 2:1 diamonds; elevation lifts
/// a tile straight up the screen by <see cref="HeightStep"/> pixels per level.
/// </summary>
public static class Iso
{
    public const int TileWidth = 32;
    public const int TileHeight = 16;
    public const int HeightStep = 7;

    /// <summary>World tile (+ elevation level) → iso-space pixel (center of the diamond top).</summary>
    public static Vector2 TileToIso(float tileX, float tileY, float level) => new(
        (tileX - tileY) * (TileWidth / 2f),
        (tileX + tileY) * (TileHeight / 2f) - level * HeightStep);

    /// <summary>Iso-space pixel → world tile, ignoring elevation (used for picking / culling).</summary>
    public static Vector2 IsoToTile(float isoX, float isoY)
    {
        float a = isoX / (TileWidth / 2f);
        float b = isoY / (TileHeight / 2f);
        return new Vector2((a + b) / 2f, (b - a) / 2f);
    }
}
