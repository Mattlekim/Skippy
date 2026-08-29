using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Skippy.Core.Worlds.Biomes;

namespace Skippy.Core.Worlds.Rendering;

/// <summary>
/// Draws the visible part of a <see cref="World"/> in isometric projection.
///
/// All art is generated at runtime (a diamond, a disc, a triangle, a pixel), so
/// the world renders with zero content-pipeline assets. Tiles are drawn back to
/// front along screen diagonals (painter's algorithm); each tile is a diamond
/// top, shaded by elevation, with dark side faces filling the cliff down to
/// whichever neighbor in front is lower. Vegetation is stamped on top.
/// </summary>
public sealed class WorldRenderer
{
    private readonly Texture2D _pixel;
    private readonly Texture2D _diamond;
    private readonly Texture2D _disc;
    private readonly Texture2D _triangle;

    private static readonly Color TrunkColor = new(86, 60, 36);

    public WorldRenderer(GraphicsDevice gd)
    {
        _pixel = Solid(gd, 1, 1);
        _diamond = MakeDiamond(gd, Iso.TileWidth, Iso.TileHeight);
        _disc = MakeDisc(gd, 16);
        _triangle = MakeTriangle(gd, 16);
    }

    public void Draw(SpriteBatch sb, GraphicsDevice gd, World world)
    {
        Camera cam = world.Camera;
        var center = new Vector2(gd.Viewport.Width / 2f, gd.Viewport.Height / 2f);
        float zoom = cam.Zoom;
        Vector2 focusIso = cam.FocusIso;

        // Screen → world-tile for the four viewport corners, to bound what to draw.
        Vector2 ScreenToTile(float sx, float sy)
        {
            Vector2 iso = (new Vector2(sx, sy) - center) / zoom + focusIso;
            return Iso.IsoToTile(iso.X, iso.Y);
        }

        Vector2 c0 = ScreenToTile(0, 0);
        Vector2 c1 = ScreenToTile(gd.Viewport.Width, 0);
        Vector2 c2 = ScreenToTile(0, gd.Viewport.Height);
        Vector2 c3 = ScreenToTile(gd.Viewport.Width, gd.Viewport.Height);

        int minX = (int)MathF.Floor(Min4(c0.X, c1.X, c2.X, c3.X)) - 2;
        int maxX = (int)MathF.Ceiling(Max4(c0.X, c1.X, c2.X, c3.X)) + 2;
        int minY = (int)MathF.Floor(Min4(c0.Y, c1.Y, c2.Y, c3.Y)) - 2;
        int maxY = (int)MathF.Ceiling(Max4(c0.Y, c1.Y, c2.Y, c3.Y)) + 2;

        // Tall tiles poke up from below the bottom edge, so extend the far side.
        int lift = (WorldConstants.MaxLevel * Iso.HeightStep) / Iso.TileHeight + 2;
        maxX += lift;
        maxY += lift;

        sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);

        for (int sum = minX + minY; sum <= maxX + maxY; sum++)
        {
            int txStart = Math.Max(minX, sum - maxY);
            int txEnd = Math.Min(maxX, sum - minY);
            for (int tx = txStart; tx <= txEnd; tx++)
            {
                int ty = sum - tx;
                if (!world.TryGetTile(tx, ty, out Tile tile))
                    continue;

                Vector2 top = center + (Iso.TileToIso(tx, ty, tile.Level) - focusIso) * zoom;
                DrawTileColumn(sb, world, tx, ty, tile, top, zoom);
            }
        }

        sb.End();
    }

    private void DrawTileColumn(SpriteBatch sb, World world, int tx, int ty, in Tile tile, Vector2 top, float zoom)
    {
        Color baseColor = BiomeRegistry.TopColor(tile.Biome);

        // Cliff faces: fill from just below the top down to whichever front
        // neighbor (south-east / south-west) is lower.
        if (!tile.IsWater && tile.Level > 0)
        {
            int front = Math.Min(LevelAt(world, tx + 1, ty), LevelAt(world, tx, ty + 1));
            Color side = Scale(baseColor, 0.5f);
            for (int lvl = tile.Level - 1; lvl >= front; lvl--)
            {
                Vector2 p = top + new Vector2(0f, (tile.Level - lvl) * Iso.HeightStep * zoom);
                Diamond(sb, p, zoom, side);
            }
        }

        // Top face, brightened with elevation so height reads at a glance.
        float shade = 0.82f + 0.30f * (tile.Level / (float)WorldConstants.MaxLevel);
        Diamond(sb, top, zoom, Scale(baseColor, shade));

        if (tile.Vegetation != Vegetation.None)
            DrawVegetation(sb, top, zoom, tile.Vegetation);
    }

    private void DrawVegetation(SpriteBatch sb, Vector2 tileTop, float zoom, Vegetation veg)
    {
        // Anchor plants a touch above the diamond center so they sit "on" the tile.
        Vector2 at = tileTop + new Vector2(0f, -1f * zoom);

        switch (veg)
        {
            case Vegetation.GrassTuft:
                for (int i = -1; i <= 1; i++)
                    Sprite(sb, _pixel, at + new Vector2(i * 2f * zoom, 0f), new Vector2(1f, 4f) * zoom,
                           new Color(110, 150, 86), 0.5f, 1f);
                break;

            case Vegetation.Bush:
                Disc(sb, at - new Vector2(0f, 3f * zoom), 8f * zoom, new Color(86, 124, 70));
                break;

            case Vegetation.PineTree:
                Trunk(sb, at, zoom, 5f);
                Triangle(sb, at - new Vector2(0f, 4f * zoom), 12f * zoom, 11f * zoom, new Color(40, 86, 54));
                Triangle(sb, at - new Vector2(0f, 9f * zoom), 9f * zoom, 9f * zoom, new Color(46, 96, 60));
                break;

            case Vegetation.BroadleafTree:
                Trunk(sb, at, zoom, 5f);
                Disc(sb, at - new Vector2(0f, 9f * zoom), 13f * zoom, new Color(64, 120, 60));
                break;

            case Vegetation.PalmTree:
                Trunk(sb, at, zoom, 9f);
                Disc(sb, at - new Vector2(0f, 12f * zoom), 11f * zoom, new Color(90, 168, 84));
                break;

            case Vegetation.Cactus:
                Sprite(sb, _disc, at - new Vector2(0f, 6f * zoom), new Vector2(5f, 13f) * zoom / 16f,
                       new Color(80, 124, 80), 0.5f, 1f);
                Disc(sb, at - new Vector2(3f * zoom, 8f * zoom), 4f * zoom, new Color(80, 124, 80));
                Disc(sb, at + new Vector2(3f * zoom, -10f * zoom), 4f * zoom, new Color(80, 124, 80));
                break;

            case Vegetation.Rock:
                Disc(sb, at - new Vector2(0f, 2f * zoom), 8f * zoom, new Color(120, 116, 112));
                break;
        }
    }

    // ---- primitive draws -------------------------------------------------

    private void Diamond(SpriteBatch sb, Vector2 centerPos, float zoom, Color color)
        => sb.Draw(_diamond, centerPos, null, color, 0f,
                   new Vector2(Iso.TileWidth / 2f, Iso.TileHeight / 2f),
                   new Vector2(zoom), SpriteEffects.None, 0f);

    private void Trunk(SpriteBatch sb, Vector2 baseAt, float zoom, float height)
        => Sprite(sb, _pixel, baseAt, new Vector2(2.2f, height) * zoom, TrunkColor, 0.5f, 1f);

    private void Disc(SpriteBatch sb, Vector2 pos, float diameter, Color color)
        => sb.Draw(_disc, pos, null, color, 0f, new Vector2(8f), new Vector2(diameter / 16f),
                   SpriteEffects.None, 0f);

    private void Triangle(SpriteBatch sb, Vector2 basePos, float width, float height, Color color)
        => sb.Draw(_triangle, basePos, null, color, 0f, new Vector2(8f, 16f),
                   new Vector2(width / 16f, height / 16f), SpriteEffects.None, 0f);

    /// <summary>Draw a texture at <paramref name="scale"/> (in units of texture size)
    /// with a fractional origin (0..1 across each axis).</summary>
    private static void Sprite(SpriteBatch sb, Texture2D tex, Vector2 pos, Vector2 scale, Color color,
                               float originU, float originV)
        => sb.Draw(tex, pos, null, color, 0f, new Vector2(tex.Width * originU, tex.Height * originV),
                   scale, SpriteEffects.None, 0f);

    // ---- runtime textures ------------------------------------------------

    private static Texture2D Solid(GraphicsDevice gd, int w, int h)
    {
        var tex = new Texture2D(gd, w, h);
        var data = new Color[w * h];
        for (int i = 0; i < data.Length; i++) data[i] = Color.White;
        tex.SetData(data);
        return tex;
    }

    private static Texture2D MakeDiamond(GraphicsDevice gd, int w, int h)
    {
        var tex = new Texture2D(gd, w, h);
        var data = new Color[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float fx = (x + 0.5f - w / 2f) / (w / 2f);
                float fy = (y + 0.5f - h / 2f) / (h / 2f);
                data[y * w + x] = (MathF.Abs(fx) + MathF.Abs(fy) <= 1f) ? Color.White : Color.Transparent;
            }
        }
        tex.SetData(data);
        return tex;
    }

    private static Texture2D MakeDisc(GraphicsDevice gd, int size)
    {
        var tex = new Texture2D(gd, size, size);
        var data = new Color[size * size];
        float c = size / 2f, r = size / 2f - 0.5f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x + 0.5f - c, dy = y + 0.5f - c;
                data[y * size + x] = (dx * dx + dy * dy <= r * r) ? Color.White : Color.Transparent;
            }
        }
        tex.SetData(data);
        return tex;
    }

    private static Texture2D MakeTriangle(GraphicsDevice gd, int size)
    {
        var tex = new Texture2D(gd, size, size);
        var data = new Color[size * size];
        for (int y = 0; y < size; y++)
        {
            float halfWidth = (y / (float)(size - 1)) * (size / 2f);
            for (int x = 0; x < size; x++)
            {
                bool inside = MathF.Abs(x + 0.5f - size / 2f) <= halfWidth;
                data[y * size + x] = inside ? Color.White : Color.Transparent;
            }
        }
        tex.SetData(data);
        return tex;
    }

    private static float Min4(float a, float b, float c, float d) => MathF.Min(MathF.Min(a, b), MathF.Min(c, d));
    private static float Max4(float a, float b, float c, float d) => MathF.Max(MathF.Max(a, b), MathF.Max(c, d));
    private static int LevelAt(World world, int wx, int wy) => world.TryGetTile(wx, wy, out Tile t) ? t.Level : 0;

    private static Color Scale(Color c, float f) => new(
        (int)Math.Clamp(c.R * f, 0, 255),
        (int)Math.Clamp(c.G * f, 0, 255),
        (int)Math.Clamp(c.B * f, 0, 255));
}
