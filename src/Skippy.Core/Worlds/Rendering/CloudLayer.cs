using System;
using System.Diagnostics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Skippy.Core.Worlds.Rendering;

/// <summary>
/// A drifting sheet of clouds and the shadows they throw on the ground.
///
/// It is one tileable cloud texture (white puffs, density in the alpha) drawn twice
/// as a full-viewport overlay whose texture coordinates are derived from world
/// (iso) position, so the pattern is pinned to the land and slides across it on the
/// wind rather than sticking to the screen:
///   • once dark, laid on the ground — the cloud shadow — offset in the sun's
///     direction so a cloud and its shadow sit apart the way they do under a low sun;
///   • once white, overhead — the clouds themselves.
///
/// Both fade out at night with the daylight, and everything but the tiny 4-vertex
/// overlay quad lives in the one texture on the GPU.
/// </summary>
public sealed class CloudLayer : IDisposable
{
    private const int TexSize = 256;

    /// <summary>One cloud-texture tile spans this many iso pixels of world — large, so puffs read as clouds rather than aliasing into speckle.</summary>
    private const float WorldPerTile = 3000f;

    /// <summary>Wind, in cloud-tiles per second — how fast the sheet slides over the land.</summary>
    private static readonly Vector2 Wind = new(0.020f, 0.008f);

    /// <summary>Darkest a thick cloud shadow gets at full daylight.</summary>
    private const float ShadowStrength = 0.5f;

    /// <summary>Opacity of the clouds themselves.</summary>
    private const float CloudOpacity = 0.33f;

    private readonly Texture2D _tex;
    private readonly BasicEffect _fx;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private readonly VertexPositionColorTexture[] _quad = new VertexPositionColorTexture[4];
    private static readonly short[] _idx = { 0, 1, 2, 0, 2, 3 };

    public CloudLayer(GraphicsDevice gd)
    {
        _tex = BuildTexture(gd);
        _fx = new BasicEffect(gd)
        {
            TextureEnabled = true,
            VertexColorEnabled = true,
            LightingEnabled = false,
            Texture = _tex,
            World = Matrix.Identity,
            View = Matrix.Identity,
        };
    }

    /// <summary>
    /// Draws the cloud shadows (then, from <see cref="DrawClouds"/>, the clouds). The
    /// caller supplies the same focus/zoom the world was drawn with so the overlay
    /// lines up, plus the sun to fade and offset by.
    ///
    /// <paramref name="isoTop"/> and <paramref name="isoBottom"/> are the world's two
    /// poles in iso space. The sheet is cut to them: there is no sky past the end of the
    /// world, and a cloud drifting on over the black would be the one thing still saying
    /// the map goes on out there.
    /// </summary>
    public void DrawShadows(GraphicsDevice gd, Vector2 focusIso, Vector2 center, float zoom, SkyLight sky,
                            float isoTop, float isoBottom)
    {
        if (sky.Daylight <= 0.01f)
            return;

        // The shadow lies on the ground under the cloud, thrown along the sun's
        // horizontal direction — so it slides away from the cloud as the sun lowers.
        // Alpha-blended (not multiplied), because the cloud's shape is in the texture's
        // alpha: only where the cloud is opaque does its dark colour reach the ground.
        Vector2 sunPush = HorizontalSunPush(sky);
        byte a = (byte)(255 * ShadowStrength * sky.Daylight);
        DrawOverlay(gd, focusIso, center, zoom, new Color((byte)18, (byte)20, (byte)28, a), sunPush,
                    BlendState.NonPremultiplied, isoTop, isoBottom);
    }

    /// <summary>The clouds themselves, drawn last so they sit above the world.</summary>
    public void DrawClouds(GraphicsDevice gd, Vector2 focusIso, Vector2 center, float zoom, SkyLight sky,
                           float isoTop, float isoBottom)
    {
        // Clouds catch the sun's colour too — warm at dusk, white at noon — and dim
        // but never vanish at night.
        float lit = 0.35f + 0.65f * sky.Daylight;
        var tint = new Vector3(1f, 0.98f, 0.95f);
        tint = Vector3.Lerp(tint, sky.SunColor + new Vector3(0.4f), 0.35f * (1f - sky.Daylight));
        var color = new Color(new Vector4(tint * lit, CloudOpacity));
        DrawOverlay(gd, focusIso, center, zoom, color, Vector2.Zero, BlendState.NonPremultiplied,
                    isoTop, isoBottom);
    }

    private void DrawOverlay(GraphicsDevice gd, Vector2 focusIso, Vector2 center, float zoom,
                             Color color, Vector2 uvPush, BlendState blend, float isoTop, float isoBottom)
    {
        int w = gd.Viewport.Width, h = gd.Viewport.Height;

        // The world's poles are lines of constant iso Y, which come out as two horizontal
        // lines on screen — so cutting the sheet to the world is just clipping the quad's
        // top and bottom. The UVs are worked out per corner from where that corner lands
        // in the world, so the cropped sheet still lines up with the uncropped one.
        float top = MathF.Max(0f, (isoTop - focusIso.Y) * zoom + center.Y);
        float bottom = MathF.Min(h, (isoBottom - focusIso.Y) * zoom + center.Y);
        if (bottom - top < 1f)
            return; // the world is entirely off screen

        _fx.Projection = Matrix.CreateOrthographicOffCenter(0, w, h, 0, 0, 1);

        float drift = (float)_clock.Elapsed.TotalSeconds;
        Vector2 scroll = Wind * drift + uvPush;

        // Each screen corner maps back to a world-iso point; UVs come from that point,
        // so the cloud sheet is locked to the world and only the wind moves it.
        SetCorner(0, 0, top, focusIso, center, zoom, color, scroll);
        SetCorner(1, w, top, focusIso, center, zoom, color, scroll);
        SetCorner(2, w, bottom, focusIso, center, zoom, color, scroll);
        SetCorner(3, 0, bottom, focusIso, center, zoom, color, scroll);

        gd.BlendState = blend;
        gd.DepthStencilState = DepthStencilState.None;
        gd.RasterizerState = RasterizerState.CullNone;
        gd.SamplerStates[0] = SamplerState.LinearWrap;

        foreach (EffectPass pass in _fx.CurrentTechnique.Passes)
        {
            pass.Apply();
            gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, _quad, 0, 4, _idx, 0, 2);
        }
    }

    private void SetCorner(int i, float sx, float sy, Vector2 focusIso, Vector2 center, float zoom, Color color, Vector2 scroll)
    {
        Vector2 iso = (new Vector2(sx, sy) - center) / zoom + focusIso;
        Vector2 uv = iso / WorldPerTile + scroll;
        _quad[i] = new VertexPositionColorTexture(new Vector3(sx, sy, 0f), color, uv);
    }

    /// <summary>Horizontal shove given to the shadow: further out as the sun sinks.</summary>
    private static Vector2 HorizontalSunPush(SkyLight sky)
    {
        Vector3 d = sky.LightDirection; // travel direction, ground-ward
        var horiz = new Vector2(d.X, d.Z);
        if (horiz.LengthSquared() < 1e-5f)
            return Vector2.Zero;
        horiz.Normalize();
        // Longer shadow when the sun is low (small altitude). Capped so it stays sane.
        float lowness = 1f - sky.Daylight;
        return horiz * (0.05f + 0.12f * lowness);
    }

    /// <summary>Fraction of the sky left clear — the rest is broken into puffs.</summary>
    private const float ClearFraction = 0.45f;

    /// <summary>
    /// A tileable cloud sheet: fractal value noise, normalised across the whole
    /// texture and then thresholded so a fixed fraction of the sky stays clear and
    /// the rest breaks into soft, separated puffs. Working out the threshold from the
    /// noise's own measured range (rather than a guessed constant) is what keeps the
    /// coverage sane instead of fogging the whole map. White throughout; the shape
    /// lives in the alpha.
    /// </summary>
    private static Texture2D BuildTexture(GraphicsDevice gd)
    {
        var raw = new float[TexSize * TexSize];
        float min = float.MaxValue, max = float.MinValue;
        for (int y = 0; y < TexSize; y++)
            for (int x = 0; x < TexSize; x++)
            {
                float n = Fbm(x, y);
                raw[y * TexSize + x] = n;
                if (n < min) min = n;
                if (n > max) max = n;
            }

        float span = MathF.Max(1e-4f, max - min);
        var data = new Color[TexSize * TexSize];
        for (int i = 0; i < raw.Length; i++)
        {
            float norm = (raw[i] - min) / span;               // 0..1 across the sheet
            float density = Smooth((norm - ClearFraction) / (1f - ClearFraction));
            data[i] = new Color((byte)255, (byte)255, (byte)255, (byte)(255 * density));
        }

        var tex = new Texture2D(gd, TexSize, TexSize, false, SurfaceFormat.Color);
        tex.SetData(data);
        return tex;
    }

    // ---- tileable value-noise --------------------------------------------------

    /// <summary>Low-frequency-dominated fractal noise, so puffs are big and soft, not grainy.</summary>
    private static float Fbm(int x, int y)
    {
        float sum = 0f, amp = 1f, norm = 0f;
        int period = 2;
        for (int o = 0; o < 3; o++)
        {
            sum += amp * TileNoise(x / (float)TexSize * period, y / (float)TexSize * period, period);
            norm += amp;
            amp *= 0.45f;
            period *= 2;
        }
        return sum / norm;
    }

    /// <summary>Value noise on a torus of side <paramref name="period"/>, so it wraps seamlessly.</summary>
    private static float TileNoise(float x, float y, int period)
    {
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        float v00 = Hash((x0) % period, (y0) % period, period);
        float v10 = Hash((x0 + 1) % period, (y0) % period, period);
        float v01 = Hash((x0) % period, (y0 + 1) % period, period);
        float v11 = Hash((x0 + 1) % period, (y0 + 1) % period, period);
        float sx = Smooth(fx), sy = Smooth(fy);
        return MathHelper.Lerp(MathHelper.Lerp(v00, v10, sx), MathHelper.Lerp(v01, v11, sx), sy);
    }

    private static float Hash(int x, int y, int period)
    {
        x = ((x % period) + period) % period;
        y = ((y % period) + period) % period;
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + period * 2246822519);
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFF) / 65535f;
        }
    }

    private static float Smooth(float t)
    {
        t = t < 0f ? 0f : t > 1f ? 1f : t;
        return t * t * (3f - 2f * t);
    }

    public void Dispose()
    {
        _tex.Dispose();
        _fx.Dispose();
    }
}
