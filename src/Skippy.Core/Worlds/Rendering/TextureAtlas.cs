using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Skippy.Core.Worlds.Rendering;

/// <summary>
/// The single "master" texture every piece of world art lives in. Terrain, surf and
/// all vegetation sample from this one texture, so the whole map draws with one
/// texture bound — no texture swaps between draw calls.
///
/// It is assembled at runtime from two sources:
///
///   • A few procedural shapes (a solid block, a disc) generated in code, which the
///     terrain, surf, grass, cactus and rocks are drawn from.
///   • The painted sprite sheets in <c>Assets/</c>, which are cut up into individual
///     plants and packed in alongside them.
///
/// The sheets are embedded in the assembly rather than loaded from disk, so every
/// platform head gets them the same way and the browser build needs no file server.
/// They are painted on a flat dark card rather than on transparency, so the
/// background is keyed out on load (see <see cref="KeyOutBackground"/>).
/// </summary>
public sealed class TextureAtlas : IDisposable
{
    /// <summary>Width the atlas is packed into; its height follows from what fits.</summary>
    private const int AtlasWidth = 1024;

    /// <summary>Transparent margin around every packed sprite, so filtering can't bleed across.</summary>
    private const int Gutter = 4;

    /// <summary>
    /// Height, in atlas pixels, that sheet art is resampled to. Plants draw far
    /// smaller than this; the headroom is what keeps them sharp when zoomed in.
    /// </summary>
    private const int TreeAtlasHeight = 112;
    private const int BushAtlasHeight = 72;

    /// <summary>
    /// What still counts as backdrop card while flooding. Measured off the sheets:
    /// the card varies by up to ~9 from corner to middle and carries almost no
    /// colour, while the faintest foliage already has a chroma around 9.
    /// </summary>
    private const int CardDistance = 11;
    private const int CardChroma = 8;

    public Texture2D Texture { get; }

    public Region White { get; }
    public Region Disc { get; }

    /// <summary>The 12 painted ground textures, in the sheet's row-major order (see the biome map in <see cref="WorldRenderer"/>).</summary>
    public IReadOnlyList<Region> Terrain { get; }

    /// <summary>Columns and rows the terrain sheet is cut into, and the wrap-around margin baked around each tile.</summary>
    private const int TerrainCols = 4, TerrainRows = 3, TerrainPad = 8;

    /// <summary>Living conifers, living broadleaves, and standing deadwood.</summary>
    public IReadOnlyList<Region> Conifers { get; }
    public IReadOnlyList<Region> Broadleaves { get; }
    public IReadOnlyList<Region> DeadTrees { get; }

    /// <summary>Leafy bushes, and bare twiggy ones.</summary>
    public IReadOnlyList<Region> Bushes { get; }
    public IReadOnlyList<Region> DeadBushes { get; }

    /// <summary>A rectangle in the atlas and its UV bounds (u0, v0, u1, v1).</summary>
    public readonly struct Region
    {
        public readonly Rectangle Rect;
        public readonly float U0, V0, U1, V1;

        /// <summary>UV at the centre of the region — safe for solid fills at any scale.</summary>
        public Vector2 CenterUv => new((U0 + U1) * 0.5f, (V0 + V1) * 0.5f);

        /// <summary>Width over height, for drawing art at its own proportions.</summary>
        public float Aspect => Rect.Height == 0 ? 1f : Rect.Width / (float)Rect.Height;

        public Region(Rectangle rect, int atlasWidth, int atlasHeight)
        {
            Rect = rect;
            U0 = rect.Left / (float)atlasWidth;
            V0 = rect.Top / (float)atlasHeight;
            U1 = rect.Right / (float)atlasWidth;
            V1 = rect.Bottom / (float)atlasHeight;
        }
    }

    /// <summary>One piece of art on its way into the atlas.</summary>
    private sealed class Piece
    {
        public Color[] Pixels = Array.Empty<Color>();
        public int Width, Height;
        public Rectangle Placed;
    }

    public TextureAtlas(GraphicsDevice gd)
    {
        var white = Solid(8, 8);
        var disc = Disc16();

        // Row bands come out top to bottom, which is how the sheets are laid out.
        List<List<Piece>> trees = LoadSheet(gd, "Skippy.Assets.trees.png", TreeAtlasHeight);
        List<List<Piece>> bushes = LoadSheet(gd, "Skippy.Assets.bushes.png", BushAtlasHeight);

        // The ground textures, cut from a plain grid (no card to key out), each wrapped
        // by a margin so the atlas gutter carries the opposite edge — that is what lets a
        // tile repeat seamlessly across the world under linear filtering.
        List<Piece> terrain = LoadTerrainSheet(gd, "Skippy.Assets.terrain.png");

        var all = new List<Piece> { white, disc };
        foreach (List<Piece> band in trees) all.AddRange(band);
        foreach (List<Piece> band in bushes) all.AddRange(band);
        all.AddRange(terrain);

        Texture = Pack(gd, all, out int atlasWidth, out int atlasHeight);

        White = new Region(white.Placed, atlasWidth, atlasHeight);
        Disc = new Region(disc.Placed, atlasWidth, atlasHeight);

        var terrainRegions = new List<Region>(terrain.Count);
        foreach (Piece p in terrain)
        {
            // The sampled region is the tile itself, inside its wrap margin.
            var inner = new Rectangle(p.Placed.X + TerrainPad, p.Placed.Y + TerrainPad,
                                      p.Width - 2 * TerrainPad, p.Height - 2 * TerrainPad);
            terrainRegions.Add(new Region(inner, atlasWidth, atlasHeight));
        }
        Terrain = terrainRegions;

        // trees.png: bare shrubs, then standing deadwood, then living trees.
        // Within the living row the sheet alternates conifer / broadleaf.
        List<Region> deadShrubs = Band(trees, 0, atlasWidth, atlasHeight);
        DeadTrees = Band(trees, 1, atlasWidth, atlasHeight);
        List<Region> living = Band(trees, 2, atlasWidth, atlasHeight);

        var conifers = new List<Region>();
        var broadleaves = new List<Region>();
        for (int i = 0; i < living.Count; i++)
            (i % 2 == 0 ? conifers : broadleaves).Add(living[i]);
        Conifers = conifers.Count > 0 ? conifers : living;
        Broadleaves = broadleaves.Count > 0 ? broadleaves : living;

        // bushes.png: bare, then sparsely leafed, then full.
        var leafy = new List<Region>();
        leafy.AddRange(Band(bushes, 1, atlasWidth, atlasHeight));
        leafy.AddRange(Band(bushes, 2, atlasWidth, atlasHeight));
        Bushes = leafy;

        var bare = new List<Region>();
        bare.AddRange(Band(bushes, 0, atlasWidth, atlasHeight));
        bare.AddRange(deadShrubs); // the shrub row off the tree sheet is bare too
        DeadBushes = bare;
    }

    public void Dispose() => Texture.Dispose();

    private static List<Region> Band(List<List<Piece>> bands, int index, int atlasWidth, int atlasHeight)
    {
        var regions = new List<Region>();
        if (index < bands.Count)
            foreach (Piece p in bands[index])
                regions.Add(new Region(p.Placed, atlasWidth, atlasHeight));
        return regions;
    }

    // ---- loading the ground textures ------------------------------------------

    /// <summary>
    /// Cuts the terrain sheet into its <see cref="TerrainCols"/>×<see cref="TerrainRows"/>
    /// grid of ground textures. Unlike the sprite sheets these are full-bleed and opaque,
    /// so there is no background to key out and no gaps to hunt for — just an even grid.
    /// Each tile is copied into a piece with a <see cref="TerrainPad"/> margin filled by
    /// wrapping the tile's opposite edge, so once packed the gutter around it continues
    /// the texture instead of fading to nothing, and the tile stays seamless when it
    /// repeats across the ground.
    /// </summary>
    private static List<Piece> LoadTerrainSheet(GraphicsDevice gd, string resource)
    {
        using Stream stream = typeof(TextureAtlas).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded asset '{resource}' is missing.");

        using Texture2D sheet = Texture2D.FromStream(gd, stream);
        var src = new Color[sheet.Width * sheet.Height];
        sheet.GetData(src);
        int sw = sheet.Width, tw = sw / TerrainCols, th = sheet.Height / TerrainRows;

        var pieces = new List<Piece>(TerrainCols * TerrainRows);
        for (int r = 0; r < TerrainRows; r++)
        {
            for (int c = 0; c < TerrainCols; c++)
            {
                int pw = tw + 2 * TerrainPad, ph = th + 2 * TerrainPad;
                var outPx = new Color[pw * ph];
                for (int y = 0; y < ph; y++)
                {
                    int syt = ((y - TerrainPad) % th + th) % th; // wrap into the tile
                    for (int x = 0; x < pw; x++)
                    {
                        int sxt = ((x - TerrainPad) % tw + tw) % tw;
                        outPx[y * pw + x] = src[(r * th + syt) * sw + (c * tw + sxt)];
                    }
                }
                pieces.Add(new Piece { Pixels = outPx, Width = pw, Height = ph });
            }
        }
        return pieces;
    }

    // ---- loading and cutting up a sprite sheet --------------------------------

    private static List<List<Piece>> LoadSheet(GraphicsDevice gd, string resource, int targetHeight)
    {
        using Stream stream = typeof(TextureAtlas).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded asset '{resource}' is missing.");

        using Texture2D sheet = Texture2D.FromStream(gd, stream);
        var pixels = new Color[sheet.Width * sheet.Height];
        sheet.GetData(pixels);

        KeyOutBackground(pixels, sheet.Width, sheet.Height);
        Premultiply(pixels);
        return Segment(pixels, sheet.Width, sheet.Height, targetHeight);
    }

    /// <summary>
    /// Turns the sheet's flat dark backdrop into transparency, by flooding inwards
    /// from the edges of the sheet.
    ///
    /// Thresholding on "how far is this pixel from the backdrop colour" cannot work
    /// here, and measuring the art says why: the backdrop sits at luminance ~18 with
    /// almost no colour in it, while shaded foliage is *darker than the backdrop* and
    /// only distinguishable by being coloured at all. Any threshold loose enough to
    /// keep that foliage also keeps the soft glow the sheets carry around each
    /// subject, which leaves every plant standing on a visible rectangle of card;
    /// any threshold tight enough to remove the glow eats the foliage.
    ///
    /// Flooding sidesteps the whole problem. Only backdrop that actually connects to
    /// the outside is removed, so shade enclosed by a plant survives however dark it
    /// is, while the card and its glow — which do connect — go completely. A pixel
    /// stops the flood by being either far enough off the backdrop colour or
    /// coloured enough to not be card.
    /// </summary>
    private static void KeyOutBackground(Color[] pixels, int width, int height)
    {
        Color bg = pixels[0];
        var isBackdrop = new bool[pixels.Length];
        var queue = new Queue<int>();

        void Flood(int i)
        {
            if (isBackdrop[i] || !IsCard(pixels[i], bg))
                return;
            isBackdrop[i] = true;
            queue.Enqueue(i);
        }

        for (int x = 0; x < width; x++)
        {
            Flood(x);
            Flood((height - 1) * width + x);
        }
        for (int y = 0; y < height; y++)
        {
            Flood(y * width);
            Flood(y * width + width - 1);
        }

        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            int x = i % width, y = i / width;
            if (x > 0) Flood(i - 1);
            if (x < width - 1) Flood(i + 1);
            if (y > 0) Flood(i - width);
            if (y < height - 1) Flood(i + width);
        }

        // Hard in or out. The box filter that resamples each sprite turns this into
        // smooth edges on its own, so there is nothing to gain from feathering here.
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = isBackdrop[i] ? Color.Transparent : pixels[i];
    }

    /// <summary>
    /// Into premultiplied alpha, which is what the renderer's blend state expects —
    /// and what makes averaging correct when the sprites are resampled and mipped.
    /// </summary>
    private static void Premultiply(Color[] pixels)
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            Color c = pixels[i];
            if (c.A == 255 || c.A == 0)
                continue;

            float a = c.A / 255f;
            pixels[i] = new Color((int)(c.R * a), (int)(c.G * a), (int)(c.B * a), c.A);
        }
    }

    /// <summary>
    /// Whether a pixel is the backdrop card: close to its colour *and* near-neutral.
    /// Colour is what saves dark foliage, which can be dimmer than the card itself.
    /// </summary>
    private static bool IsCard(Color c, Color bg)
    {
        int distance = Math.Max(Math.Abs(c.R - bg.R), Math.Max(Math.Abs(c.G - bg.G), Math.Abs(c.B - bg.B)));
        int chroma = Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B));
        return distance <= CardDistance && chroma <= CardChroma;
    }

    /// <summary>
    /// Finds the sprites on a keyed sheet by looking for the empty gaps between them,
    /// rather than assuming a grid — the rows on these sheets are not evenly spaced,
    /// and a fixed grid would slice the tall trees in half.
    /// </summary>
    private static List<List<Piece>> Segment(Color[] px, int w, int h, int targetHeight)
    {
        var bands = new List<List<Piece>>();

        foreach ((int y0, int y1) in Runs(y =>
        {
            for (int x = 0; x < w; x++)
                if (px[y * w + x].A > InkAlpha) return true;
            return false;
        }, h))
        {
            var row = new List<Piece>();
            foreach ((int x0, int x1) in Runs(x =>
            {
                for (int y = y0; y <= y1; y++)
                    if (px[y * w + x].A > InkAlpha) return true;
                return false;
            }, w))
            {
                row.Add(Resample(px, w, x0, y0, x1, y1, targetHeight));
            }

            if (row.Count > 0)
                bands.Add(row);
        }

        return bands;
    }

    private const int InkAlpha = 24;

    /// <summary>Contiguous runs where <paramref name="hasInk"/> holds, ignoring specks.</summary>
    private static List<(int Start, int End)> Runs(Func<int, bool> hasInk, int count)
    {
        const int MinRun = 8;
        var runs = new List<(int, int)>();
        int start = -1;

        for (int i = 0; i < count; i++)
        {
            if (hasInk(i))
            {
                if (start < 0) start = i;
            }
            else if (start >= 0)
            {
                if (i - start >= MinRun) runs.Add((start, i - 1));
                start = -1;
            }
        }

        if (start >= 0 && count - start >= MinRun)
            runs.Add((start, count - 1));

        return runs;
    }

    /// <summary>Crops one sprite out of the sheet and box-filters it down to size.</summary>
    private static Piece Resample(Color[] px, int w, int x0, int y0, int x1, int y1, int targetHeight)
    {
        int sw = x1 - x0 + 1, sh = y1 - y0 + 1;
        float scale = MathF.Min(1f, targetHeight / (float)sh);
        int dw = Math.Max(1, (int)MathF.Round(sw * scale));
        int dh = Math.Max(1, (int)MathF.Round(sh * scale));

        var outPx = new Color[dw * dh];
        for (int y = 0; y < dh; y++)
        {
            int sy0 = y0 + y * sh / dh;
            int sy1 = Math.Max(sy0 + 1, y0 + (y + 1) * sh / dh);

            for (int x = 0; x < dw; x++)
            {
                int sx0 = x0 + x * sw / dw;
                int sx1 = Math.Max(sx0 + 1, x0 + (x + 1) * sw / dw);

                // Averaging premultiplied values is what makes this correct: a
                // straight RGB average would drag transparent pixels' colour in.
                long r = 0, g = 0, b = 0, a = 0;
                int n = 0;
                for (int sy = sy0; sy < sy1; sy++)
                {
                    for (int sx = sx0; sx < sx1; sx++)
                    {
                        Color c = px[sy * w + sx];
                        r += c.R; g += c.G; b += c.B; a += c.A;
                        n++;
                    }
                }

                outPx[y * dw + x] = new Color((int)(r / n), (int)(g / n), (int)(b / n), (int)(a / n));
            }
        }

        return new Piece { Pixels = outPx, Width = dw, Height = dh };
    }

    // ---- packing ---------------------------------------------------------------

    /// <summary>Shelf-packs every piece into one texture and uploads it with mip levels.</summary>
    private static Texture2D Pack(GraphicsDevice gd, List<Piece> pieces, out int atlasWidth, out int atlasHeight)
    {
        atlasWidth = AtlasWidth;
        int x = Gutter, y = Gutter, shelfHeight = 0;

        foreach (Piece p in pieces)
        {
            if (x + p.Width + Gutter > atlasWidth)
            {
                x = Gutter;
                y += shelfHeight + Gutter;
                shelfHeight = 0;
            }

            p.Placed = new Rectangle(x, y, p.Width, p.Height);
            x += p.Width + Gutter;
            shelfHeight = Math.Max(shelfHeight, p.Height);
        }

        atlasHeight = RoundUpPowerOfTwo(y + shelfHeight + Gutter);

        var data = new Color[atlasWidth * atlasHeight]; // transparent by default
        foreach (Piece p in pieces)
            for (int row = 0; row < p.Height; row++)
                Array.Copy(p.Pixels, row * p.Width,
                           data, (p.Placed.Y + row) * atlasWidth + p.Placed.X, p.Width);

        // Mip levels matter here: plants draw a few times smaller than they are
        // stored, and without them the foliage crawls and sparkles as the camera moves.
        var tex = new Texture2D(gd, atlasWidth, atlasHeight, true, SurfaceFormat.Color);
        tex.SetData(0, null, data, 0, data.Length);

        int mw = atlasWidth, mh = atlasHeight;
        Color[] level = data;
        for (int mip = 1; mw > 1 || mh > 1; mip++)
        {
            int nw = Math.Max(1, mw / 2), nh = Math.Max(1, mh / 2);
            var next = new Color[nw * nh];
            for (int j = 0; j < nh; j++)
            {
                for (int i = 0; i < nw; i++)
                {
                    int i0 = Math.Min(i * 2, mw - 1), i1 = Math.Min(i * 2 + 1, mw - 1);
                    int j0 = Math.Min(j * 2, mh - 1), j1 = Math.Min(j * 2 + 1, mh - 1);
                    Color a = level[j0 * mw + i0], b = level[j0 * mw + i1];
                    Color c = level[j1 * mw + i0], d = level[j1 * mw + i1];
                    next[j * nw + i] = new Color(
                        (a.R + b.R + c.R + d.R) / 4,
                        (a.G + b.G + c.G + d.G) / 4,
                        (a.B + b.B + c.B + d.B) / 4,
                        (a.A + b.A + c.A + d.A) / 4);
                }
            }

            tex.SetData(mip, null, next, 0, next.Length);
            level = next;
            mw = nw;
            mh = nh;
        }

        return tex;
    }

    private static int RoundUpPowerOfTwo(int v)
    {
        int p = 1;
        while (p < v) p <<= 1;
        return p;
    }

    // ---- the procedural shapes -------------------------------------------------

    private static Piece Solid(int w, int h)
    {
        var px = new Color[w * h];
        for (int i = 0; i < px.Length; i++) px[i] = Color.White;
        return new Piece { Pixels = px, Width = w, Height = h };
    }

    private static Piece Disc16()
    {
        const int size = 16;
        var px = new Color[size * size];
        float c = size / 2f, rad = size / 2f - 0.5f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x + 0.5f - c, dy = y + 0.5f - c;
                if (dx * dx + dy * dy <= rad * rad)
                    px[y * size + x] = Color.White;
            }
        }
        return new Piece { Pixels = px, Width = size, Height = size };
    }
}
