using System;

namespace Skippy.Core.Worlds.Noise;

/// <summary>
/// Seeded 2D Perlin noise with fractal (fBm) sampling. Deterministic and
/// coordinate-based: the value at a world position depends only on that position
/// and the seed, so an infinite world can be regenerated chunk-by-chunk and
/// always stitches together seamlessly.
///
/// Create separate instances (different seeds) for independent fields such as
/// height, temperature and moisture.
/// </summary>
public sealed class PerlinNoise
{
    private readonly int[] _perm = new int[512];

    public PerlinNoise(int seed)
    {
        var p = new int[256];
        for (int i = 0; i < 256; i++) p[i] = i;

        var rng = new Random(seed);
        for (int i = 255; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (p[i], p[j]) = (p[j], p[i]);
        }
        for (int i = 0; i < 512; i++) _perm[i] = p[i & 255];
    }

    /// <summary>Single-octave noise in roughly [-1, 1].</summary>
    public float Sample(float x, float y)
    {
        int xi = (int)MathF.Floor(x) & 255;
        int yi = (int)MathF.Floor(y) & 255;
        float xf = x - MathF.Floor(x);
        float yf = y - MathF.Floor(y);

        float u = Fade(xf);
        float v = Fade(yf);

        int aa = _perm[_perm[xi] + yi];
        int ab = _perm[_perm[xi] + yi + 1];
        int ba = _perm[_perm[xi + 1] + yi];
        int bb = _perm[_perm[xi + 1] + yi + 1];

        float x1 = Lerp(Grad(aa, xf, yf), Grad(ba, xf - 1, yf), u);
        float x2 = Lerp(Grad(ab, xf, yf - 1), Grad(bb, xf - 1, yf - 1), u);
        return Lerp(x1, x2, v);
    }

    /// <summary>
    /// Fractal Brownian motion: sums <paramref name="octaves"/> layers of noise,
    /// each higher frequency and lower amplitude, normalized to roughly [-1, 1].
    /// </summary>
    public float Fractal(float x, float y, int octaves, float lacunarity = 2f, float gain = 0.5f)
    {
        float sum = 0f, amp = 1f, freq = 1f, norm = 0f;
        for (int i = 0; i < octaves; i++)
        {
            sum += Sample(x * freq, y * freq) * amp;
            norm += amp;
            amp *= gain;
            freq *= lacunarity;
        }
        return sum / norm;
    }

    /// <summary>Fractal noise remapped to [0, 1].</summary>
    public float Fractal01(float x, float y, int octaves, float lacunarity = 2f, float gain = 0.5f)
        => Fractal(x, y, octaves, lacunarity, gain) * 0.5f + 0.5f;

    private static float Fade(float t) => t * t * t * (t * (t * 6 - 15) + 10);
    private static float Lerp(float a, float b, float t) => a + t * (b - a);

    private static float Grad(int hash, float x, float y)
    {
        int h = hash & 7;
        float u = h < 4 ? x : y;
        float v = h < 4 ? y : x;
        return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
    }
}
