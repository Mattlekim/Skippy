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
    private const float WorldPerTile = 4200f;

    /// <summary>Base wind, in cloud-tiles per second — how fast the sheet slides over the land. Brisk, so it reads as a high, windy sky. Gusts are layered on top in <see cref="WindScroll"/>.</summary>
    private static readonly Vector2 Wind = new(0.060f, 0.026f);

    /// <summary>Darkest a thick cloud shadow gets at full daylight.</summary>
    private const float ShadowStrength = 0.5f;

    /// <summary>Opacity of the clouds themselves.</summary>
    private const float CloudOpacity = 0.33f;

    // --- "high in the sky, seen only from far off" ---------------------------
    //
    // The clouds live far above the land, so two things follow:
    //   • You only notice them when you pull the camera back — up close the view is
    //     under them. So they fade in as you zoom out and are gone when zoomed in.
    //   • Being high up, they slide across the ground a touch slower than the land
    //     itself as you pan (parallax), which reads as distance/altitude.

    /// <summary>At or above this zoom the sky is empty — you are in too close to see the high clouds.</summary>
    private const float CloudHiddenZoom = 0.85f;

    /// <summary>At or below this zoom the clouds are at full strength — you have pulled back far enough to see the whole high sky.</summary>
    private const float CloudFullZoom = 0.5f;

    /// <summary>How much of the camera's pan the high sheet takes up (less than 1 = it lags the ground, the way distant, high things do).</summary>
    private const float Parallax = 0.88f;

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
    /// </summary>
    public void DrawShadows(GraphicsDevice gd, Vector2 focusIso, Vector2 center, float zoom, SkyLight sky)
    {
        if (sky.Daylight <= 0.01f)
            return;

        float show = ZoomVisibility(zoom);
        if (show <= 0.004f)
            return; // zoomed in under the clouds — nothing to cast.

        // The shadow lies on the ground under the cloud, thrown along the sun's
        // horizontal direction — so it slides away from the cloud as the sun lowers.
        // Alpha-blended (not multiplied), because the cloud's shape is in the texture's
        // alpha: only where the cloud is opaque does its dark colour reach the ground.
        Vector2 sunPush = HorizontalSunPush(sky);
        byte a = (byte)(255 * ShadowStrength * sky.Daylight * show);
        DrawOverlay(gd, focusIso, center, zoom, new Color((byte)18, (byte)20, (byte)28, a), sunPush, BlendState.NonPremultiplied);
    }

    /// <summary>The clouds themselves, drawn last so they sit above the world.</summary>
    public void DrawClouds(GraphicsDevice gd, Vector2 focusIso, Vector2 center, float zoom, SkyLight sky)
    {
        float show = ZoomVisibility(zoom);
        if (show <= 0.004f)
            return; // in too close to see the high sky.

        // Clouds catch the sun's colour too — warm at dusk, white at noon — and dim
        // but never vanish at night.
        float lit = 0.35f + 0.65f * sky.Daylight;
        var tint = new Vector3(1f, 0.98f, 0.95f);
        tint = Vector3.Lerp(tint, sky.SunColor + new Vector3(0.4f), 0.35f * (1f - sky.Daylight));
        var color = new Color(new Vector4(tint * lit, CloudOpacity * show));
        DrawOverlay(gd, focusIso, center, zoom, color, Vector2.Zero, BlendState.NonPremultiplied);
    }

    /// <summary>
    /// How present the high clouds are at this zoom: 0 when zoomed in close (you are
    /// below them), ramping smoothly to 1 as you pull the camera back far enough to
    /// take in the whole sky.
    /// </summary>
    private static float ZoomVisibility(float zoom)
    {
        float t = (CloudHiddenZoom - zoom) / (CloudHiddenZoom - CloudFullZoom);
        return Smooth(t);
    }

    private void DrawOverlay(GraphicsDevice gd, Vector2 focusIso, Vector2 center, float zoom,
                             Color color, Vector2 uvPush, BlendState blend)
    {
        int w = gd.Viewport.Width, h = gd.Viewport.Height;
        _fx.Projection = Matrix.CreateOrthographicOffCenter(0, w, h, 0, 0, 1);

        float drift = (float)_clock.Elapsed.TotalSeconds;
        Vector2 scroll = WindScroll(drift) + uvPush;

        // The sheet is high above the land, so it takes up only part of the camera's pan —
        // parallax that reads as altitude. Fold that into the focus the corners map against.
        Vector2 parallaxFocus = focusIso * Parallax;

        // Each screen corner maps back to a world-iso point; UVs come from that point,
        // so the cloud sheet is locked to the world and only the wind moves it.
        SetCorner(0, 0, 0, parallaxFocus, center, zoom, color, scroll);
        SetCorner(1, w, 0, parallaxFocus, center, zoom, color, scroll);
        SetCorner(2, w, h, parallaxFocus, center, zoom, color, scroll);
        SetCorner(3, 0, h, parallaxFocus, center, zoom, color, scroll);

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

    /// <summary>
    /// Where the cloud sheet has drifted to, in tile-space, after <paramref name="t"/>
    /// seconds of wind. Not a straight line: on top of the steady drift the wind gusts
    /// (surges and eases along its heading) and wanders (swings a little to either side),
    /// so the sky churns the way a high, windy one does rather than sliding rigidly.
    /// </summary>
    private static Vector2 WindScroll(float t)
    {
        Vector2 steady = Wind * t;

        // Wind heading, and the unit across it, to push the gust and the wander along.
        float speed = Wind.Length();
        Vector2 along = speed > 1e-5f ? Wind / speed : new Vector2(1f, 0f);
        Vector2 across = new(-along.Y, along.X);

        // Gust: the drift speeds up and slows down. Two out-of-step waves so the surges
        // never fall into an obvious rhythm.
        float gust = 0.35f * MathF.Sin(t * 0.23f) + 0.18f * MathF.Sin(t * 0.61f + 1.3f);

        // Wander: the heading swings gently to one side and back.
        float wander = 0.5f * MathF.Sin(t * 0.17f + 2.1f) + 0.22f * MathF.Sin(t * 0.43f);

        // Kept small enough that a gust only speeds or eases the drift — it never
        // outruns the steady flow and reverses it, which high wind does not do.
        return steady + along * (speed * 3f * gust) + across * (speed * 2.5f * wander);
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
