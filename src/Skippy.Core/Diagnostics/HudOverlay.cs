using System;
using System.Diagnostics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Skippy.Core.Diagnostics;

/// <summary>
/// The debug read-out in the top-right corner: frames per second on the first
/// line, then whatever status lines the caller passes in (the tile size, while it
/// is being tuned). Right-aligned, set in the runtime-generated
/// <see cref="PixelFont"/>.
/// </summary>
public sealed class HudOverlay : IDisposable
{
    private const int PixelScale = 3;   // screen pixels per glyph pixel
    private const int Margin = 8;
    private const float SampleSeconds = 0.5f; // how often the displayed FPS refreshes

    private readonly PixelFont _font;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastSampleTicks;
    private int _framesSinceSample;
    private int _fps;

    private static readonly Color TextColor = Color.White;
    private static readonly Color ShadowColor = new(0, 0, 0, 160);

    public HudOverlay(GraphicsDevice gd) => _font = new PixelFont(gd);

    /// <summary>Current frames-per-second, refreshed every half second.</summary>
    public int Fps => _fps;

    /// <summary>Call once per rendered frame; draws the read-out and advances the FPS measurement.</summary>
    public void Draw(SpriteBatch sb, Viewport viewport, params string[] statusLines)
    {
        Sample();

        sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
        DrawLine(sb, viewport, "FPS " + _fps, 0);
        for (int i = 0; i < statusLines.Length; i++)
            DrawLine(sb, viewport, statusLines[i], i + 1);
        sb.End();
    }

    private void DrawLine(SpriteBatch sb, Viewport viewport, string text, int row)
    {
        var at = new Vector2(
            viewport.Width - PixelFont.Measure(text, PixelScale) - Margin,
            Margin + row * (PixelFont.LineHeight(PixelScale) + PixelScale));
        _font.DrawShadowed(sb, text, at, PixelScale, TextColor, ShadowColor);
    }

    private void Sample()
    {
        _framesSinceSample++;
        long now = _clock.ElapsedTicks;
        double elapsed = (now - _lastSampleTicks) / (double)Stopwatch.Frequency;
        if (elapsed < SampleSeconds)
            return;

        _fps = (int)Math.Round(_framesSinceSample / elapsed);
        _framesSinceSample = 0;
        _lastSampleTicks = now;
    }

    public void Dispose() => _font.Dispose();
}
