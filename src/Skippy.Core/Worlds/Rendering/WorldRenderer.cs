using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Skippy.Core.Worlds.Biomes;

namespace Skippy.Core.Worlds.Rendering;

/// <summary>
/// Draws the visible part of a <see cref="World"/> in isometric projection with
/// smooth, Age-of-Empires-II-style terrain: elevation lives at tile corners, so
/// each tile is a four-corner quad that ramps between neighbours instead of
/// stepping with vertical cliffs.
///
/// Terrain is drawn as colored triangles (two per tile) through a
/// <see cref="BasicEffect"/>; vegetation is stamped on top with a
/// <see cref="SpriteBatch"/> using runtime-generated art. No content assets.
/// </summary>
public sealed class WorldRenderer
{
    private readonly BasicEffect _effect;
    private VertexPositionColor[] _verts = new VertexPositionColor[6 * 8192];
    private int _vertCount;

    private readonly List<VegItem> _veg = new();

    private readonly Texture2D _pixel;
    private readonly Texture2D _disc;
    private readonly Texture2D _triangle;

    private static readonly Color TrunkColor = new(86, 60, 36);

    private readonly struct VegItem
    {
        public readonly Vector2 Pos;
        public readonly Vegetation Veg;
        public readonly float Scale;
        public VegItem(Vector2 pos, Vegetation veg, float scale) { Pos = pos; Veg = veg; Scale = scale; }
    }

    /// <summary>Deterministic per-tile RNG for scattering plants (no heap allocation).</summary>
    private struct XorShift
    {
        private uint _s;
        public XorShift(uint seed) { _s = seed == 0 ? 1u : seed; }
        public float Next() { _s ^= _s << 13; _s ^= _s >> 17; _s ^= _s << 5; return (_s & 0xFFFFFF) / 16777216f; }
    }

    public WorldRenderer(GraphicsDevice gd)
    {
        _effect = new BasicEffect(gd)
        {
            VertexColorEnabled = true,
            LightingEnabled = false,
            TextureEnabled = false,
            World = Matrix.Identity,
            View = Matrix.Identity,
        };

        _pixel = Solid(gd, 1, 1);
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

        int lift = (WorldConstants.MaxLevel * Iso.HeightStep) / Iso.TileHeight + 3;
        maxX += lift;
        maxY += lift;

        // Corner elevation → screen position (shared corners keep the mesh seamless).
        Vector2 Corner(int vx, int vy) =>
            center + (Iso.TileToIso(vx, vy, LevelAt(world, vx, vy)) - focusIso) * zoom;

        _vertCount = 0;
        _veg.Clear();

        for (int sum = minX + minY; sum <= maxX + maxY; sum++)
        {
            int txStart = Math.Max(minX, sum - maxY);
            int txEnd = Math.Min(maxX, sum - minY);
            for (int tx = txStart; tx <= txEnd; tx++)
            {
                int ty = sum - tx;
                if (!world.TryGetTile(tx, ty, out Tile tile))
                    continue;

                int lA = LevelAt(world, tx, ty);
                int lB = LevelAt(world, tx + 1, ty);
                int lC = LevelAt(world, tx + 1, ty + 1);
                int lD = LevelAt(world, tx, ty + 1);

                Vector2 a = Corner(tx, ty);
                Vector2 b = Corner(tx + 1, ty);
                Vector2 c = Corner(tx + 1, ty + 1);
                Vector2 d = Corner(tx, ty + 1);

                float avgLevel = (lA + lB + lC + lD) * 0.25f;
                float elevShade = 0.82f + 0.30f * (avgLevel / WorldConstants.MaxLevel);
                // Slope lighting: faces whose north corner sits above the south catch light.
                float slope = (lA - lC) + 0.4f * (lB - lD);
                float slopeShade = Math.Clamp(1f + 0.05f * slope, 0.8f, 1.2f);
                float jitter = 0.95f + 0.10f * Hash01(tx, ty); // break up flat color per tile
                Color col = Scale(BiomeRegistry.TopColor(tile.Biome), elevShade * slopeShade * jitter);

                PushTri(a, b, c, col);
                PushTri(a, c, d, col);

                AppendScatter(tile, a, b, c, d, tx, ty);
            }
        }

        // ---- terrain pass (colored triangles) ----
        gd.BlendState = BlendState.AlphaBlend;
        gd.DepthStencilState = DepthStencilState.None;
        gd.RasterizerState = RasterizerState.CullNone;
        _effect.Projection = Matrix.CreateOrthographicOffCenter(
            0, gd.Viewport.Width, gd.Viewport.Height, 0, 0, 1);

        if (_vertCount >= 3)
        {
            foreach (EffectPass pass in _effect.CurrentTechnique.Passes)
            {
                pass.Apply();
                gd.DrawUserPrimitives(PrimitiveType.TriangleList, _verts, 0, _vertCount / 3);
            }
        }

        // ---- vegetation pass (sprites) ----
        sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
        foreach (VegItem item in _veg)
            DrawVegetation(sb, item.Pos, zoom * item.Scale, item.Veg);
        sb.End();
    }

    /// <summary>
    /// AoE2-style vegetation: a tile scatters many individual plants at randomized,
    /// deterministic positions within its quad (dense forests, sparse savanna),
    /// their count driven by biome and the tile's vegetation-density field.
    /// </summary>
    private void AppendScatter(in Tile tile, Vector2 a, Vector2 b, Vector2 c, Vector2 d, int tx, int ty)
    {
        var rng = new XorShift((uint)((tx * 73856093) ^ (ty * 19349663) ^ 0x9e37));
        float dens = tile.VegetationDensity;

        void Add(Vegetation kind, int n)
        {
            if (n > 12) n = 12;
            for (int i = 0; i < n; i++)
            {
                float u = 0.14f + 0.72f * rng.Next();
                float v = 0.12f + 0.76f * rng.Next();
                float s = 0.8f + 0.5f * rng.Next();
                _veg.Add(new VegItem(Bilerp(a, b, c, d, u, v), kind, s));
            }
        }

        switch (tile.Biome)
        {
            case Biome.TemperateForest: Add(Vegetation.BroadleafTree, 5 + Ri(dens * 6)); Add(Vegetation.Bush, Ri(dens * 2)); break;
            case Biome.Taiga: Add(Vegetation.PineTree, 5 + Ri(dens * 6)); if (rng.Next() > 0.7f) Add(Vegetation.Bush, 1); break;
            case Biome.Jungle: Add(Vegetation.PalmTree, 6 + Ri(dens * 6)); Add(Vegetation.Bush, 1 + Ri(dens * 2)); break;
            case Biome.Grassland:
                if (dens > 0.5f) Add(Vegetation.BroadleafTree, 1 + Ri((dens - 0.5f) * 6));
                if (dens > 0.35f && rng.Next() > 0.5f) Add(Vegetation.Bush, 1);
                break;
            case Biome.Savanna: if (dens > 0.55f) Add(Vegetation.BroadleafTree, dens > 0.8f ? 2 : 1); break;
            case Biome.Swamp: Add(Vegetation.Bush, 1 + Ri(dens * 3)); if (dens > 0.6f) Add(Vegetation.BroadleafTree, 1); break;
            case Biome.Tundra: if (dens > 0.6f || rng.Next() > 0.75f) Add(Vegetation.Bush, 1); break;
            case Biome.Desert: if (dens > 0.7f) Add(Vegetation.Cactus, dens > 0.88f ? 2 : 1); break;
            case Biome.Rock: if (rng.Next() > 0.6f) Add(Vegetation.Rock, rng.Next() > 0.6f ? 2 : 1); break;
        }
    }

    private static int Ri(float x) => (int)MathF.Round(x);

    private static Vector2 Bilerp(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float u, float v)
        => Vector2.Lerp(Vector2.Lerp(a, b, u), Vector2.Lerp(d, c, u), v);

    private void PushTri(Vector2 p0, Vector2 p1, Vector2 p2, Color color)
    {
        if (_vertCount + 3 > _verts.Length)
            Array.Resize(ref _verts, _verts.Length * 2);
        _verts[_vertCount++] = new VertexPositionColor(new Vector3(p0, 0f), color);
        _verts[_vertCount++] = new VertexPositionColor(new Vector3(p1, 0f), color);
        _verts[_vertCount++] = new VertexPositionColor(new Vector3(p2, 0f), color);
    }

    private void DrawVegetation(SpriteBatch sb, Vector2 at, float zoom, Vegetation veg)
    {
        // Far zoom: a cheap dot keeps big, plant-dense views fast.
        if (zoom < 0.35f)
        {
            Color lc = veg switch
            {
                Vegetation.PineTree => new Color(46, 96, 60),
                Vegetation.PalmTree => new Color(90, 168, 84),
                Vegetation.BroadleafTree or Vegetation.Bush => new Color(64, 120, 60),
                Vegetation.Cactus => new Color(80, 124, 80),
                Vegetation.Rock => new Color(120, 116, 112),
                _ => new Color(96, 140, 74),
            };
            Disc(sb, at - new Vector2(0f, 2f * zoom), MathF.Max(1.3f, 2.4f * zoom), lc);
            return;
        }

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

    // ---- sprite helpers --------------------------------------------------

    private void Trunk(SpriteBatch sb, Vector2 baseAt, float zoom, float height)
        => Sprite(sb, _pixel, baseAt, new Vector2(2.2f, height) * zoom, TrunkColor, 0.5f, 1f);

    private void Disc(SpriteBatch sb, Vector2 pos, float diameter, Color color)
        => sb.Draw(_disc, pos, null, color, 0f, new Vector2(8f), new Vector2(diameter / 16f),
                   SpriteEffects.None, 0f);

    private void Triangle(SpriteBatch sb, Vector2 basePos, float width, float height, Color color)
        => sb.Draw(_triangle, basePos, null, color, 0f, new Vector2(8f, 16f),
                   new Vector2(width / 16f, height / 16f), SpriteEffects.None, 0f);

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

    private static float Hash01(int x, int y)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263);
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFF) / 65535f;
        }
    }

    private static Color Scale(Color c, float f) => new(
        (int)Math.Clamp(c.R * f, 0, 255),
        (int)Math.Clamp(c.G * f, 0, 255),
        (int)Math.Clamp(c.B * f, 0, 255));
}
