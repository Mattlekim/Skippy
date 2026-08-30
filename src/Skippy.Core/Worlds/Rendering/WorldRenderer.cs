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
    private readonly CloudLayer _clouds;

    private readonly Dictionary<ChunkCoord, ChunkMesh> _meshes = new();

    /// <summary>Tile-grid overlay meshes, built lazily and only while the grid is switched on.</summary>
    private readonly Dictionary<ChunkCoord, ChunkMesh> _gridMeshes = new();

    private readonly List<ChunkCoord> _scratchCoords = new();
    private readonly List<Chunk> _visible = new();
    private readonly List<VegItem> _veg = new();
    private readonly List<ShoreEdge> _shore = new();

    /// <summary>Per-vertex colours for the (Chunk.Size + 1)² corner grid of the chunk being built.</summary>
    private readonly Color[] _cornerColors = new Color[(Chunk.Size + 1) * (Chunk.Size + 1)];

    /// <summary>World-space surface normal at each of those corners, for the sun to light.</summary>
    private readonly Vector3[] _cornerNormals = new Vector3[(Chunk.Size + 1) * (Chunk.Size + 1)];

    /// <summary>
    /// How many sub-cells each land tile is split into per side. The terrain surface is
    /// otherwise flat between one integer corner and the next, so its slope snaps at
    /// every vertex and reads as a crease or a point. Splitting the tile and lifting
    /// each interior sub-vertex to a smoothly interpolated height rounds those slope
    /// changes off. 1 would be the old single quad.
    /// </summary>
    private const int Subdiv = 3;

    /// <summary>
    /// A tile that meets the sea is subdivided finer than the rest and cut along the
    /// smooth waterline, so the shore is a rounded curve through the tile rather than a
    /// staircase along its edges. Only shore tiles pay for the extra detail.
    /// </summary>
    private const int CoastSubdiv = 6;

    /// <summary>Height band over which the land fades out into the water — the softness of the shore edge.</summary>
    private const float CoastFeather = 0.012f;

    /// <summary>The chunk's fields plus a four-tile margin, wide enough for the smoothing stencil and the warped biome lookup to never leave it.</summary>
    private const int FieldStride = Chunk.Size + 8;
    private const int FieldMargin = 4;
    private readonly float[] _levelField = new float[FieldStride * FieldStride];

    /// <summary>The chunk's continuous fields (same margin): height for the waterline, climate to classify biomes within a boundary tile.</summary>
    private readonly float[] _heightField = new float[FieldStride * FieldStride];
    private readonly float[] _tempField = new float[FieldStride * FieldStride];
    private readonly float[] _moistField = new float[FieldStride * FieldStride];

    /// <summary>A boundary tile is subdivided this fine so its biome edge follows the climate contour smoothly.</summary>
    private const int BlendSubdiv = 6;

    /// <summary>How far the biome edge is warped off the (very smooth) climate contour so it wanders naturally. In tiles; kept inside the field margin.</summary>
    private const float BiomeWarpAmp = 1.4f;
    private const float BiomeWarpFreq = 0.11f;

    /// <summary>Scratch for one tile's sub-vertices while it is being emitted (sized for the finest grid used).</summary>
    private const int MaxSub = CoastSubdiv > BlendSubdiv ? CoastSubdiv : BlendSubdiv;
    private readonly Vector2[] _subPos = new Vector2[(MaxSub + 1) * (MaxSub + 1)];
    private readonly Vector2[] _subUv = new Vector2[(MaxSub + 1) * (MaxSub + 1)];
    private readonly Vector3[] _subNrm = new Vector3[(MaxSub + 1) * (MaxSub + 1)];
    private readonly Color[] _subCol = new Color[(MaxSub + 1) * (MaxSub + 1)];
    private readonly int[] _subTex = new int[(MaxSub + 1) * (MaxSub + 1)];

    /// <summary>Per-sub-vertex master alpha: 1 inland, fading to 0 across the waterline on a shore tile.</summary>
    private readonly float[] _subA = new float[(MaxSub + 1) * (MaxSub + 1)];

    /// <summary>Scratch for the textures blended over a boundary tile, and their per-corner weights.</summary>
    private readonly int[] _blendTex = new int[MaxBlendOverlays];
    private readonly float[] _blendW = new float[MaxBlendOverlays * 4];

    /// <summary>Most neighbouring textures blended into one tile (a rare 4-way corner is capped here).</summary>
    private const int MaxBlendOverlays = 4;

    /// <summary>How the blend edge is roughened so a biome border wanders instead of running straight.</summary>
    private const float BlendNoiseFreq = 0.9f;
    private const float BlendNoiseAmp = 0.5f;

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
        _clouds = new CloudLayer(gd);
        _effect = new BasicEffect(gd)
        {
            VertexColorEnabled = true,
            TextureEnabled = true,
            LightingEnabled = false,
            // Per-pixel lighting interpolates the normal across each triangle instead of
            // the lit colour, so a lone sun-facing corner reads as a smooth highlight
            // rather than a hard star flaring out of the vertex.
            PreferPerPixelLighting = true,
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

        ApplyLighting(world);

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

        // Clouds and their shadows sit above the ground, so they come after it — and
        // only in the game view, where they belong (the data views stay unclouded).
        if (world.MapView == MapView.Biome)
        {
            _clouds.DrawShadows(gd, focusIso, center, zoom, world.Sky);
            _clouds.DrawClouds(gd, focusIso, center, zoom, world.Sky);
        }
    }

    /// <summary>
    /// Points the built-in directional light at the sun for this frame. The geometry
    /// on the GPU never changes as the sun moves — only these few uniforms do, so a
    /// full day passes without a single vertex being re-uploaded.
    ///
    /// The data map views (height, temperature, moisture) are meant to be read as
    /// values, so they are drawn flat with lighting off; only the biome view is lit.
    /// </summary>
    private void ApplyLighting(World world)
    {
        if (world.MapView != MapView.Biome)
        {
            _effect.LightingEnabled = false;
            return;
        }

        SkyLight sky = world.Sky;
        _effect.LightingEnabled = true;
        _effect.DiffuseColor = Vector3.One;   // the surface albedo is the vertex colour
        _effect.EmissiveColor = Vector3.Zero;
        _effect.SpecularColor = Vector3.Zero;
        _effect.AmbientLightColor = sky.Ambient;

        DirectionalLight sun = _effect.DirectionalLight0;
        sun.Enabled = true;
        sun.Direction = sky.LightDirection;
        sun.DiffuseColor = sky.SunColor;
        sun.SpecularColor = Vector3.Zero;
        _effect.DirectionalLight1.Enabled = false;
        _effect.DirectionalLight2.Enabled = false;
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

        // Cache the chunk's level field (plus a two-tile margin) once, so the smooth
        // subdivision below reads heights out of a flat array instead of doing a tile
        // lookup for every one of its samples.
        for (int fy = 0; fy < FieldStride; fy++)
            for (int fx = 0; fx < FieldStride; fx++)
            {
                int i = fy * FieldStride + fx;
                int wx = ox + fx - FieldMargin, wy = oy + fy - FieldMargin;
                _levelField[i] = LevelAt(world, wx, wy);
                if (world.TryGetTile(wx, wy, out Tile ht))
                {
                    _heightField[i] = ht.Height;
                    _tempField[i] = ht.Temperature;
                    _moistField[i] = ht.Moisture;
                }
                else
                {
                    _heightField[i] = WorldConstants.SeaLevel + 0.1f;
                    _tempField[i] = 0.5f;
                    _moistField[i] = 0.5f;
                }
            }

        // Every vertex of the chunk gets ONE colour, computed from data that belongs
        // to the vertex itself (its own elevation, the slope around it, the biomes
        // meeting at it). All tiles sharing the vertex use the identical colour, so
        // the GPU interpolates continuously across tile edges instead of snapping.
        //
        // The waterline is the exception, so each vertex also records which side it
        // came down on; see the tile loop below.
        // The per-corner colour/normal cache feeds only the data map views now; the game
        // view is textured and lit from the smoothed surface, so it never reads it.
        bool biomeView = world.MapView == MapView.Biome;
        if (!biomeView)
        {
            for (int vy = 0; vy < stride; vy++)
                for (int vx = 0; vx < stride; vx++)
                {
                    int i = vy * stride + vx;
                    _cornerWet[i] = false;
                    _cornerColors[i] = VertexColor(world, ox + vx, oy + vy, false);
                    _cornerNormals[i] = VertexNormal(world, ox + vx, oy + vy);
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

                if (biomeView)
                {
                    // The ground wears its biome's painted texture, tiled one repeat per
                    // world tile and randomly flipped so the grid does not read as a
                    // stamp. Where a neighbouring tile wears a different texture it is
                    // blended back in over this one, fading from the shared edge, so the
                    // border is a soft (and, with the noise, wandering) gradient rather
                    // than a hard tile edge. Colour is left white — the texture carries
                    // the colour, the sun the shade. Land is subdivided and smoothed;
                    // flat water keeps a single quad.
                    TileFlip(tx, ty, out bool flipU, out bool flipV);
                    int baseTex = TerrainTextureIndex(tile.Biome, tx, ty);
                    int overlays = GatherBlend(world, tx, ty, baseTex);

                    if (!tile.IsWater && IsCoastalLand(lx, ly))
                    {
                        // A land tile that touches the sea: cut it along the real waterline
                        // so the shore rounds through the tile, and blend its land the same
                        // as anywhere else so the inland edge is soft too.
                        EmitCoastalTile(ox, oy, lx, ly, tx, ty, baseTex, flipU, flipV);
                    }
                    else if (!tile.IsWater)
                    {
                        // Interior of a biome: one texture, the cheap single quad. On a
                        // biome border: subdivide and cut the textures along the climate
                        // contour (a natural, sub-tile edge).
                        if (overlays == 0)
                            EmitSmoothTile(lx, ly, tx, ty, _atlas.Terrain[baseTex], flipU, flipV, 1f, 1f, 1f, 1f, false);
                        else
                            EmitBlendedLandTile(ox, oy, lx, ly, tx, ty, baseTex, flipU, flipV);
                    }
                    else
                    {
                        EmitFlatTile(a, b, c, d, tx, ty, _atlas.Terrain[baseTex], flipU, flipV, 1f, 1f, 1f, 1f, false);
                        for (int o = 0; o < overlays; o++)
                            EmitFlatTile(a, b, c, d, tx, ty, _atlas.Terrain[_blendTex[o]], flipU, flipV,
                                         _blendW[o * 4], _blendW[o * 4 + 1], _blendW[o * 4 + 2], _blendW[o * 4 + 3], true);
                    }
                }
                else
                {
                    // Data views read the raw per-vertex value off a solid fill, so they
                    // keep the cheap single quad with the ramp colours and no texture.
                    Color cA = CornerColor(world, ly * stride + lx, tx, ty, false);
                    Color cB = CornerColor(world, ly * stride + lx + 1, tx + 1, ty, false);
                    Color cC = CornerColor(world, (ly + 1) * stride + lx + 1, tx + 1, ty + 1, false);
                    Color cD = CornerColor(world, (ly + 1) * stride + lx, tx, ty + 1, false);

                    Vector3 nA = _cornerNormals[ly * stride + lx];
                    Vector3 nB = _cornerNormals[ly * stride + lx + 1];
                    Vector3 nC = _cornerNormals[(ly + 1) * stride + lx + 1];
                    Vector3 nD = _cornerNormals[(ly + 1) * stride + lx];

                    Vector2 m = (a + b + c + d) * 0.25f;
                    Color cM = Average(cA, cB, cC, cD);
                    Vector3 nM = Vector3.Normalize(nA + nB + nC + nD);

                    _builder.Triangle(a, cA, nA, b, cB, nB, m, cM, nM, terrainUv);
                    _builder.Triangle(b, cB, nB, c, cC, nC, m, cM, nM, terrainUv);
                    _builder.Triangle(c, cC, nC, d, cD, nD, m, cM, nM, terrainUv);
                    _builder.Triangle(d, cD, nD, a, cA, nA, m, cM, nM, terrainUv);
                }

                // The data views are about the ground itself; plants would only hide it.
                // Surf is no longer collected here: it traced tile edges, which the
                // coastal cut has moved the visible waterline away from, so it now floats
                // offset in the water. The smooth shore reads as wet beach without it.
                if (world.MapView == MapView.Biome)
                    AppendScatter(tile, a, b, c, d, tx, ty);
            }
        }

        // Surf goes on after every tile's ground, or the next tile along would paint
        // over it.
        foreach (ShoreEdge edge in _shore)
            AppendShoreFoam(edge);

        // Vegetation goes after all terrain, sorted so lower-on-screen plants draw
        // over the ones behind them.
        _veg.Sort(static (p, q) => p.Pos.Y.CompareTo(q.Pos.Y));

        // Every plant's shadow first, as one layer on the ground, then the plants on
        // top: that way a shadow only ever darkens the terrain, never the trunk of a
        // tree standing in front of it.
        foreach (VegItem item in _veg)
            AppendShadow(item);
        foreach (VegItem item in _veg)
            AppendVegetation(item);

        return _builder.Upload(neighborMask);
    }

    /// <summary>The white ground draws the texture at its own colour; only the sun shades it.</summary>
    private static readonly Color TerrainWhite = Color.White;

    /// <summary>
    /// Emits one land tile as a <see cref="Subdiv"/>×<see cref="Subdiv"/> grid whose
    /// interior heights and normals come from a smooth (Catmull-Rom) interpolation of
    /// the level field. The interpolation passes exactly through the integer corner
    /// heights, so the tile still meets its neighbours — and the flat water beside it —
    /// seamlessly; only the ground in between is rounded off. Each sub-vertex samples the
    /// tile's ground texture <paramref name="tex"/> once across the whole tile, flipped
    /// per <paramref name="flipU"/>/<paramref name="flipV"/>.
    /// </summary>
    private void EmitSmoothTile(int lx, int ly, int tx, int ty, TextureAtlas.Region tex, bool flipU, bool flipV,
                                float wTL, float wTR, float wBR, float wBL, bool overlay)
    {
        const int n = Subdiv, span = Subdiv + 1;
        for (int i = 0; i <= n; i++)
        {
            float v = i / (float)n;
            for (int j = 0; j <= n; j++)
            {
                float u = j / (float)n;
                float fx = lx + u, fy = ly + v;
                float level = SmoothLevel(fx, fy);
                int k = i * span + j;
                _subPos[k] = Iso.TileToIso(tx + u, ty + v, level);
                _subUv[k] = TileUv(tex, u, v, flipU, flipV);
                _subNrm[k] = SmoothNormal(fx, fy);
                _subCol[k] = overlay ? BlendColor(wTL, wTR, wBR, wBL, u, v, tx, ty) : TerrainWhite;
            }
        }

        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                int p00 = i * span + j, p01 = p00 + 1, p10 = p00 + span, p11 = p10 + 1;
                _builder.Triangle(_subPos[p00], _subCol[p00], _subNrm[p00], _subUv[p00],
                                  _subPos[p01], _subCol[p01], _subNrm[p01], _subUv[p01],
                                  _subPos[p11], _subCol[p11], _subNrm[p11], _subUv[p11]);
                _builder.Triangle(_subPos[p00], _subCol[p00], _subNrm[p00], _subUv[p00],
                                  _subPos[p11], _subCol[p11], _subNrm[p11], _subUv[p11],
                                  _subPos[p10], _subCol[p10], _subNrm[p10], _subUv[p10]);
            }
    }

    /// <summary>A flat (water) tile as two textured triangles, the ground texture stretched once across it.</summary>
    private void EmitFlatTile(Vector2 a, Vector2 b, Vector2 c, Vector2 d, int tx, int ty,
                              TextureAtlas.Region tex, bool flipU, bool flipV,
                              float wTL, float wTR, float wBR, float wBL, bool overlay)
    {
        Vector3 up = Vector3.Up;
        Color cA = overlay ? BlendColor(wTL, wTR, wBR, wBL, 0f, 0f, tx, ty) : TerrainWhite;
        Color cB = overlay ? BlendColor(wTL, wTR, wBR, wBL, 1f, 0f, tx, ty) : TerrainWhite;
        Color cC = overlay ? BlendColor(wTL, wTR, wBR, wBL, 1f, 1f, tx, ty) : TerrainWhite;
        Color cD = overlay ? BlendColor(wTL, wTR, wBR, wBL, 0f, 1f, tx, ty) : TerrainWhite;
        Vector2 uvA = TileUv(tex, 0f, 0f, flipU, flipV);
        Vector2 uvB = TileUv(tex, 1f, 0f, flipU, flipV);
        Vector2 uvC = TileUv(tex, 1f, 1f, flipU, flipV);
        Vector2 uvD = TileUv(tex, 0f, 1f, flipU, flipV);
        _builder.Triangle(a, cA, up, uvA, b, cB, up, uvB, c, cC, up, uvC);
        _builder.Triangle(a, cA, up, uvA, c, cC, up, uvC, d, cD, up, uvD);
    }

    /// <summary>A land tile is coastal if it is above the sea but at least one of its eight neighbours is under it.</summary>
    private bool IsCoastalLand(int lx, int ly)
    {
        int c = (ly + FieldMargin) * FieldStride + (lx + FieldMargin);
        if (_heightField[c] < WorldConstants.SeaLevel)
            return false;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if (_heightField[(ly + FieldMargin + dy) * FieldStride + (lx + FieldMargin + dx)] < WorldConstants.SeaLevel)
                    return true;
        return false;
    }

    /// <summary>
    /// A shore tile: a flat sheet of shallow water across the tile, then the land laid on
    /// top and faded out (per sub-vertex) exactly where the smoothly-interpolated height
    /// drops below sea level. The land's own edge is therefore the real waterline — a
    /// rounded curve through the tile — with the water showing through beyond it. The land
    /// itself is still blended between biomes, so its inland edge (say sand into grass) is
    /// as soft as anywhere else.
    /// </summary>
    private void EmitCoastalTile(int ox, int oy, int lx, int ly, int tx, int ty, int baseTex, bool flipU, bool flipV)
    {
        // Shallow water, flat at sea level, filling the whole tile behind the land.
        Vector2 a0 = Iso.TileToIso(tx, ty, 0f), b0 = Iso.TileToIso(tx + 1, ty, 0f);
        Vector2 c0 = Iso.TileToIso(tx + 1, ty + 1, 0f), d0 = Iso.TileToIso(tx, ty + 1, 0f);
        EmitFlatTile(a0, b0, c0, d0, tx, ty, _atlas.Terrain[9], flipU, flipV, 1f, 1f, 1f, 1f, false);

        const int n = CoastSubdiv, span = CoastSubdiv + 1;
        FillSubGrid(ox, oy, lx, ly, tx, ty, n, span, coastal: true);
        EmitLandLayers(baseTex, flipU, flipV, n, span);
    }

    /// <summary>A smoothly interpolated continuous height at a chunk-local fractional position (bicubic Catmull-Rom).</summary>
    private float SmoothHeight(float fx, float fy)
    {
        int ix = (int)MathF.Floor(fx), iy = (int)MathF.Floor(fy);
        float u = fx - ix, v = fy - iy;
        float r0 = CatmullRom(Hf(ix - 1, iy - 1), Hf(ix, iy - 1), Hf(ix + 1, iy - 1), Hf(ix + 2, iy - 1), u);
        float r1 = CatmullRom(Hf(ix - 1, iy), Hf(ix, iy), Hf(ix + 1, iy), Hf(ix + 2, iy), u);
        float r2 = CatmullRom(Hf(ix - 1, iy + 1), Hf(ix, iy + 1), Hf(ix + 1, iy + 1), Hf(ix + 2, iy + 1), u);
        float r3 = CatmullRom(Hf(ix - 1, iy + 2), Hf(ix, iy + 2), Hf(ix + 1, iy + 2), Hf(ix + 2, iy + 2), u);
        return CatmullRom(r0, r1, r2, r3, v);
    }

    /// <summary>Reads the cached height field by chunk-local tile coordinate, clamped to the margin.</summary>
    private float Hf(int localX, int localY)
    {
        int fx = Math.Clamp(localX + FieldMargin, 0, FieldStride - 1);
        int fy = Math.Clamp(localY + FieldMargin, 0, FieldStride - 1);
        return _heightField[fy * FieldStride + fx];
    }

    /// <summary>
    /// The premultiplied white an overlay vertex draws at: its blend weight (bilinear
    /// across the tile's four corner weights) roughened by position noise so the edge of
    /// the blend wanders. rgb = a = weight, so the layer composites as weight·texture over
    /// what is already there.
    /// </summary>
    private static Color BlendColor(float wTL, float wTR, float wBR, float wBL, float u, float v, int tx, int ty)
    {
        float top = MathHelper.Lerp(wTL, wTR, u);
        float bottom = MathHelper.Lerp(wBL, wBR, u);
        float w = MathHelper.Lerp(top, bottom, v);
        w += (ValueNoise((tx + u) * BlendNoiseFreq, (ty + v) * BlendNoiseFreq) - 0.5f) * BlendNoiseAmp;
        return TerrainWhite * Math.Clamp(w, 0f, 1f);
    }

    /// <summary>
    /// Finds the textures worn by tiles around this one that differ from its own, and how
    /// strongly each shows at the tile's four corners (the share of the corner's four
    /// tiles wearing it). Land and water never bleed into one another — that join is the
    /// shore, drawn with surf — so only same-element textures are gathered. Returns the
    /// overlay count, filling <see cref="_blendTex"/> and <see cref="_blendW"/>.
    /// </summary>
    private int GatherBlend(World world, int tx, int ty, int baseTex)
    {
        // The 3×3 block of texture indices around the tile (-1 where nothing is loaded).
        Span<int> t = stackalloc int[9];
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int wx = tx + dx, wy = ty + dy;
                t[(dy + 1) * 3 + (dx + 1)] =
                    world.TryGetTile(wx, wy, out Tile n) ? TerrainTextureIndex(n.Biome, wx, wy) : -1;
            }

        // The four tiles meeting at each of the tile's corners, as indices into the block.
        ReadOnlySpan<int> corners = stackalloc int[] { 0, 1, 3, 4, /*TL*/ 1, 2, 4, 5, /*TR*/ 4, 5, 7, 8, /*BR*/ 3, 4, 6, 7 /*BL*/ };

        int count = 0;
        for (int c = 0; c < 9 && count < MaxBlendOverlays; c++)
        {
            int cand = t[c];
            if (cand < 0 || cand == baseTex || !CanBlend(baseTex, cand))
                continue;
            bool seen = false;
            for (int k = 0; k < count; k++) if (_blendTex[k] == cand) { seen = true; break; }
            if (seen) continue;

            _blendTex[count] = cand;
            for (int q = 0; q < 4; q++)
            {
                int hit = 0, valid = 0;
                for (int m = 0; m < 4; m++)
                {
                    int v = t[corners[q * 4 + m]];
                    if (v < 0) continue;
                    valid++;
                    if (v == cand) hit++;
                }
                _blendW[count * 4 + q] = valid > 0 ? hit / (float)valid : 0f;
            }
            count++;
        }
        return count;
    }

    private static bool IsWaterTex(int tex) => tex == 8 || tex == 9;

    /// <summary>
    /// Whether two ground textures may blend into one another. Water and land never do:
    /// that meeting is the shore, a real waterline kept crisp (and carved by the coastal
    /// erosion pass, not faded). Land blends with land, and deep water with shallow.
    /// </summary>
    private static bool CanBlend(int a, int b) => IsWaterTex(a) == IsWaterTex(b);

    /// <summary>Smooth value noise in 0..1 from the integer hash, for roughening the blend edge.</summary>
    private static float ValueNoise(float x, float y)
    {
        int xi = (int)MathF.Floor(x), yi = (int)MathF.Floor(y);
        float fx = x - xi, fy = y - yi;
        fx = fx * fx * (3f - 2f * fx);
        fy = fy * fy * (3f - 2f * fy);
        float top = MathHelper.Lerp(Hash01(xi, yi), Hash01(xi + 1, yi), fx);
        float bottom = MathHelper.Lerp(Hash01(xi, yi + 1), Hash01(xi + 1, yi + 1), fx);
        return MathHelper.Lerp(top, bottom, fy);
    }

    /// <summary>The atlas UV for a point (u,v) in a tile, mapped into the texture's region and optionally flipped.</summary>
    private static Vector2 TileUv(in TextureAtlas.Region tex, float u, float v, bool flipU, bool flipV)
    {
        if (flipU) u = 1f - u;
        if (flipV) v = 1f - v;
        return new Vector2(MathHelper.Lerp(tex.U0, tex.U1, u), MathHelper.Lerp(tex.V0, tex.V1, v));
    }

    /// <summary>Which of the 12 ground textures a biome wears. Indices are the terrain sheet's row-major order.</summary>
    private static int TerrainTextureIndex(Biome biome, int tx, int ty) => BiomeTextureIndex[(int)biome];

    /// <summary>Biome → terrain-sheet tile. See the sheet: 0 grass · 1 dirt · 2 snow · 3 dry grass · 4 sand · 5 frost-dirt · 6 rock · 7 orange flowers · 8 deep water · 9 shallow water · 10 swamp · 11 flower meadow.</summary>
    private static readonly int[] BiomeTextureIndex =
    {
        8,  // Ocean
        9,  // ShallowWater
        4,  // Beach → sand
        0,  // Grassland → grass
        3,  // Savanna → dry grass
        4,  // Desert → sand
        0,  // TemperateForest → grass
        1,  // Taiga → dirt
        5,  // Tundra → frost-dirt
        2,  // Snow
        11, // Jungle → flower meadow (lush)
        10, // Swamp
        6,  // Rock
    };

    /// <summary>A deterministic per-tile flip in u and/or v, so a repeating texture doesn't read as a stamped grid.</summary>
    private static void TileFlip(int tx, int ty, out bool flipU, out bool flipV)
    {
        uint h = (uint)((tx * 40503) ^ (ty * 20693) ^ 0x1f3d);
        h ^= h >> 13;
        flipU = (h & 1u) != 0;
        flipV = (h & 2u) != 0;
    }

    /// <summary>A smoothly interpolated elevation level at a chunk-local fractional position (bicubic Catmull-Rom).</summary>
    private float SmoothLevel(float fx, float fy)
    {
        int ix = (int)MathF.Floor(fx), iy = (int)MathF.Floor(fy);
        float u = fx - ix, v = fy - iy;
        float r0 = CatmullRom(Lf(ix - 1, iy - 1), Lf(ix, iy - 1), Lf(ix + 1, iy - 1), Lf(ix + 2, iy - 1), u);
        float r1 = CatmullRom(Lf(ix - 1, iy), Lf(ix, iy), Lf(ix + 1, iy), Lf(ix + 2, iy), u);
        float r2 = CatmullRom(Lf(ix - 1, iy + 1), Lf(ix, iy + 1), Lf(ix + 1, iy + 1), Lf(ix + 2, iy + 1), u);
        float r3 = CatmullRom(Lf(ix - 1, iy + 2), Lf(ix, iy + 2), Lf(ix + 1, iy + 2), Lf(ix + 2, iy + 2), u);
        return CatmullRom(r0, r1, r2, r3, v);
    }

    /// <summary>The surface normal of the smoothed height field, from a central difference of <see cref="SmoothLevel"/>.</summary>
    private Vector3 SmoothNormal(float fx, float fy)
    {
        const float e = 0.5f;
        float slopeX = SmoothLevel(fx + e, fy) - SmoothLevel(fx - e, fy);
        float slopeY = SmoothLevel(fx, fy + e) - SmoothLevel(fx, fy - e);
        return Vector3.Normalize(new Vector3(-slopeX * 2f * NormalRelief, 2f, -slopeY * 2f * NormalRelief));
    }

    /// <summary>Reads the cached level field by chunk-local tile coordinate, clamped to the margin.</summary>
    private float Lf(int localX, int localY)
    {
        int fx = Math.Clamp(localX + FieldMargin, 0, FieldStride - 1);
        int fy = Math.Clamp(localY + FieldMargin, 0, FieldStride - 1);
        return _levelField[fy * FieldStride + fx];
    }

    /// <summary>Bilinear sample of a cached field at a chunk-local fractional position (climate is smooth, so bilinear is plenty).</summary>
    private float SampleField(float[] field, float flx, float fly)
    {
        int ix = (int)MathF.Floor(flx), iy = (int)MathF.Floor(fly);
        float u = flx - ix, v = fly - iy;
        float a = Ff(field, ix, iy), b = Ff(field, ix + 1, iy);
        float c = Ff(field, ix, iy + 1), d = Ff(field, ix + 1, iy + 1);
        return MathHelper.Lerp(MathHelper.Lerp(a, b, u), MathHelper.Lerp(c, d, u), v);
    }

    private float Ff(float[] field, int localX, int localY)
    {
        int fx = Math.Clamp(localX + FieldMargin, 0, FieldStride - 1);
        int fy = Math.Clamp(localY + FieldMargin, 0, FieldStride - 1);
        return field[fy * FieldStride + fx];
    }

    /// <summary>
    /// The ground texture a point inside a boundary tile belongs to, by classifying the
    /// biome from the smooth climate there — but sampled through a position warp, so the
    /// (very low-frequency, near-straight) climate contour the biomes divide on comes out
    /// as a naturally wandering edge rather than a smooth arc. The warp is a function of
    /// world position, so two chunks agree on the border they share.
    /// </summary>
    private int SubTextureIndex(int ox, int oy, float flx, float fly)
    {
        float wx = ox + flx, wy = oy + fly;
        float dx = (ValueNoise(wx * BiomeWarpFreq, wy * BiomeWarpFreq) - 0.5f) * (2f * BiomeWarpAmp);
        float dy = (ValueNoise(wx * BiomeWarpFreq + 53.3f, wy * BiomeWarpFreq + 17.1f) - 0.5f) * (2f * BiomeWarpAmp);
        float sx = flx + dx, sy = fly + dy;
        Biome b = BiomeClassifier.Classify(SampleField(_heightField, sx, sy),
                                            SampleField(_tempField, sx, sy),
                                            SampleField(_moistField, sx, sy));
        return BiomeTextureIndex[(int)b];
    }

    /// <summary>
    /// A land tile straddling a biome border. It is subdivided fine and each sub-vertex is
    /// classified to its own biome's texture from the smooth climate, so the border runs
    /// as a natural contour through the tile. The tile's own texture is laid down opaque,
    /// then every other texture found among the sub-vertices is painted over it, showing
    /// only where the ground classifies to it — the layers meet along the contour, blurred
    /// one sub-cell wide by the interpolation, so nothing is a hard tile edge.
    /// </summary>
    private void EmitBlendedLandTile(int ox, int oy, int lx, int ly, int tx, int ty, int baseTex, bool flipU, bool flipV)
    {
        const int n = BlendSubdiv, span = BlendSubdiv + 1;
        FillSubGrid(ox, oy, lx, ly, tx, ty, n, span, coastal: false);
        EmitLandLayers(baseTex, flipU, flipV, n, span);
    }

    /// <summary>
    /// Fills the sub-vertex grid — position, normal, classified texture, and master alpha
    /// — for a land tile. On a shore tile the alpha fades to 0 across the waterline (so the
    /// land is cut there); everywhere else it is 1.
    /// </summary>
    private void FillSubGrid(int ox, int oy, int lx, int ly, int tx, int ty, int n, int span, bool coastal)
    {
        for (int i = 0; i <= n; i++)
        {
            float v = i / (float)n;
            for (int j = 0; j <= n; j++)
            {
                float u = j / (float)n;
                float flx = lx + u, fly = ly + v;
                int k = i * span + j;
                _subPos[k] = Iso.TileToIso(tx + u, ty + v, SmoothLevel(flx, fly));
                _subNrm[k] = SmoothNormal(flx, fly);
                _subTex[k] = SubTextureIndex(ox, oy, flx, fly);
                _subA[k] = coastal
                    ? Smoothstep01((SmoothHeight(flx, fly) - WorldConstants.SeaLevel) / CoastFeather)
                    : 1f;
            }
        }
    }

    /// <summary>
    /// Paints the classified sub-grid: the tile's own texture opaque as a base, then every
    /// other land texture found among the sub-vertices over it, each showing only where the
    /// ground classifies to it. The whole thing is scaled by the master alpha, so a shore
    /// tile's blended land still fades out at the waterline.
    /// </summary>
    private void EmitLandLayers(int baseTex, bool flipU, bool flipV, int n, int span)
    {
        EmitTexLayer(n, span, _atlas.Terrain[baseTex], flipU, flipV, baseLayer: true, overlayTex: -1);

        int count = 0;
        for (int k = 0; k < span * span && count < MaxBlendOverlays; k++)
        {
            int st = _subTex[k];
            // Land never blends to a water texture: where the warped climate dips below the
            // sea the sub-vertex just keeps the tile's own (land) texture. The real
            // waterline is the shore cut's job.
            if (st == baseTex || !CanBlend(baseTex, st)) continue;
            bool seen = false;
            for (int q = 0; q < count; q++) if (_blendTex[q] == st) { seen = true; break; }
            if (!seen) _blendTex[count++] = st;
        }
        for (int o = 0; o < count; o++)
            EmitTexLayer(n, span, _atlas.Terrain[_blendTex[o]], flipU, flipV, baseLayer: false, overlayTex: _blendTex[o]);
    }

    /// <summary>Emits one texture layer over the classified sub-grid: opaque for the base, or opaque only where a sub-vertex classifies to <paramref name="overlayTex"/> for an overlay — all scaled by the per-sub-vertex master alpha.</summary>
    private void EmitTexLayer(int n, int span, TextureAtlas.Region tex, bool flipU, bool flipV, bool baseLayer, int overlayTex)
    {
        for (int i = 0; i <= n; i++)
            for (int j = 0; j <= n; j++)
            {
                int k = i * span + j;
                _subUv[k] = TileUv(tex, j / (float)n, i / (float)n, flipU, flipV);
                _subCol[k] = (baseLayer || _subTex[k] == overlayTex ? TerrainWhite : ColorNone) * _subA[k];
            }

        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                int p00 = i * span + j, p01 = p00 + 1, p10 = p00 + span, p11 = p10 + 1;
                _builder.Triangle(_subPos[p00], _subCol[p00], _subNrm[p00], _subUv[p00],
                                  _subPos[p01], _subCol[p01], _subNrm[p01], _subUv[p01],
                                  _subPos[p11], _subCol[p11], _subNrm[p11], _subUv[p11]);
                _builder.Triangle(_subPos[p00], _subCol[p00], _subNrm[p00], _subUv[p00],
                                  _subPos[p11], _subCol[p11], _subNrm[p11], _subUv[p11],
                                  _subPos[p10], _subCol[p10], _subNrm[p10], _subUv[p10]);
            }
    }

    /// <summary>Fully transparent premultiplied — an overlay sub-vertex that does not belong to that layer.</summary>
    private static readonly Color ColorNone = new(0, 0, 0, 0);

    /// <summary>Catmull-Rom spline through <paramref name="p1"/>..<paramref name="p2"/>; passes through the control points.</summary>
    private static float CatmullRom(float p0, float p1, float p2, float p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return 0.5f * (2f * p1
                       + (-p0 + p2) * t
                       + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2
                       + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
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
    // In level-steps, and scaled with the four-fold finer elevation levels so a cliff
    // still bares rock at the same real steepness, not a quarter of it.
    private const float CliffStart = 6f;
    private const float CliffFull = 16f;

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

        // Elevation and slope shading used to be baked in here on the CPU. The scene
        // is now lit for real by the sun (see VertexNormal and the directional light
        // in Draw), so this returns only the surface's own albedo — its biome colour,
        // bared to rock on cliffs, with a little per-vertex jitter so a flat expanse
        // still reads as textured ground rather than one solid fill.
        float jitter = 0.96f + 0.08f * Hash01(vx, vy);
        return Scale(ground, jitter);
    }

    /// <summary>Height (in level units) fed into the normal per horizontal tile — how steep a slope reads to the light.</summary>
    private const float NormalRelief = 0.2125f;

    /// <summary>
    /// The world-space surface normal at a mesh vertex, from the slope of the level
    /// field around it (central differences). x runs east, z south, y up; the terrain
    /// mesh is drawn in flattened iso-space, but the light works in this upright frame,
    /// so a hillside genuinely turns its face toward or away from the sun.
    /// </summary>
    private static Vector3 VertexNormal(World world, int vx, int vy)
    {
        float west = LevelAt(world, vx - 1, vy), east = LevelAt(world, vx + 1, vy);
        float north = LevelAt(world, vx, vy - 1), south = LevelAt(world, vx, vy + 1);
        var n = new Vector3((west - east) * NormalRelief, 2f, (north - south) * NormalRelief);
        return Vector3.Normalize(n);
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

    /// <summary>How dark a plant's ground shadow is, premultiplied over the terrain.</summary>
    private static readonly Color ShadowTint = new Color(6, 10, 8) * 0.30f;

    /// <summary>
    /// A plant's shadow, pooled on the ground at its foot. It is a single dark disc
    /// squashed flat to lie in the ground plane and nudged to the south-east, away
    /// from the north-west light the terrain is already shaded by, so every shadow on
    /// the map falls the same way. Its size tracks the plant, so a tall tree throws a
    /// broad pool and a shrub only a small one; grass is too slight to shadow.
    /// </summary>
    private void AppendShadow(in VegItem item)
    {
        float radius = ShadowRadius(item);
        if (radius <= 0f)
            return;

        // Squashed to the ground plane and offset south-east (down-screen) from the
        // foot, so the pool reads as lying on the floor rather than standing up.
        Vector2 center = item.Pos + new Vector2(radius * 0.32f, radius * 0.16f);
        var size = new Vector2(radius * 2f, radius * 2f * ShadowSquash);
        _builder.Sprite(center, size, Center, _atlas.Disc, ShadowTint);
    }

    /// <summary>How flat a shadow lies: its height as a fraction of its width.</summary>
    private const float ShadowSquash = 0.5f;

    /// <summary>The ground radius (iso pixels) of a plant's shadow, or 0 for none.</summary>
    private static float ShadowRadius(in VegItem item) => item.Veg switch
    {
        Vegetation.BroadleafTree or Vegetation.PalmTree or Vegetation.PineTree
            => TreeHeight * 0.30f * item.Scale,
        Vegetation.Bush => BushHeight * 0.42f * item.Scale,
        Vegetation.Cactus => 4.5f * item.Scale,
        Vegetation.Rock => 6f * item.Scale,
        _ => 0f, // grass tufts are too slight to cast one
    };

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
        _clouds.Dispose();
        _effect.Dispose();
        _atlas.Texture.Dispose();
    }
}
