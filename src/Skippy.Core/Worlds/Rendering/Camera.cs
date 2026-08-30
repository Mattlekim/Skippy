using System;
using Microsoft.Xna.Framework;

namespace Skippy.Core.Worlds.Rendering;

/// <summary>
/// A 2D isometric camera. It tracks a focus point in world-tile space (so it
/// stays put as chunks stream in and out) plus a zoom factor.
/// </summary>
public sealed class Camera
{
    /// <summary>Point the view is centered on, in world-tile coordinates.</summary>
    public Vector2 FocusTile;

    public float Zoom = 0.8f;

    public const float MinZoom = 0.4f;
    public const float MaxZoom = 16f;

    public void PanTiles(Vector2 tileDelta) => FocusTile += tileDelta;

    public void ZoomBy(float factor) => Zoom = Math.Clamp(Zoom * factor, MinZoom, MaxZoom);

    /// <summary>Iso-space position the camera is looking at (elevation ignored).</summary>
    public Vector2 FocusIso => Iso.TileToIso(FocusTile.X, FocusTile.Y, 0f);
}
