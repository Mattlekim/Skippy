using System;

namespace Skippy.Core.Worlds.Noise;

/// <summary>
/// Seeded 2D Perlin noise with fractal (fBm) sampling. Deterministic and
/// coordinate-based: the value at a world position depends only on that position
/// and the seed, so an infinite world can be regenerated chunk-by-chunk and
/// always stitches together seamlessly.
///
/// It can also be made to <em>close on itself</em> along x: pass a
/// <c>periodX</c> and the field repeats exactly every that many lattice cells,
/// which is what lets a world wrap round like a planet with no seam to hide.
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
    public float Sample(float x, float y) => Sample(x, y, 0);

    /// <summary>
    /// Single-octave noise that repeats every <paramref name="periodX"/> lattice cells
    /// along x (0 for no repeat).
    ///
    /// Perlin noise is built from the gradients at the integer lattice points around the
    /// sample, so it is made periodic simply by folding those two lattice indices into
    /// the period before they are looked up: x and x + period then read the identical
    /// pair of gradients and return the identical value. The fold happens before the
    /// permutation table's own 256-cell wrap, so any period works, not just a divisor
    /// of 256.
    /// </summary>
    public float Sample(float x, float y, int periodX)
    {
        int floorX = (int)MathF.Floor(x);
        int x0 = floorX, x1 = floorX + 1;
        if (periodX > 0)
        {
            x0 = Mod(x0, periodX);
            x1 = Mod(x1, periodX);
        }

        int xi = x0 & 255;
        int xi1 = x1 & 255;
        int yi = (int)MathF.Floor(y) & 255;
        float xf = x - floorX;
        float yf = y - MathF.Floor(y);

        float u = Fade(xf);
        float v = Fade(yf);

        int aa = _perm[_perm[xi] + yi];
        int ab = _perm[_perm[xi] + yi + 1];
        int ba = _perm[_perm[xi1] + yi];
        int bb = _perm[_perm[xi1] + yi + 1];

        float x1v = Lerp(Grad(aa, xf, yf), Grad(ba, xf - 1, yf), u);
        float x2v = Lerp(Grad(ab, xf, yf - 1), Grad(bb, xf - 1, yf - 1), u);
        return Lerp(x1v, x2v, v);
    }

    /// <summary>
    /// Fractal Brownian motion: sums <paramref name="octaves"/> layers of noise,
    /// each higher frequency and lower amplitude, normalized to roughly [-1, 1].
    ///
    /// <paramref name="periodX"/> is the first octave's period; each octave after it
    /// runs at <paramref name="lacunarity"/> times the frequency and so closes on
    /// itself that many times more often. The sum therefore keeps the period of the
    /// first octave — provided the lacunarity is a whole number, which is why a
    /// fractional one turns the wrap off rather than quietly leaving a seam.
    /// </summary>
    public float Fractal(float x, float y, int octaves, float lacunarity = 2f, float gain = 0.5f, int periodX = 0)
    {
        int period = lacunarity == MathF.Floor(lacunarity) ? periodX : 0;

        float sum = 0f, amp = 1f, freq = 1f, norm = 0f;
        for (int i = 0; i < octaves; i++)
        {
            sum += Sample(x * freq, y * freq, period) * amp;
            norm += amp;
            amp *= gain;
            freq *= lacunarity;
            period *= (int)lacunarity;
        }
        return sum / norm;
    }

    /// <summary>Fractal noise remapped to [0, 1].</summary>
    public float Fractal01(float x, float y, int octaves, float lacunarity = 2f, float gain = 0.5f, int periodX = 0)
        => Fractal(x, y, octaves, lacunarity, gain, periodX) * 0.5f + 0.5f;

    /// <summary>Modulo that returns a non-negative result for negative inputs.</summary>
    private static int Mod(int a, int m)
    {
        int r = a % m;
        return r < 0 ? r + m : r;
    }

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
