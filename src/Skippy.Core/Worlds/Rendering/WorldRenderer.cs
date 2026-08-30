using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Skippy.Core.Worlds.Biomes;
using Skippy.Core.Worlds.Generation;

namespace Skippy.Core.Worlds.Rendering;

/// <summary>
/// Draws a <see cref="World"/> in isometric projection with smooth,
/// Age-of-Empires-II-style terrain: elevation lives at tile corners, so each
/// tile is a four-corner quad that ramps between neighbours.
///
/// Rendering strategy:
///   • Every loaded chunk is turned into a static GPU mesh (<see cref="ChunkMesh"/>)
///     containing its terrain AND its vegetation, in iso-space coordinates. The
///     mesh is built once, cached, and only rebuilt when a neighbouring chunk
///     appears (edge vertices blend across the border). Moving the camera changes
///     just the effect's view matrix — no per-frame vertex uploads.
///   • All art comes from one <see cref="TextureAtlas"/>, so the whole map is drawn
///     with a single texture bound and one indexed draw call per chunk segment.
///   • No SpriteBatch is involved in world rendering.
/// </summary>
public sealed class WorldRenderer : IDisposable
{
    private readonly GraphicsDevice _gd;
    private readonly BasicEffect _effect;
    private readonly TextureAtlas _atlas;
    private readonly MeshBuilder _builder;

    private readonly Dictionary<ChunkCoord, ChunkMesh> _meshes = new();

    /// <summary>Tile-grid overlay meshes, built lazily and only while the grid is switched on.</summary>
    private readonly Dictionary<ChunkCoord, ChunkMesh> _gridMeshes = new();

    private readonly List<ChunkCoord> _scratchCoords = new();
    private readonly List<Chunk> _visible = new();
    private readonly List<VegItem> _veg = new();
    private readonly List<ShoreEdge> _shore = new();

    /// <summary>Per-vertex colours for the (Chunk.Size + 1)² corner grid of the chunk being built.</summary>
    private readonly Color[] _cornerColors = new Color[(Chunk.Size + 1) * (Chunk.Size + 1)];

    /// <summary>Which side of the waterline each of those corners came down on.</summary>
    private readonly bool[] _cornerWet = new bool[(Chunk.Size + 1) * (Chunk.Size + 1)];

    /// <summary>Base tints for what is still drawn in code (see <see cref="Vary"/>).</summary>
    private static readonly Color GrassBlade = new(110, 150, 86);
    private static readonly Color CactusFlesh = new(80, 124, 80);
    private static readonly Color RockStone = new(120, 116, 112);

    /// <summary>Surf, premultiplied: bright on the waterline, faint on the swell behind.</summary>
    private static readonly Color FoamNear = Color.White * 0.7f;
    private static readonly Color FoamFar = Color.White * 0.35f;

    /// <summary>Breaker size and spacing along a shoreline, in iso pixels.</summary>
    private const float FoamStroke = 9f;
    private const float FoamSpacing = 13f;
    private static readonly Vector2 BottomCenter = new(0.5f, 1f);
    private static readonly Vector2 Center = new(0.5f, 0.5f);

    /// <summary>
    /// How tall painted plants stand, in iso pixels, before their own size jitter.
    /// Absolute, like every other piece of tile content: a bigger tile is more
    /// ground, not a magnifying glass.
    /// </summary>
    private const float TreeHeight = 34f;
    private const float BushHeight = 16f;

    /// <summary>Tallest sprite (in iso pixels) a tile can stand on itself — pads the mesh bounds for culling.</summary>
    private const float MaxSpriteHeight = TreeHeight * 1.3f;

    /// <summary>
    /// Ceiling on the plants one tile may scatter. Counts grow with tile area, so
    /// without this a huge tile would put six figures of sprites in a single chunk
    /// mesh. It binds above roughly a 150-pixel tile, where forests are already
    /// as dense as they read.
    /// </summary>
    private const int MaxPlantsPerTile = 64;

    /// <summary>
    /// How far from a biome boundary (in climate units) trees reach full density.
    /// Inside this band their numbers fall away, so a forest frays into the open
    /// ground next to it instead of ending on a hard line. Undergrowth is left
    /// alone — it is the canopy that should thin. Kept tight so the fringe is a
    /// thin rind and most of a wood is its dense interior.
    /// </summary>
    private const float TreeEdgeFade = 0.07f;

    /// <summary>Trees never thin below this, so two neighbouring woods don't leave a bald seam.</summary>
    private const float MinTreeEdgeDensity = 0.12f;

    /// <summary>
    /// Extra tree density added at the very core of a true forest (temperate,
    /// taiga, jungle), on top of the open-canopy baseline of 1. At the centre a
    /// wood is (1 + this)× as thick as its edge, so the deep interior packs in;
    /// open biomes that merely hold a few trees are not boosted.
    /// </summary>
    private const float ForestCoreBoost = 1.3f;

    private static bool IsForestBiome(Biome b)
        => b is Biome.TemperateForest or Biome.Taiga or Biome.Jungle;

    /// <summary>Tile-grid overlay: line thickness in iso pixels, and its (premultiplied) colour.</summary>
    private const float GridLineWidth = 1.5f;
    private static readonly Color GridColor = Color.White * 0.4f;

    /// <summary>The <see cref="Iso.Version"/> and map view the cached meshes were built at.</summary>
    private int _isoVersion = Iso.Version;
    private MapView _mapView = MapView.Biome;

    /// <summary>Colour ramps for the data views, walked from the field's 0 to its 1.</summary>
    private static readonly Color[] HeightRamp =
    {
        new(8, 10, 14), new(120, 122, 126), Color.White,
    };

    private static readonly Color[] TemperatureRamp =
    {
        new(30, 60, 170), new(60, 170, 200), new(240, 230, 120), new(200, 60, 40),
    };

    private static readonly Color[] MoistureRamp =
    {
        new(190, 160, 100), new(150, 180, 110), new(70, 150, 150), new(30, 80, 180),
    };

    private readonly struct VegItem
    {
        public readonly Vector2 Pos;
        public readonly Vegetation Veg;
        public readonly float Scale;

        /// <summary>Dead: no foliage, just bare wood.</summary>
        public readonly bool Dead;

        /// <summary>Seeds this plant's own RNG, so its shape and colour are its alone.</summary>
        public readonly uint Seed;

        public VegItem(Vector2 pos, Vegetation veg, float scale, bool dead, uint seed)
        {
            Pos = pos;
            Veg = veg;
            Scale = scale;
            Dead = dead;
            Seed = seed;
        }
    }

    /// <summary>One edge of a water tile that meets land — where surf gets drawn.</summary>
    private readonly struct ShoreEdge
    {
        public readonly Vector2 From, To;

        /// <summary>Unit vector from the edge back into the water.</summary>
        public readonly Vector2 Inward;
        public readonly uint Seed;

        public ShoreEdge(Vector2 from, Vector2 to, Vector2 inward, uint seed)
        {
            From = from;
            To = to;
            Inward = inward;
            Seed = seed;
        }
    }

    /// <summary>Deterministic per-tile RNG for scattering plants (no heap allocation).</summary>
    private struct XorShift
    {
        private uint _s;
        public XorShift(uint seed) { _s = seed == 0 ? 1u : seed; }

        public uint NextBits()
        {
            _s ^= _s << 13;
            _s ^= _s >> 17;
            _s ^= _s << 5;
            return _s;
        }

        public float Next() => (NextBits() & 0xFFFFFF) / 16777216f;

        /// <summary>A count in [min, max].</summary>
        public int Range(int min, int max) => min + (int)(Next() * (max - min + 1));
    }

    public WorldRenderer(GraphicsDevice gd)
    {
        _gd = gd;
        _atlas = new TextureAtlas(gd);
        _builder = new MeshBuilder(gd);
        _effect = new BasicEffect(gd)
        {
            VertexColorEnabled = true,
            TextureEnabled = true,
            LightingEnabled = false,
            Texture = _atlas.Texture,
            World = Matrix.Identity,
        };
    }

    /// <summary>Number of chunk meshes currently resident on the GPU.</summary>
    public int ResidentMeshCount => _meshes.Count;

    public void Draw(GraphicsDevice gd, World world)
    {
        Camera cam = world.Camera;
        var center = new Vector2(gd.Viewport.Width / 2f, gd.Viewport.Height / 2f);
        float zoom = cam.Zoom;
        Vector2 focusIso = cam.FocusIso;

        // Visible rectangle in iso-space.
        Vector2 isoMin = focusIso - center / zoom;
        Vector2 isoMax = focusIso + (new Vector2(gd.Viewport.Width, gd.Viewport.Height) - center) / zoom;

        DropStaleMeshes(world);
        DropUnloadedMeshes(world);

        // Gather visible chunks and make sure each has an up-to-date mesh.
        _visible.Clear();
        foreach (Chunk chunk in world.LoadedChunks)
        {
            if (!ChunkMayBeVisible(chunk, isoMin, isoMax))
                continue;

            byte mask = NeighborMask(world, chunk.Coord);
            if (!_meshes.TryGetValue(chunk.Coord, out ChunkMesh? mesh) || mesh.NeighborMask != mask)
            {
                mesh?.Dispose();
                mesh = BuildMesh(world, chunk, mask);
                _meshes[chunk.Coord] = mesh;
            }

            if (mesh.IsEmpty || !Overlaps(mesh.BoundsMin, mesh.BoundsMax, isoMin, isoMax))
                continue;

            if (world.ShowTileGrid &&
                (!_gridMeshes.TryGetValue(chunk.Coord, out ChunkMesh? grid) || grid.NeighborMask != mask))
            {
                grid?.Dispose();
                _gridMeshes[chunk.Coord] = BuildGridMesh(world, chunk, mask);
            }

            _visible.Add(chunk);
        }

        // Painter's order: chunks further "up" the screen (smaller x+y) first.
        _visible.Sort(static (p, q) => (p.Coord.X + p.Coord.Y).CompareTo(q.Coord.X + q.Coord.Y));

        gd.BlendState = BlendState.AlphaBlend;
        gd.DepthStencilState = DepthStencilState.None;
        gd.RasterizerState = RasterizerState.CullNone;
        // Linear, with mip levels: the painted plants are stored several times larger
        // than they draw, and point sampling made their foliage crawl and sparkle as
        // the camera moved. The atlas leaves a transparent gutter around every sprite
        // so filtering cannot drag a neighbour's pixels in.
        gd.SamplerStates[0] = SamplerState.LinearClamp;

        // iso-space → screen: translate to the focus, scale by zoom, centre in the viewport.
        _effect.View = Matrix.CreateTranslation(-focusIso.X, -focusIso.Y, 0f)
                     * Matrix.CreateScale(zoom, zoom, 1f)
                     * Matrix.CreateTranslation(center.X, center.Y, 0f);
        _effect.Projection = Matrix.CreateOrthographicOffCenter(0, gd.Viewport.Width, gd.Viewport.Height, 0, 0, 1);

        foreach (EffectPass pass in _effect.CurrentTechnique.Passes)
        {
            pass.Apply();
            foreach (Chunk chunk in _visible)
                _meshes[chunk.Coord].Draw(gd);

            // The overlay goes last so it sits on top of terrain and vegetation alike.
            if (world.ShowTileGrid)
                foreach (Chunk chunk in _visible)
                    _gridMeshes[chunk.Coord].Draw(gd);
        }
    }

    // ---- mesh lifetime -------------------------------------------------------

    /// <summary>
    /// Terrain meshes bake both their geometry (in iso pixels) and their vertex
    /// colours, so a tile resize or a map-view switch invalidates every one of them.
    /// They are thrown away wholesale; the visible ones are rebuilt later this
    /// frame, the rest whenever they next come into view. The grid is pure geometry,
    /// so only a resize touches it.
    /// </summary>
    private void DropStaleMeshes(World world)
    {
        bool resized = _isoVersion != Iso.Version;
        bool recoloured = _mapView != world.MapView;
        if (!resized && !recoloured)
            return;

        _isoVersion = Iso.Version;
        _mapView = world.MapView;

        foreach (ChunkMesh mesh in _meshes.Values) mesh.Dispose();
        _meshes.Clear();

        if (!resized)
            return;

        foreach (ChunkMesh grid in _gridMeshes.Values) grid.Dispose();
        _gridMeshes.Clear();
    }

    private void DropUnloadedMeshes(World world)
    {
        _scratchCoords.Clear();
        foreach (KeyValuePair<ChunkCoord, ChunkMesh> kv in _meshes)
            if (!world.IsChunkLoaded(kv.Key))
                _scratchCoords.Add(kv.Key);
        foreach (ChunkCoord coord in _scratchCoords)
        {
            _meshes[coord].Dispose();
            _meshes.Remove(coord);
            if (_gridMeshes.Remove(coord, out ChunkMesh? grid))
                grid.Dispose();
        }
    }

    private static byte NeighborMask(World world, ChunkCoord c)
    {
        int mask = 0, bit = 0;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                if (world.IsChunkLoaded(new ChunkCoord(c.X + dx, c.Y + dy))) mask |= 1 << bit;
                bit++;
            }
        return (byte)mask;
    }

    /// <summary>Cheap conservative test from the chunk's tile extents, before a mesh exists.</summary>
    private static bool ChunkMayBeVisible(Chunk chunk, Vector2 isoMin, Vector2 isoMax)
    {
        int ox = chunk.OriginX, oy = chunk.OriginY, s = Chunk.Size;
        float minX = Iso.TileToIso(ox, oy + s, 0).X;
        float maxX = Iso.TileToIso(ox + s, oy, 0).X;
        float minY = Iso.TileToIso(ox, oy, WorldConstants.MaxLevel).Y - MaxSpriteHeight;
        float maxY = Iso.TileToIso(ox + s, oy + s, 0).Y;
        return Overlaps(new Vector2(minX, minY), new Vector2(maxX, maxY), isoMin, isoMax);
    }

    private static bool Overlaps(Vector2 aMin, Vector2 aMax, Vector2 bMin, Vector2 bMax)
        => aMin.X <= bMax.X && aMax.X >= bMin.X && aMin.Y <= bMax.Y && aMax.Y >= bMin.Y;

    // ---- mesh construction ---------------------------------------------------

    private ChunkMesh BuildMesh(World world, Chunk chunk, byte neighborMask)
    {
        _builder.Clear();
        _veg.Clear();
        _shore.Clear();

        int ox = chunk.OriginX, oy = chunk.OriginY;
        const int stride = Chunk.Size + 1;

        // Every vertex of the chunk gets ONE colour, computed from data that belongs
        // to the vertex itself (its own elevation, the slope around it, the biomes
        // meeting at it). All tiles sharing the vertex use the identical colour, so
        // the GPU interpolates continuously across tile edges instead of snapping.
        //
        // The waterline is the exception, so each vertex also records which side it
        // came down on; see the tile loop below.
        bool biomeView = world.MapView == MapView.Biome;
        for (int vy = 0; vy < stride; vy++)
        {
            for (int vx = 0; vx < stride; vx++)
            {
                int i = vy * stride + vx;
                _cornerWet[i] = biomeView && VertexIsWet(world, ox + vx, oy + vy);
                _cornerColors[i] = VertexColor(world, ox + vx, oy + vy, _cornerWet[i]);
            }
        }

        Vector2 terrainUv = _atlas.White.CenterUv;

        for (int ly = 0; ly < Chunk.Size; ly++)
        {
            for (int lx = 0; lx < Chunk.Size; lx++)
            {
                ref Tile tile = ref chunk.TileAt(lx, ly);
                int tx = ox + lx, ty = oy + ly;

                Vector2 a = CornerPos(world, tx, ty);
                Vector2 b = CornerPos(world, tx + 1, ty);
                Vector2 c = CornerPos(world, tx + 1, ty + 1);
                Vector2 d = CornerPos(world, tx, ty + 1);

                // Water and land do not share a gradient. A vertex on the waterline
                // holds whichever side won it, and a tile on the far side of that
                // vertex re-derives the corner from its OWN side instead. Otherwise
                // the shore is a smear the full width of a tile — barely visible on a
                // small tile, and a hundred pixels of green-blue haze on a large one.
                // Everywhere else the shared vertex is used and the terrain stays
                // seamless.
                bool wet = biomeView && tile.IsWater;
                Color cA = CornerColor(world, ly * stride + lx, tx, ty, wet);
                Color cB = CornerColor(world, ly * stride + lx + 1, tx + 1, ty, wet);
                Color cC = CornerColor(world, (ly + 1) * stride + lx + 1, tx + 1, ty + 1, wet);
                Color cD = CornerColor(world, (ly + 1) * stride + lx, tx, ty + 1, wet);

                // Fan four triangles around a centre vertex rather than splitting on
                // one diagonal, which would bias interpolation and show as a crease.
                Vector2 m = (a + b + c + d) * 0.25f;
                Color cM = Average(cA, cB, cC, cD);

                _builder.Triangle(a, cA, b, cB, m, cM, terrainUv);
                _builder.Triangle(b, cB, c, cC, m, cM, terrainUv);
                _builder.Triangle(c, cC, d, cD, m, cM, terrainUv);
                _builder.Triangle(d, cD, a, cA, m, cM, terrainUv);

                // The data views are about the ground itself; plants and surf would
                // only hide it.
                if (world.MapView == MapView.Biome)
                {
                    AppendScatter(tile, a, b, c, d, tx, ty);
                    CollectShore(world, tile, a, b, c, d, tx, ty);
                }
            }
        }

        // Surf goes on after every tile's ground, or the next tile along would paint
        // over it.
        foreach (ShoreEdge edge in _shore)
            AppendShoreFoam(edge);

        // Vegetation goes after all terrain, sorted so lower-on-screen plants draw
        // over the ones behind them.
        _veg.Sort(static (p, q) => p.Pos.Y.CompareTo(q.Pos.Y));
        foreach (VegItem item in _veg)
            AppendVegetation(item);

        return _builder.Upload(neighborMask);
    }

    /// <summary>
    /// The debug tile grid: one line along every tile edge, following the same
    /// corner elevations the terrain mesh uses, so the grid drapes over hills
    /// instead of floating flat above them.
    ///
    /// Each chunk draws its own interior edges plus the two far borders, but only
    /// where the neighbour that would otherwise draw them isn't loaded — otherwise
    /// the shared seam would be blended twice and read as a brighter line.
    /// </summary>
    private ChunkMesh BuildGridMesh(World world, Chunk chunk, byte neighborMask)
    {
        _builder.Clear();

        int ox = chunk.OriginX, oy = chunk.OriginY;
        Vector2 uv = _atlas.White.CenterUv;
        bool eastLoaded = (neighborMask & (1 << 4)) != 0;   // (dx +1, dy 0)
        bool southLoaded = (neighborMask & (1 << 6)) != 0;  // (dx 0, dy +1)

        for (int ly = 0; ly <= Chunk.Size; ly++)
        {
            for (int lx = 0; lx <= Chunk.Size; lx++)
            {
                int vx = ox + lx, vy = oy + ly;
                Vector2 p = CornerPos(world, vx, vy);

                if (lx < Chunk.Size && !(ly == Chunk.Size && southLoaded))
                    _builder.Line(p, CornerPos(world, vx + 1, vy), GridLineWidth, GridColor, uv);

                if (ly < Chunk.Size && !(lx == Chunk.Size && eastLoaded))
                    _builder.Line(p, CornerPos(world, vx, vy + 1), GridLineWidth, GridColor, uv);
            }
        }

        return _builder.Upload(neighborMask);
    }

    /// <summary>
    /// The cached vertex colour when it belongs to the same side of the waterline as
    /// the tile asking, and a fresh one restricted to that side when it doesn't.
    /// </summary>
    private Color CornerColor(World world, int cacheIndex, int vx, int vy, bool wet)
        => _cornerWet[cacheIndex] == wet
            ? _cornerColors[cacheIndex]
            : VertexColor(world, vx, vy, wet);

    private static Vector2 CornerPos(World world, int vx, int vy)
        => Iso.TileToIso(vx, vy, LevelAt(world, vx, vy));

    private static readonly (int dx, int dy)[] CornerOff = { (-1, -1), (0, -1), (-1, 0), (0, 0) };

    /// <summary>
    /// Which side of the waterline a vertex falls on: the majority of the tiles
    /// meeting there, with ties going to water so a narrow river keeps its width.
    /// </summary>
    private static bool VertexIsWet(World world, int vx, int vy)
    {
        int water = 0, land = 0;
        foreach ((int dx, int dy) in CornerOff)
        {
            if (world.TryGetTile(vx + dx, vy + dy, out Tile t))
            {
                if (t.IsWater) water++; else land++;
            }
        }
        return water >= land && water > 0;
    }

    /// <summary>Level step to a neighbour at which ground starts to bare to rock, and where it is bare.</summary>
    private const float CliffStart = 1.5f;
    private const float CliffFull = 4f;

    /// <summary>The colour of exposed cliff rock. Cool grey with a little brown in it.</summary>
    private static readonly Color CliffRock = new(112, 104, 94);

    /// <summary>
    /// The final, fully shaded colour of one mesh vertex: the average biome colour of
    /// the (up to four) tiles meeting there, bared to rock where the ground is steep
    /// (a cliff), then lit by that vertex's own elevation and the slope of the terrain
    /// around it, with a little per-vertex noise so flat areas don't read as a single
    /// solid fill. Depends only on the vertex, never on which tile is asking — that is
    /// what keeps the mesh seamless.
    /// </summary>
    private static Color VertexColor(World world, int vx, int vy, bool wet)
    {
        if (world.MapView != MapView.Biome)
            return DataVertexColor(world, vx, vy, world.MapView);

        // Only the tiles on the requested side of the waterline contribute. Averaging
        // water in with land is what made the shore a gradient; keeping them apart is
        // what makes it a coast, and the join is covered with surf instead.
        int r = 0, g = 0, b = 0, n = 0;
        foreach ((int dx, int dy) in CornerOff)
        {
            if (world.TryGetTile(vx + dx, vy + dy, out Tile t) && t.IsWater == wet)
            {
                Color c = BiomeRegistry.SurfaceColor(t);
                r += c.R; g += c.G; b += c.B; n++;
            }
        }
        Color ground = new(r / n, g / n, b / n);

        int level = LevelAt(world, vx, vy);

        // Cliffs: steep land shows bare rock instead of turf. Steepness is the biggest
        // one-tile level step around the vertex; it bares off past CliffStart and is
        // all rock by CliffFull. Water keeps its own surface.
        if (!wet)
        {
            int steep = Math.Max(
                Math.Max(Math.Abs(LevelAt(world, vx + 1, vy) - level), Math.Abs(LevelAt(world, vx - 1, vy) - level)),
                Math.Max(Math.Abs(LevelAt(world, vx, vy + 1) - level), Math.Abs(LevelAt(world, vx, vy - 1) - level)));
            float cliff = Smoothstep01((steep - CliffStart) / (CliffFull - CliffStart));
            ground = Color.Lerp(ground, CliffRock, cliff);
        }

        float elevShade = 0.82f + 0.30f * (level / (float)WorldConstants.MaxLevel);

        // Slope lighting from the north-west: ground that rises toward the top of the
        // screen catches light. Central differences across the neighbouring vertices.
        float slope = 0.5f * (LevelAt(world, vx - 1, vy - 1) - LevelAt(world, vx + 1, vy + 1))
                    + 0.2f * (LevelAt(world, vx + 1, vy - 1) - LevelAt(world, vx - 1, vy + 1));
        float slopeShade = Math.Clamp(1f + 0.05f * slope, 0.8f, 1.2f);

        float jitter = 0.96f + 0.08f * Hash01(vx, vy);

        return Scale(ground, elevShade * slopeShade * jitter);
    }

    /// <summary>
    /// Debug colouring: one of the generator's raw fields, averaged over the tiles
    /// meeting at this vertex and run through a ramp. Deliberately left unshaded —
    /// elevation and slope lighting would make it impossible to read a value off
    /// the screen.
    /// </summary>
    private static Color DataVertexColor(World world, int vx, int vy, MapView view)
    {
        float sum = 0f;
        int n = 0;
        foreach ((int dx, int dy) in CornerOff)
        {
            if (!world.TryGetTile(vx + dx, vy + dy, out Tile t))
                continue;

            sum += view switch
            {
                MapView.Height => t.Height,
                MapView.Temperature => t.Temperature,
                _ => t.Moisture,
            };
            n++;
        }

        if (n == 0)
            return Color.Black; // no tile touches this vertex, so nothing draws it

        return Ramp(sum / n, view switch
        {
            MapView.Height => HeightRamp,
            MapView.Temperature => TemperatureRamp,
            _ => MoistureRamp,
        });
    }

    /// <summary>Walks <paramref name="t"/> (0..1) through evenly spaced colour stops.</summary>
    private static Color Ramp(float t, Color[] stops)
    {
        float scaled = Math.Clamp(t, 0f, 1f) * (stops.Length - 1);
        int i = Math.Min((int)scaled, stops.Length - 2);
        return Color.Lerp(stops[i], stops[i + 1], scaled - i);
    }

    /// <summary>
    /// Notes each edge where this water tile meets land, so surf can be drawn along
    /// it once the ground is down. Only the water side looks — the land tile facing
    /// it would find the same edge and draw it twice.
    /// </summary>
    private void CollectShore(World world, in Tile tile, Vector2 a, Vector2 b, Vector2 c, Vector2 d, int tx, int ty)
    {
        if (!tile.IsWater)
            return;

        Vector2 middle = (a + b + c + d) * 0.25f;

        Edge(tx, ty - 1, a, b, 0); // north-west edge
        Edge(tx + 1, ty, b, c, 1); // north-east
        Edge(tx, ty + 1, c, d, 2); // south-east
        Edge(tx - 1, ty, d, a, 3); // south-west

        void Edge(int nx, int ny, Vector2 from, Vector2 to, int side)
        {
            if (!world.TryGetTile(nx, ny, out Tile neighbour) || neighbour.IsWater)
                return;

            Vector2 inward = middle - (from + to) * 0.5f;
            float length = inward.Length();
            if (length < 1e-4f)
                return;

            _shore.Add(new ShoreEdge(from, to, inward / length,
                                     (uint)((tx * 51287) ^ (ty * 92821) ^ (side * 7717) ^ 0x51ed)));
        }
    }

    /// <summary>
    /// Surf along one shoreline edge: a broken line of foam right on the waterline
    /// and a fainter swell behind it. Both sit inside the water, so the beach keeps
    /// its own colour and the coast reads as a coast rather than a gradient.
    /// </summary>
    private void AppendShoreFoam(in ShoreEdge edge)
    {
        var rng = new XorShift(edge.Seed);
        Vector2 uv = _atlas.White.CenterUv;
        Vector2 along = edge.To - edge.From;
        float edgeLength = along.Length();
        if (edgeLength < 2f)
            return;

        Vector2 dir = along / edgeLength;

        // Surf is art, sized in iso pixels, not a fraction of the tile it happens to
        // border. Laying it out as fractions meant a 300-pixel tile got three
        // enormous dashes lying out in the water; breakers now repeat along the edge
        // at their own size, so the shore looks the same whatever the tile size.
        for (float t = rng.Next() * FoamSpacing; t < edgeLength; t += FoamSpacing * (0.7f + 0.6f * rng.Next()))
        {
            if (rng.Next() < 0.18f)
                continue; // surf doesn't break everywhere at once

            float length = MathF.Min(FoamStroke * (0.6f + 0.8f * rng.Next()), edgeLength - t);
            if (length < 2f)
                continue;

            // Each end sits a different distance out, so the foam doesn't simply
            // trace the tile grid's staircase back in white.
            Vector2 p0 = edge.From + dir * t + edge.Inward * (0.5f + 1.8f * rng.Next());
            Vector2 p1 = edge.From + dir * (t + length) + edge.Inward * (0.5f + 1.8f * rng.Next());
            _builder.Line(p0, p1, 1.2f + 0.8f * rng.Next(), FoamNear, uv);

            // A fainter swell behind it, to break from.
            if (rng.Next() > 0.55f)
            {
                Vector2 back = edge.Inward * (3.5f + 3f * rng.Next());
                _builder.Line(p0 + back, p1 + back, 1f, FoamFar, uv);
            }
        }
    }

    /// <summary>
    /// AoE2-style vegetation: a tile scatters many individual plants at randomized,
    /// deterministic positions within its quad (dense forests, sparse savanna),
    /// their count driven by biome and the tile's vegetation-density field.
    ///
    /// The counts below are authored per <see cref="Iso.BaseTileWidth"/> tile and
    /// multiplied by the tile's area, because plants keep their own absolute size
    /// however big the tile is: a wider tile is more ground, so more of them fit.
    /// </summary>
    private void AppendScatter(in Tile tile, Vector2 a, Vector2 b, Vector2 c, Vector2 d, int tx, int ty)
    {
        var rng = new XorShift((uint)((tx * 73856093) ^ (ty * 19349663) ^ 0x9e37));
        float dens = tile.VegetationDensity;
        float area = Iso.TileAreaScale;

        // Keep plants a little off the tile edge, but as a fixed distance rather
        // than a fixed fraction — otherwise a big tile grows a bare border and the
        // scatter reads as a grid.
        float inset = MathF.Min(0.14f, 8f / Iso.TileWidth);
        float span = 1f - 2f * inset;

        // Trees fall away towards the edge of the biome and pack in towards its
        // core: this ramps 0 at the boundary to 1 at TreeEdgeFade in. A true forest
        // then gets a further boost that peaks at the same core, so its centre is
        // markedly denser than its fringe; open country is left at its baseline.
        float core = Smoothstep01(
            BiomeClassifier.ClimateEdgeDistance(tile.Temperature, tile.Moisture) / TreeEdgeFade);
        float treeDensity = MinTreeEdgeDensity + (1f - MinTreeEdgeDensity) * core;
        if (IsForestBiome(tile.Biome))
            treeDensity += ForestCoreBoost * core;

        // Counts are per base-sized tile and may be fractional — "one tree per twenty
        // tiles" is a rate, not a number. Rounding that would lose it entirely, so
        // the remainder is settled with a coin toss: a rate of 0.05 puts a tree on
        // one tile in twenty, at any tile size.
        void Add(Vegetation kind, float rate, float deadChance = 0f)
        {
            float exact = rate * area * (IsTree(kind) ? treeDensity : 1f);
            int n = (int)exact;
            if (rng.Next() < exact - n) n++;
            if (n > MaxPlantsPerTile) n = MaxPlantsPerTile;
            for (int i = 0; i < n; i++)
            {
                float u = inset + span * rng.Next();
                float v = inset + span * rng.Next();
                float s = 0.8f + 0.5f * rng.Next();
                bool dead = rng.Next() < deadChance;
                _veg.Add(new VegItem(Bilerp(a, b, c, d, u, v), kind, s, dead, rng.NextBits()));
            }
        }

        // The dead-chance argument is how many of a kind come up as bare wood. Standing
        // deadwood is normal in a real wood; it is the rule on tundra, where nothing
        // holds a leaf, and common in swamp and savanna where trees drown or bake.
        switch (tile.Biome)
        {
            case Biome.TemperateForest:
                Add(Vegetation.BroadleafTree, 5 + Ri(dens * 6), deadChance: 0.07f);
                Add(Vegetation.Bush, Ri(dens * 2));
                break;
            case Biome.Taiga:
                Add(Vegetation.PineTree, 5 + Ri(dens * 6), deadChance: 0.10f);
                // Undergrowth is sparse in the cold: only the odd frost-bitten shrub.
                if (rng.Next() > 0.9f) Add(Vegetation.Bush, 1, deadChance: 0.5f);
                break;
            case Biome.Jungle:
                Add(Vegetation.PalmTree, 6 + Ri(dens * 6), deadChance: 0.04f);
                Add(Vegetation.Bush, 1 + Ri(dens * 2));
                break;
            case Biome.Grassland:
                // A lone tree here and there across open grass, thickening into light
                // woodland where the clumping field peaks. This used to be a hard
                // switch at dens 0.5 — half the grassland bare, half of it a forest.
                Add(Vegetation.BroadleafTree, 0.05f + 0.7f * MathF.Max(0f, dens - 0.5f), deadChance: 0.09f);
                if (dens > 0.35f && rng.Next() > 0.5f) Add(Vegetation.Bush, 1);
                break;
            case Biome.Savanna:
                if (dens > 0.55f) Add(Vegetation.BroadleafTree, dens > 0.8f ? 2 : 1, deadChance: 0.18f);
                break;
            case Biome.Swamp:
                Add(Vegetation.Bush, 1 + Ri(dens * 3), deadChance: 0.25f);
                if (dens > 0.6f) Add(Vegetation.BroadleafTree, 1, deadChance: 0.30f);
                break;
            case Biome.Tundra:
                // Nothing up here keeps a leaf: tundra scrub is bare twigs, always —
                // and thin on the ground, only the rare hardy shrub in the frost.
                if (rng.Next() > 0.92f) Add(Vegetation.Bush, 1, deadChance: 1f);
                break;
            case Biome.Desert: if (dens > 0.7f) Add(Vegetation.Cactus, dens > 0.88f ? 2 : 1); break;
            case Biome.Rock: if (rng.Next() > 0.6f) Add(Vegetation.Rock, rng.Next() > 0.6f ? 2 : 1); break;
        }
    }

    /// <summary>
    /// Emits one plant as atlas-textured quads. Sizes are in iso pixels, scaled by
    /// the plant's own <see cref="VegItem.Scale"/>.
    ///
    /// Every plant runs its own RNG off <see cref="VegItem.Seed"/>, so no two of a
    /// kind share a silhouette or a shade — height, lean, crown shape and colour all
    /// vary per instance. A dead one drops its foliage and is drawn as bare wood.
    /// </summary>
    private void AppendVegetation(in VegItem item)
    {
        var rng = new XorShift(item.Seed);
        Vector2 at = item.Pos;
        float s = item.Scale;

        switch (item.Veg)
        {
            case Vegetation.GrassTuft: GrassTuft(at, s, ref rng); break;

            case Vegetation.Bush:
                Plant(at, s, item.Dead ? _atlas.DeadBushes : _atlas.Bushes, BushHeight, ref rng);
                break;

            case Vegetation.PineTree:
                Plant(at, s, item.Dead ? _atlas.DeadTrees : _atlas.Conifers, TreeHeight, ref rng);
                break;

            // The sheets carry no palm, so jungles borrow the broadleaves rather than
            // mixing painted art with a drawn-in-code stand-in.
            case Vegetation.BroadleafTree:
            case Vegetation.PalmTree:
                Plant(at, s, item.Dead ? _atlas.DeadTrees : _atlas.Broadleaves, TreeHeight, ref rng);
                break;

            case Vegetation.Cactus: Cactus(at, s, ref rng); break;
            case Vegetation.Rock: Rock(at, s, ref rng); break;
        }
    }

    /// <summary>
    /// Draws one painted plant: a single quad standing on its own base, at a height
    /// in iso pixels with the art's own proportions. Which of the sheet's variants
    /// it gets comes from the plant's seed, so a wood is a mix rather than one tree
    /// stamped over and over.
    /// </summary>
    private void Plant(Vector2 at, float s, IReadOnlyList<TextureAtlas.Region> set, float height, ref XorShift rng)
    {
        if (set.Count == 0)
            return;

        TextureAtlas.Region art = set[(int)(rng.Next() * set.Count) % set.Count];
        float h = height * s * (0.85f + 0.3f * rng.Next());
        var size = new Vector2(h * art.Aspect, h);

        // Only a light tint: the art already carries its own colour and variation.
        _builder.Sprite(at, size, BottomCenter, art, Vary(Color.White, ref rng, 0.07f));
    }

    // ---- species -------------------------------------------------------------

    private void GrassTuft(Vector2 at, float s, ref XorShift rng)
    {
        Color blade = Vary(GrassBlade, ref rng);
        int blades = rng.Range(4, 6);
        for (int i = 0; i < blades; i++)
        {
            float lean = (i / (float)(blades - 1) - 0.5f) * 2f;
            float h = (3f + 2.5f * rng.Next()) * s;
            Vector2 root = at + new Vector2(lean * 2.6f * s, 0f);
            _builder.Line(root, root + new Vector2(lean * 1.8f * s, -h), 0.8f * s,
                          Scale(blade, 0.85f + 0.3f * rng.Next()), _atlas.White.CenterUv);
        }
    }

    private void Cactus(Vector2 at, float s, ref XorShift rng)
    {
        Color flesh = Vary(CactusFlesh, ref rng, 0.10f);
        float h = (11f + 4f * rng.Next()) * s;
        _builder.Sprite(at - new Vector2(0f, h * 0.5f), new Vector2(5f * s, h), BottomCenter, _atlas.Disc, flesh);

        int arms = rng.Range(1, 2);
        for (int i = 0; i < arms; i++)
        {
            float dir = i == 0 ? -1f : 1f;
            float armY = h * (0.55f + 0.25f * rng.Next());
            Disc(at + new Vector2(dir * 3f * s, -armY), (3.5f + 1.5f * rng.Next()) * s, Scale(flesh, 0.94f));
        }
    }

    private void Rock(Vector2 at, float s, ref XorShift rng)
    {
        Color stone = Vary(RockStone, ref rng, 0.10f);
        float d = (7f + 3f * rng.Next()) * s;
        Disc(at - new Vector2(0f, d * 0.25f), d, stone);
        Disc(at + new Vector2(d * 0.3f, -d * 0.1f), d * 0.55f, Scale(stone, 0.88f));
        Disc(at - new Vector2(d * 0.2f, d * 0.45f), d * 0.45f, Scale(stone, 1.15f));
    }

    // ---- sprite helpers (all append to the current chunk mesh) --------------

    /// <summary>Per-instance colour jitter, in brightness and a little warm/cool tilt.</summary>
    private static Color Vary(Color c, ref XorShift rng, float amount = 0.14f)
    {
        float f = 1f - amount + 2f * amount * rng.Next();
        float tilt = (rng.Next() - 0.5f) * 0.16f;
        return new Color(
            (int)Math.Clamp(c.R * (f + tilt), 0, 255),
            (int)Math.Clamp(c.G * f, 0, 255),
            (int)Math.Clamp(c.B * (f - tilt), 0, 255));
    }

    private void Disc(Vector2 pos, float diameter, Color color)
        => _builder.Sprite(pos, new Vector2(diameter), Center, _atlas.Disc, color);

    // ---- maths -----------------------------------------------------------------

    private static int Ri(float x) => (int)MathF.Round(x);

    /// <summary>The canopy species — the ones that thin out at a biome's edge.</summary>
    private static bool IsTree(Vegetation veg)
        => veg is Vegetation.PineTree or Vegetation.BroadleafTree or Vegetation.PalmTree;

    /// <summary>Clamped smoothstep, so the fade has no hard edge where it begins.</summary>
    private static float Smoothstep01(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static Vector2 Bilerp(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float u, float v)
        => Vector2.Lerp(Vector2.Lerp(a, b, u), Vector2.Lerp(d, c, u), v);

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

    private static Color Average(Color a, Color b, Color c, Color d) => new(
        (a.R + b.R + c.R + d.R) / 4,
        (a.G + b.G + c.G + d.G) / 4,
        (a.B + b.B + c.B + d.B) / 4);

    public void Dispose()
    {
        foreach (ChunkMesh mesh in _meshes.Values) mesh.Dispose();
        _meshes.Clear();
        foreach (ChunkMesh grid in _gridMeshes.Values) grid.Dispose();
        _gridMeshes.Clear();
        _effect.Dispose();
        _atlas.Texture.Dispose();
    }
}
