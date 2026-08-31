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
///     terrain, surf, grass, cactus and rocks are drawn from — and, painted the same
///     way, the animals that walk about on it (see <see cref="SheepArt"/>).
///   • The painted sprite sheets in <c>Assets/</c>, which are cut up into individual
///     plants and packed in alongside them.
///   • A snow cap baked from each of those plants (see <see cref="SnowCap"/>), packed
///     next to the art it came from.
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
    /// What still counts as backdrop card while flooding: near enough to one of the
    /// backdrop's own shades, and near enough to colourless. Measured off the sheets:
    /// a shade varies by up to ~9 from corner to middle and carries almost no colour,
    /// while the faintest foliage already has a chroma around 9.
    ///
    /// The shades are read off the sheet's border rather than assumed, because a sheet
    /// exported flattened from an image editor arrives with the editor's transparency
    /// *checkerboard* baked into it — two greys around 15 apart, which no single
    /// backdrop colour covers.
    /// </summary>
    private const int CardDistance = 11;
    private const int CardChroma = 8;
    private const int MaxCardShades = 4;

    /// <summary>
    /// Width over height above which a piece in the bare row is a shrub rather than a
    /// dead tree: bare wood is drawn tall and narrow, a leafless bush squat and wide.
    /// </summary>
    private const float BareBushAspect = 0.65f;

    /// <summary>
    /// How high up a piece its widest part has to sit (0 at the foot, 1 at the crown)
    /// for it to be a palm rather than a conifer. A palm carries everything in a burst
    /// on top of a bare trunk; a conifer's skirt is widest down at its foot.
    /// </summary>
    private const float PalmCrown = 0.6f;

    public Texture2D Texture { get; }

    public Region White { get; }
    public Region Disc { get; }

    /// <summary>A disc that fades out towards its rim — what plant shadows are pooled from.</summary>
    public Region SoftDisc { get; }

    /// <summary>A sheep, painted facing right. The renderer mirrors it to turn one round.</summary>
    public Region Sheep { get; }

    /// <summary>The 12 painted ground textures, in the sheet's row-major order (see the biome map in <see cref="WorldRenderer"/>).</summary>
    public IReadOnlyList<Region> Terrain { get; }

    /// <summary>Columns and rows the terrain sheet is cut into, and the wrap-around margin baked around each tile.</summary>
    private const int TerrainCols = 4, TerrainRows = 3, TerrainPad = 8;

    /// <summary>Living conifers, living broadleaves, palms, and standing deadwood.</summary>
    public IReadOnlyList<PlantArt> Conifers { get; }
    public IReadOnlyList<PlantArt> Broadleaves { get; }
    public IReadOnlyList<PlantArt> Palms { get; }
    public IReadOnlyList<PlantArt> DeadTrees { get; }

    /// <summary>Leafy bushes, and bare twiggy ones.</summary>
    public IReadOnlyList<PlantArt> Bushes { get; }
    public IReadOnlyList<PlantArt> DeadBushes { get; }

    /// <summary>
    /// One plant's painted art together with the snow cap baked from it. The cap is a
    /// white sprite that shares the art's outline exactly — it is drawn over the plant
    /// at the same size, so snow can only land where the plant's own pixels are and
    /// never on the transparent part of the billboard. See <see cref="SnowCap"/>.
    /// </summary>
    public readonly struct PlantArt
    {
        public readonly Region Art;
        public readonly Region Snow;

        public PlantArt(Region art, Region snow)
        {
            Art = art;
            Snow = snow;
        }
    }

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

        /// <summary>The snow cap baked from this piece, packed alongside it (plants only).</summary>
        public Piece? Cap;
    }

    public TextureAtlas(GraphicsDevice gd)
    {
        var white = Solid(8, 8);
        var disc = Disc16();
        var softDisc = SoftDisc32();
        var sheep = SheepArt();

        // Row bands come out top to bottom, which is how the sheet is laid out. One sheet
        // carries every plant now — its lower rows hold the bushes that used to come off a
        // second sheet of their own.
        List<List<Cut>> trees = LoadSheet(gd, "Skippy.Assets.trees.png");

        // The ground textures, cut from a plain grid (no card to key out), each wrapped
        // by a margin so the atlas gutter carries the opposite edge — that is what lets a
        // tile repeat seamlessly across the world under linear filtering.
        List<Piece> terrain = LoadTerrainSheet(gd, "Skippy.Assets.terrain.png");

        var deadTrees = new List<Cut>();
        var deadBushes = new List<Cut>();
        var broadleaves = new List<Cut>();
        var conifers = new List<Cut>();
        var palms = new List<Cut>();
        var leafy = new List<Cut>();
        Sort(trees, deadTrees, deadBushes, broadleaves, conifers, palms, leafy);

        // Trees and bushes are resampled to their own heights, so a plant's size on
        // screen comes from what it is rather than from how big it happened to be drawn.
        List<Piece> deadTreeArt = Resample(deadTrees, TreeAtlasHeight);
        List<Piece> broadleafArt = Resample(broadleaves, TreeAtlasHeight);
        List<Piece> coniferArt = Resample(conifers, TreeAtlasHeight);
        List<Piece> palmArt = Resample(palms, TreeAtlasHeight);
        List<Piece> bushArt = Resample(leafy, BushAtlasHeight);
        List<Piece> deadBushArt = Resample(deadBushes, BushAtlasHeight);

        var all = new List<Piece> { white, disc, softDisc, sheep };
        foreach (List<Piece> set in new[] { deadTreeArt, broadleafArt, coniferArt, palmArt, bushArt, deadBushArt })
        {
            BakeCaps(set);      // every plant gets a snow cap baked off its own pixels...
            AddWithCaps(all, set); // ...packed next to it
        }
        all.AddRange(terrain);

        Texture = Pack(gd, all, out int atlasWidth, out int atlasHeight);

        White = new Region(white.Placed, atlasWidth, atlasHeight);
        Disc = new Region(disc.Placed, atlasWidth, atlasHeight);
        SoftDisc = new Region(softDisc.Placed, atlasWidth, atlasHeight);
        Sheep = new Region(sheep.Placed, atlasWidth, atlasHeight);

        var terrainRegions = new List<Region>(terrain.Count);
        foreach (Piece p in terrain)
        {
            // The sampled region is the tile itself, inside its wrap margin.
            var inner = new Rectangle(p.Placed.X + TerrainPad, p.Placed.Y + TerrainPad,
                                      p.Width - 2 * TerrainPad, p.Height - 2 * TerrainPad);
            terrainRegions.Add(new Region(inner, atlasWidth, atlasHeight));
        }
        Terrain = terrainRegions;

        DeadTrees = Art(deadTreeArt, atlasWidth, atlasHeight);
        Broadleaves = Art(broadleafArt, atlasWidth, atlasHeight);
        Conifers = Art(coniferArt, atlasWidth, atlasHeight);
        Bushes = Art(bushArt, atlasWidth, atlasHeight);
        DeadBushes = Art(deadBushArt, atlasWidth, atlasHeight);

        // A sheet with no palms on it falls back to the broadleaves, as it used to.
        List<PlantArt> palmSet = Art(palmArt, atlasWidth, atlasHeight);
        Palms = palmSet.Count > 0 ? palmSet : Broadleaves;
    }

    /// <summary>
    /// Sorts what was cut out of the sheet into the kinds the world plants.
    ///
    /// The sheet is painted in rows and the rows carry the meaning: bare wood first, then
    /// broadleaves, then conifers, then bushes for every row after that. Two of those rows
    /// hold more than one kind, and those are split by the shape of the art itself rather
    /// than by counting columns, so the sheet can be repainted with more (or fewer) of
    /// anything without this having to change: a squat piece among the bare wood is a
    /// leafless bush, and a piece carrying its width up at the top is a palm.
    /// </summary>
    private static void Sort(List<List<Cut>> trees,
                            List<Cut> deadTrees, List<Cut> deadBushes, List<Cut> broadleaves,
                            List<Cut> conifers, List<Cut> palms, List<Cut> leafy)
    {
        for (int band = 0; band < trees.Count; band++)
            foreach (Cut c in trees[band])
            {
                switch (band)
                {
                    case 0: (c.Aspect > BareBushAspect ? deadBushes : deadTrees).Add(c); break;
                    case 1: broadleaves.Add(c); break;
                    case 2: (c.Crown > PalmCrown ? palms : conifers).Add(c); break;
                    default: leafy.Add(c); break;
                }
            }
    }

    public void Dispose() => Texture.Dispose();

    /// <summary>Resamples one kind of plant to the height that kind is stored at.</summary>
    private static List<Piece> Resample(List<Cut> cuts, int targetHeight)
    {
        var pieces = new List<Piece>(cuts.Count);
        foreach (Cut c in cuts)
            pieces.Add(c.Resample(targetHeight));
        return pieces;
    }

    /// <summary>Pairs each packed piece with its snow cap, now that both have a place in the atlas.</summary>
    private static List<PlantArt> Art(List<Piece> pieces, int atlasWidth, int atlasHeight)
    {
        var set = new List<PlantArt>(pieces.Count);
        foreach (Piece p in pieces)
            set.Add(new PlantArt(new Region(p.Placed, atlasWidth, atlasHeight),
                                 new Region(p.Cap!.Placed, atlasWidth, atlasHeight)));
        return set;
    }

    private static void BakeCaps(List<Piece> band)
    {
        foreach (Piece p in band)
            p.Cap = SnowCap(p);
    }

    private static void AddWithCaps(List<Piece> all, List<Piece> band)
    {
        foreach (Piece p in band)
        {
            all.Add(p);
            if (p.Cap is not null)
                all.Add(p.Cap);
        }
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
    /// <summary>
    /// How far each ground texture is washed towards white as it is packed, in the sheet's
    /// row-major order. Only the frost-dirt tile the tundra wears is touched: painted, it is
    /// still mostly bare earth, and frozen ground wants to read pale and cold from a
    /// distance — well before it is cold enough for snow to actually lie on it.
    ///
    /// It is done here, once, rather than as a tint at draw time because a vertex colour can
    /// only ever darken a texture; making ground *lighter* means changing the pixels.
    /// </summary>
    private static readonly float[] GroundWash = { 0f, 0f, 0f, 0f, 0f, 0.58f, 0f, 0f, 0f, 0f, 0f, 0f };

    /// <summary>
    /// How far each ground texture is flattened toward its own average colour as it is packed,
    /// in the sheet's row-major order. This is what makes the tiles read as simple, calm ground
    /// rather than busy speckle: pulling every pixel partway to the tile's mean drains the small
    /// colour-to-colour variation while leaving just enough texture to see the surface. The two
    /// water tiles (8, 9) are left alone — their ripple is the whole point of them — and the
    /// bare rock the mountains wear (6) is flattened hardest, on its way to grey (below).
    /// </summary>
    private static readonly float[] GroundFlatten = { 0.5f, 0.5f, 0.45f, 0.5f, 0.45f, 0.5f, 0.62f, 0.5f, 0f, 0f, 0.5f, 0.5f };

    /// <summary>The neutral stone grey the rock tile is pulled toward, so the mountains read as bare grey rock.</summary>
    private static readonly Color RockGrey = new(124, 121, 118);

    /// <summary>Mixes a colour towards white by <paramref name="amount"/> (0 leaves it alone).</summary>
    private static Color Whiten(Color c, float amount)
        => amount <= 0f ? c : new Color(
            (int)(c.R + (255 - c.R) * amount),
            (int)(c.G + (255 - c.G) * amount),
            (int)(c.B + (255 - c.B) * amount));

    /// <summary>Mixes two colours; <paramref name="t"/> = 0 is <paramref name="a"/>, 1 is <paramref name="b"/>.</summary>
    private static Color Mix(Color a, Color b, float t) => new(
        (int)(a.R + (b.R - a.R) * t),
        (int)(a.G + (b.G - a.G) * t),
        (int)(a.B + (b.B - a.B) * t));

    /// <summary>
    /// Drains a colour of its hue: partway to its own grey (its luminance) by
    /// <paramref name="amount"/>, then a touch toward the neutral stone <see cref="RockGrey"/>,
    /// so the rock tile comes out as grey stone whatever tint the source art carried.
    /// </summary>
    private static Color Greyify(Color c, float amount)
    {
        int lum = (int)(0.30f * c.R + 0.59f * c.G + 0.11f * c.B);
        Color desat = Mix(c, new Color(lum, lum, lum), amount);
        return Mix(desat, RockGrey, 0.3f);
    }

    /// <summary>The mean colour of a tile in the source sheet, used to flatten it toward one calm shade.</summary>
    private static Color TileMean(Color[] src, int sw, int tileCol, int tileRow, int tw, int th)
    {
        long sr = 0, sg = 0, sb = 0;
        for (int y = 0; y < th; y++)
            for (int x = 0; x < tw; x++)
            {
                Color p = src[(tileRow * th + y) * sw + (tileCol * tw + x)];
                sr += p.R; sg += p.G; sb += p.B;
            }
        int count = tw * th;
        return new Color((int)(sr / count), (int)(sg / count), (int)(sb / count));
    }

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
                int idx = r * TerrainCols + c;
                int pw = tw + 2 * TerrainPad, ph = th + 2 * TerrainPad;
                float wash = GroundWash[idx];
                float flatten = GroundFlatten[idx];
                bool rock = idx == 6;

                // The tile's own mean, to flatten each pixel toward — computed once up front.
                Color mean = flatten > 0f ? TileMean(src, sw, c, r, tw, th) : default;

                var outPx = new Color[pw * ph];
                for (int y = 0; y < ph; y++)
                {
                    int syt = ((y - TerrainPad) % th + th) % th; // wrap into the tile
                    for (int x = 0; x < pw; x++)
                    {
                        int sxt = ((x - TerrainPad) % tw + tw) % tw;
                        Color p = src[(r * th + syt) * sw + (c * tw + sxt)];
                        if (flatten > 0f) p = Mix(p, mean, flatten); // calmer, simpler ground
                        if (rock) p = Greyify(p, 0.7f);              // grey mountain stone
                        outPx[y * pw + x] = Whiten(p, wash);
                    }
                }
                pieces.Add(new Piece { Pixels = outPx, Width = pw, Height = ph });
            }
        }
        return pieces;
    }

    // ---- loading and cutting up a sprite sheet --------------------------------

    private static List<List<Cut>> LoadSheet(GraphicsDevice gd, string resource)
    {
        using Stream stream = typeof(TextureAtlas).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded asset '{resource}' is missing.");

        using Texture2D sheet = Texture2D.FromStream(gd, stream);
        var pixels = new Color[sheet.Width * sheet.Height];
        sheet.GetData(pixels);

        KeyOutBackground(pixels, sheet.Width, sheet.Height);
        Premultiply(pixels);
        return Segment(pixels, sheet.Width, sheet.Height);
    }

    /// <summary>
    /// Turns the sheet's backdrop into transparency, by flooding inwards from its edges.
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
    /// stops the flood by being either far from every backdrop shade or coloured
    /// enough to not be backdrop at all.
    /// </summary>
    private static void KeyOutBackground(Color[] pixels, int width, int height)
    {
        List<Color> shades = CardShades(pixels, width, height);
        var isBackdrop = new bool[pixels.Length];
        var queue = new Queue<int>();

        void Flood(int i)
        {
            if (isBackdrop[i] || !IsCard(pixels[i], shades))
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
    /// The shades the sheet's backdrop is made of, read off its border. A card painted
    /// flat gives one; a sheet exported flattened out of an image editor gives the two
    /// greys of the editor's transparency checkerboard, and nothing but taking both as
    /// backdrop will key it out. Anything with colour in it is ignored, so a plant
    /// running to the edge of the sheet cannot be mistaken for backdrop.
    /// </summary>
    private static List<Color> CardShades(Color[] pixels, int width, int height)
    {
        var shades = new List<Color>();
        var sums = new List<(long R, long G, long B, int Count)>();

        void Feed(Color c)
        {
            if (Chroma(c) > CardChroma)
                return;

            for (int i = 0; i < shades.Count; i++)
            {
                if (Distance(c, shades[i]) > CardDistance)
                    continue;
                (long r, long g, long b, int n) = sums[i];
                sums[i] = (r + c.R, g + c.G, b + c.B, n + 1);
                shades[i] = new Color((int)((r + c.R) / (n + 1)), (int)((g + c.G) / (n + 1)), (int)((b + c.B) / (n + 1)));
                return;
            }

            if (shades.Count >= MaxCardShades)
                return;
            shades.Add(c);
            sums.Add((c.R, c.G, c.B, 1));
        }

        for (int x = 0; x < width; x++)
        {
            Feed(pixels[x]);
            Feed(pixels[(height - 1) * width + x]);
        }
        for (int y = 0; y < height; y++)
        {
            Feed(pixels[y * width]);
            Feed(pixels[y * width + width - 1]);
        }
        return shades;
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
    /// Whether a pixel is backdrop: close to one of its shades *and* near-neutral.
    /// Colour is what saves dark foliage, which can be dimmer than the card itself.
    /// </summary>
    private static bool IsCard(Color c, List<Color> shades)
    {
        if (Chroma(c) > CardChroma)
            return false;
        foreach (Color shade in shades)
            if (Distance(c, shade) <= CardDistance)
                return true;
        return false;
    }

    private static int Distance(Color a, Color b)
        => Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));

    private static int Chroma(Color c)
        => Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B));

    /// <summary>
    /// Finds the sprites on a keyed sheet by looking for the empty gaps between them,
    /// rather than assuming a grid — the rows on these sheets are not evenly spaced,
    /// and a fixed grid would slice the tall trees in half.
    /// </summary>
    private static List<List<Cut>> Segment(Color[] px, int w, int h)
    {
        var bands = new List<List<Cut>>();

        foreach ((int y0, int y1) in Runs(y =>
        {
            for (int x = 0; x < w; x++)
                if (px[y * w + x].A > InkAlpha) return true;
            return false;
        }, h))
        {
            var row = new List<Cut>();
            foreach ((int x0, int x1) in Runs(x =>
            {
                for (int y = y0; y <= y1; y++)
                    if (px[y * w + x].A > InkAlpha) return true;
                return false;
            }, w))
            {
                row.Add(Cut.From(px, w, x0, y0, x1, y1));
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

    /// <summary>
    /// One sprite lifted off a sheet at the sheet's own resolution, with the two
    /// measurements the sort uses to tell one kind of plant from another.
    /// </summary>
    private sealed class Cut
    {
        public Color[] Pixels = Array.Empty<Color>();
        public int Width, Height;

        /// <summary>Width over height: a leafless bush is squat, standing deadwood is not.</summary>
        public float Aspect => Height == 0 ? 1f : Width / (float)Height;

        /// <summary>Where its widest part sits, 0 at the foot and 1 at the crown.</summary>
        public float Crown;

        /// <summary>
        /// Cuts one sprite out of the sheet. The column run gives its sides; its top and
        /// bottom are found here rather than taken from the band, so a knee-high shrub
        /// painted in the same row as a tree is its own height and not the row's.
        /// </summary>
        public static Cut From(Color[] px, int w, int x0, int y0, int x1, int y1)
        {
            int top = int.MaxValue, bottom = int.MinValue;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    if (px[y * w + x].A > InkAlpha)
                    {
                        if (y < top) top = y;
                        if (y > bottom) bottom = y;
                        break;
                    }
            if (top > bottom) { top = y0; bottom = y1; }

            int sw = x1 - x0 + 1, sh = bottom - top + 1;
            var pixels = new Color[sw * sh];
            var rowWidth = new int[sh];

            for (int y = 0; y < sh; y++)
            {
                int lo = int.MaxValue, hi = int.MinValue;
                for (int x = 0; x < sw; x++)
                {
                    Color c = px[(top + y) * w + x0 + x];
                    pixels[y * sw + x] = c;
                    if (c.A <= InkAlpha) continue;
                    if (x < lo) lo = x;
                    if (x > hi) hi = x;
                }
                rowWidth[y] = hi >= lo ? hi - lo + 1 : 0;
            }

            int widest = 0, maxWidth = 0;
            for (int y = 0; y < sh; y++)
                if (rowWidth[y] > maxWidth) { maxWidth = rowWidth[y]; widest = y; }

            return new Cut
            {
                Pixels = pixels,
                Width = sw,
                Height = sh,
                Crown = sh == 0 ? 0f : 1f - widest / (float)sh,
            };
        }

        /// <summary>Box-filters this sprite down to the height its kind is stored at.</summary>
        public Piece Resample(int targetHeight)
        {
            float scale = MathF.Min(1f, targetHeight / (float)Height);
            int dw = Math.Max(1, (int)MathF.Round(Width * scale));
            int dh = Math.Max(1, (int)MathF.Round(Height * scale));

            var outPx = new Color[dw * dh];
            for (int y = 0; y < dh; y++)
            {
                int sy0 = y * Height / dh;
                int sy1 = Math.Max(sy0 + 1, (y + 1) * Height / dh);

                for (int x = 0; x < dw; x++)
                {
                    int sx0 = x * Width / dw;
                    int sx1 = Math.Max(sx0 + 1, (x + 1) * Width / dw);

                    // Averaging premultiplied values is what makes this correct: a
                    // straight RGB average would drag transparent pixels' colour in.
                    long r = 0, g = 0, b = 0, a = 0;
                    int n = 0;
                    for (int sy = sy0; sy < sy1; sy++)
                    {
                        for (int sx = sx0; sx < sx1; sx++)
                        {
                            Color c = Pixels[sy * Width + sx];
                            r += c.R; g += c.G; b += c.B; a += c.A;
                            n++;
                        }
                    }

                    outPx[y * dw + x] = new Color((int)(r / n), (int)(g / n), (int)(b / n), (int)(a / n));
                }
            }

            return new Piece { Pixels = outPx, Width = dw, Height = dh };
        }
    }

    // ---- baking the snow caps ---------------------------------------------------

    /// <summary>How deep snow lies on a surface, as a fraction of the plant's height, before
    /// the art below it shows through again. Roughly the thickness of the white rim.</summary>
    private const float CapDepth = 0.09f;

    /// <summary>
    /// How quickly art overhead stops sheltering what is under it. Each row down keeps this
    /// much of the cover above, so a branch a few rows clear of the one above it catches snow
    /// of its own instead of staying bare all the way down the canopy.
    /// </summary>
    private const float CapShelter = 0.90f;

    /// <summary>
    /// How much snow settles on a pixel with the whole plant over it — the rime that gets
    /// everywhere rather than the cap that lies on top. Without it only the outermost tips
    /// of a plant ever whitened: a tundra shrub is a dense mass of twigs, so every pixel
    /// but the rim has something above it, and a bush standing in deep snow came out a dark
    /// blob with a white fringe. What is exposed still takes far more than this, so the cap
    /// on top of a bough stays the brightest part of the plant.
    /// </summary>
    private const float CapRimeFloor = 0.45f;

    /// <summary>Gain on the finished cap, so a laden crown reads as snow rather than as haze.</summary>
    private const float CapGain = 2.0f;

    /// <summary>Half the height below which snow thins out — a plant is capped, not whitewashed.</summary>
    private const float CapLowFade = 0.55f;

    /// <summary>
    /// How much of the cap survives all the way down at the plant's foot. It used to be
    /// nothing, which meant no amount of snow could whiten the bottom half of anything —
    /// and the plants standing in the deepest snow on the map are tundra scrub, which is
    /// bare twigs all the way up with no crown to speak of.
    ///
    /// It is only a floor on the *height* fade. What is sheltered from above stays bare
    /// however deep the snow gets, so a dense tree keeps its dark trunk and undersides
    /// while a bush with nothing over it whitens from top to bottom.
    /// </summary>
    private const float CapLowFloor = 0.35f;

    /// <summary>
    /// Bakes the snow that would lie on one plant, as a white sprite the exact shape of the
    /// art it came from. Only pixels the plant actually paints can hold snow, so drawing this
    /// over the plant never puts white on the transparent part of the billboard.
    ///
    /// Where the snow settles is worked out by looking straight up: a pixel is snowy in
    /// proportion to how little of the plant lies above it (decaying with distance, so a
    /// branch clear of the canopy above catches its own), which puts white along every
    /// upward-facing edge — the crown, the tops of the boughs — and leaves the undersides
    /// and the trunk dark. It is stored at half the art's resolution: the result is soft
    /// enough that nothing is lost, and the atlas stays small.
    /// </summary>
    private static Piece SnowCap(Piece art)
    {
        int w = art.Width, h = art.Height;
        var cover = new float[w * h];
        for (int i = 0; i < cover.Length; i++)
            cover[i] = art.Pixels[i].A / 255f;

        float depth = MathF.Max(3f, h * CapDepth);
        var snow = new float[w * h];
        for (int x = 0; x < w; x++)
        {
            float above = 0f;
            for (int y = 0; y < h; y++)
            {
                int i = y * w + x;
                snow[i] = cover[i] * Math.Clamp(1f - above / depth, 0f, 1f);
                above = above * CapShelter + cover[i];
            }
        }

        Blur3(snow, w, h);

        // Thin the snow towards the foot, and mask by the art again: the blur softened the
        // cap outwards, and nothing may lie outside the plant's own outline.
        for (int y = 0; y < h; y++)
        {
            float up = h < 2 ? 1f : 1f - y / (float)(h - 1);
            float bias = Math.Clamp(up / CapLowFade, 0f, 1f);
            bias *= bias * (3f - 2f * bias); // smoothstep
            bias = CapLowFloor + (1f - CapLowFloor) * bias;
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;

                // The cap that lies on top, then the rime under it as a floor. The floor is
                // taken after the gain rather than fed through it: gained, it swamped the
                // cap and every plant came out a featureless white ball.
                float cap = Math.Clamp(snow[i] * CapGain, 0f, 1f);
                snow[i] = MathF.Max(cap, CapRimeFloor) * bias * cover[i];
            }
        }

        // Down to half size, and out as premultiplied white: (a, a, a, a).
        int dw = Math.Max(1, w / 2), dh = Math.Max(1, h / 2);
        var px = new Color[dw * dh];
        for (int y = 0; y < dh; y++)
        {
            for (int x = 0; x < dw; x++)
            {
                int x1 = Math.Min(x * 2 + 1, w - 1), y1 = Math.Min(y * 2 + 1, h - 1);
                float a = 0.25f * (snow[y * 2 * w + x * 2] + snow[y * 2 * w + x1]
                                 + snow[y1 * w + x * 2] + snow[y1 * w + x1]);
                int v = (int)(a * 255f);
                px[y * dw + x] = new Color(v, v, v, v);
            }
        }

        return new Piece { Pixels = px, Width = dw, Height = dh };
    }

    /// <summary>A 3×3 box blur in place, clamping at the edges.</summary>
    private static void Blur3(float[] v, int w, int h)
    {
        var tmp = new float[v.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                tmp[y * w + x] = (v[y * w + Math.Max(0, x - 1)]
                                + v[y * w + x]
                                + v[y * w + Math.Min(w - 1, x + 1)]) / 3f;

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                v[y * w + x] = (tmp[Math.Max(0, y - 1) * w + x]
                              + tmp[y * w + x]
                              + tmp[Math.Min(h - 1, y + 1) * w + x]) / 3f;
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

    /// <summary>
    /// A disc that eases off towards its rim instead of stopping dead at it, premultiplied
    /// like everything else in the atlas. A plant's shadow is laid down as several of these
    /// overlapping down its length; a hard edge would show as a ring around every one of
    /// them, where a soft one lets them melt into a single pool.
    /// </summary>
    private static Piece SoftDisc32()
    {
        const int size = 32;

        // Where the fade starts, as a fraction of the radius: solid to here, gone by the rim.
        const float core = 0.45f;

        var px = new Color[size * size];
        float c = size / 2f, rad = size / 2f - 0.5f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f - c) / rad, dy = (y + 0.5f - c) / rad;
                float d = MathF.Sqrt(dx * dx + dy * dy);
                float t = Math.Clamp((d - core) / (1f - core), 0f, 1f);
                px[y * size + x] = Color.White * (1f - t * t * (3f - 2f * t));
            }
        }
        return new Piece { Pixels = px, Width = size, Height = size };
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

    // ---- the animals ----------------------------------------------------------

    /// <summary>
    /// Canvas the sheep is painted on. It draws a dozen-odd pixels tall; the rest is
    /// headroom for zooming in. Half again as wide as it is tall, because that is the
    /// shape of the animal: a deep fleece slung low, on legs that barely show.
    /// </summary>
    private const int SheepWidth = 96, SheepHeight = 60;

    /// <summary>
    /// Subsamples per pixel per axis. The shapes below are hard-edged — a point is in a
    /// circle or it isn't — so this grid is the only anti-aliasing they get, and without
    /// it the fleece comes out with a staircase for an outline.
    /// </summary>
    private const int SheepSupersample = 3;

    private static readonly Color WoolLight = new(247, 245, 239);
    private static readonly Color WoolShade = new(186, 181, 173);
    private static readonly Color SheepFace = new(60, 56, 60);
    private static readonly Color SheepMuzzle = new(84, 78, 80);
    private static readonly Color SheepEar = new(74, 68, 72);
    private static readonly Color SheepLeg = new(62, 58, 62);
    private static readonly Color SheepHoof = new(38, 34, 38);

    /// <summary>
    /// The fleece: a handful of overlapping circles rather than one ellipse, so the
    /// outline comes out lumpy the way wool is. Each is (centre u, centre v, radius) in
    /// the shape space described in <see cref="SheepArt"/>.
    /// </summary>
    private static readonly (float u, float v, float r)[] Fleece =
    {
        (0.13f, 0.32f, 0.09f), (0.22f, 0.28f, 0.13f), (0.34f, 0.22f, 0.14f),
        (0.48f, 0.24f, 0.13f), (0.58f, 0.30f, 0.11f), (0.30f, 0.35f, 0.12f),
        (0.45f, 0.36f, 0.12f),
    };

    /// <summary>
    /// Where the four legs hang, as the u of each; they all run between the same two v.
    /// They start well up inside the fleece, so what shows below it is only the last
    /// stretch of leg — a sheep is mostly wool with feet under it, not an animal on stilts.
    /// </summary>
    private static readonly float[] SheepLegs = { 0.22f, 0.29f, 0.50f, 0.57f };
    private const float LegTop = 0.38f, LegFoot = 0.62f, LegHalfWidth = 0.028f;

    /// <summary>
    /// A sheep in side view, facing right, standing on the bottom edge of its canvas —
    /// which is where the renderer anchors it, so its feet land on the ground.
    ///
    /// It is painted rather than drawn from a sheet for the same reason the grass and the
    /// rocks are: it is a handful of circles, and a handful of circles is less to carry
    /// around than a PNG. Shapes are given in a square space (u across, v down, both in
    /// units of the canvas *width*) so a radius means the same thing on either axis, and
    /// they are tested front to back: the head is in front of the fleece, the fleece in
    /// front of the neck and legs.
    /// </summary>
    private static Piece SheepArt()
    {
        const int w = SheepWidth, h = SheepHeight, ss = SheepSupersample;
        var px = new Color[w * h];
        float samples = ss * ss;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float r = 0f, g = 0f, b = 0f, hits = 0f;
                for (int sy = 0; sy < ss; sy++)
                {
                    for (int sx = 0; sx < ss; sx++)
                    {
                        // Both axes in units of the width, so v runs 0..h/w rather than 0..1.
                        float u = (x + (sx + 0.5f) / ss) / w;
                        float v = (y + (sy + 0.5f) / ss) / w;
                        if (!SheepColorAt(u, v, out Color c))
                            continue;

                        r += c.R; g += c.G; b += c.B; hits++;
                    }
                }

                // Premultiplied, like everything else in the atlas: colour summed over the
                // samples that hit and divided by all of them, which is exactly rgb·alpha.
                px[y * w + x] = new Color((int)(r / samples), (int)(g / samples), (int)(b / samples),
                                          (int)(255f * hits / samples));
            }
        }

        return new Piece { Pixels = px, Width = w, Height = h };
    }

    /// <summary>What the sheep is at one point of its canvas, or false where it isn't.</summary>
    private static bool SheepColorAt(float u, float v, out Color color)
    {
        if (InEllipse(u, v, 0.725f, 0.225f, 0.038f, 0.027f)) { color = SheepEar; return true; }
        if (InEllipse(u, v, 0.850f, 0.330f, 0.040f, 0.033f)) { color = SheepMuzzle; return true; }
        if (InEllipse(u, v, 0.785f, 0.285f, 0.070f, 0.078f)) { color = SheepFace; return true; }

        float lump = FleeceDepth(u, v);
        if (lump > 0f) { color = Wool(lump, v); return true; }

        // The neck only ever shows as the strip between fleece and head.
        if (InEllipse(u, v, 0.660f, 0.310f, 0.065f, 0.065f)) { color = SheepFace; return true; }

        foreach (float leg in SheepLegs)
        {
            if (v < LegTop || v > LegFoot || MathF.Abs(u - leg) > LegHalfWidth)
                continue;

            color = v > LegFoot - 0.035f ? SheepHoof : SheepLeg;
            return true;
        }

        color = default;
        return false;
    }

    /// <summary>How far inside the fleece a point is, 0 at its outline and 1 at the heart of a lump.</summary>
    private static float FleeceDepth(float u, float v)
    {
        float deepest = 0f;
        foreach ((float cu, float cv, float r) in Fleece)
        {
            float du = u - cu, dv = v - cv;
            float depth = 1f - MathF.Sqrt(du * du + dv * dv) / r;
            if (depth > deepest) deepest = depth;
        }
        return deepest;
    }

    /// <summary>
    /// The shade of one point of fleece. The middle of a lump is its lit top and the rim
    /// is the crease between it and the next, which is what makes the wool read as a
    /// cluster of curls rather than one flat blob; on top of that the whole fleece darkens
    /// towards the belly, where the light doesn't reach.
    /// </summary>
    private static Color Wool(float depth, float v)
    {
        float t = Math.Clamp(depth * 3.2f, 0f, 1f);
        t = t * t * (3f - 2f * t);

        float belly = Math.Clamp((v - 0.22f) / 0.22f, 0f, 1f);
        return Mix(WoolShade, WoolLight, t * (1f - 0.45f * belly));
    }

    private static bool InEllipse(float u, float v, float cu, float cv, float ru, float rv)
    {
        float du = (u - cu) / ru, dv = (v - cv) / rv;
        return du * du + dv * dv <= 1f;
    }
}
