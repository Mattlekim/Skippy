using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Skippy.Core.Worlds.Biomes;
using Skippy.Core.Worlds.Generation;
using Skippy.Core.Worlds.Mobs;

namespace Skippy.Core.Worlds.Rendering;

/// <summary>
/// Draws a <see cref="World"/> in isometric projection with smooth,
/// Age-of-Empires-II-style terrain: elevation lives at tile corners, so each
/// tile is a four-corner quad that ramps between neighbours.
///
/// Rendering strategy:
///   • Every loaded chunk is turned into static GPU meshes (<see cref="ChunkMesh"/>)
///     in iso-space coordinates — its ground, the shadows its plants cast on that
///     ground, and the plants themselves, drawn in that order across every visible
///     chunk so the shadow layer can be sheared and faded by the sun on its own. They
///     are built once, cached, and only rebuilt when a neighbouring chunk appears
///     (edge vertices blend across the border). Moving the camera changes just the
///     effect's view matrix — no per-frame vertex uploads.
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

    /// <summary>The ground of each chunk: terrain, water and surf. Drawn first, and the only
    /// mesh whose bounds and neighbour mask the chunk's freshness is judged by.</summary>
    private readonly Dictionary<ChunkCoord, ChunkMesh> _meshes = new();

    /// <summary>
    /// Each chunk's plant shadows, kept apart from the ground they fall on and the plants
    /// that cast them so they can be drawn as their own layer: sheared by the sun, faded by
    /// how far it has risen, and skipped outright once it is down.
    /// </summary>
    private readonly Dictionary<ChunkCoord, ChunkMesh> _shadowMeshes = new();

    /// <summary>Each chunk's plants, drawn over every shadow so none is cast on a trunk.</summary>
    private readonly Dictionary<ChunkCoord, ChunkMesh> _vegMeshes = new();

    /// <summary>Tile-grid overlay meshes, built lazily and only while the grid is switched on.</summary>
    private readonly Dictionary<ChunkCoord, ChunkMesh> _gridMeshes = new();

    /// <summary>
    /// The mobs, and the shadows they pool on the ground. Everything else in the world
    /// stands still once it is built and so lives in a chunk's mesh; a mob is somewhere
    /// else every frame, so it is gathered fresh into these each time round (see
    /// <see cref="SpriteLayer"/>).
    /// </summary>
    private readonly SpriteLayer _mobSprites = new();
    private readonly SpriteLayer _mobShadows = new();

    /// <summary>The mobs on screen this frame, sorted so the nearer ones draw over the further.</summary>
    private readonly List<MobDraw> _visibleMobs = new();

    /// <summary>Where each plant of the chunk being built starts in its mesh (see <see cref="ChunkMesh.Slice"/>).</summary>
    private readonly List<ChunkMesh.Slice> _vegSlices = new();

    private readonly List<ChunkCoord> _scratchCoords = new();
    private readonly List<Chunk> _visible = new();
    private readonly List<VegItem> _veg = new();
    private readonly List<ShoreEdge> _shore = new();

    /// <summary>Per-vertex colours for the (Chunk.Size + 1)² corner grid of the chunk being built.</summary>
    private readonly Color[] _cornerColors = new Color[(Chunk.Size + 1) * (Chunk.Size + 1)];

    /// <summary>World-space surface normal at each of those corners, for the sun to light.</summary>
    private readonly Vector3[] _cornerNormals = new Vector3[(Chunk.Size + 1) * (Chunk.Size + 1)];

    /// <summary>
    /// How many sub-cells every land tile is split into per side. Splitting lets the
    /// surface follow a smoothly interpolated height (no creased corners), the shore be
    /// cut along a rounded waterline, and biome textures blend along a contour. It is one
    /// value for ALL land tiles on purpose: if a plain tile and a border tile split their
    /// shared edge into a different number of segments, the two approximations of the
    /// curve don't meet and a hairline T-junction shows along the tile edge.
    /// </summary>
    private const int Subdiv = 6;

    /// <summary>
    /// Shore tiles use the same subdivision as every other land tile — they have to, or the
    /// shared edge cracks (see <see cref="Subdiv"/>). The waterline is not resolved by
    /// splitting them finer but by cutting each cell along it; see <see cref="EmitSubGrid"/>.
    /// </summary>
    private const int CoastSubdiv = Subdiv;

    /// <summary>
    /// Where the waterline sits in the wetness field (see <see cref="_wetField"/>) and the
    /// band it fades over. Half way between water and land is the line itself; the feather
    /// is what keeps it a soft edge of wet sand rather than a cut-out.
    /// </summary>
    private const float ShoreLine = 0.5f;
    private const float ShoreFeather = 0.05f;

    /// <summary>How far the waterline is warped off the tile grid, and how coarse that wander is. In tiles; kept well inside the field margin.</summary>
    private const float ShoreWarpAmp = 0.4f;
    private const float ShoreWarpFreq = 0.4f;

    /// <summary>Elevation steps the ground climbs out of the water over, at the waterline.</summary>
    private const float ShoreRise = 0.4f;

    /// <summary>Elevation steps above the surface beyond which ground is dry whatever the wet field says.</summary>
    private const float ShoreBank = 1.2f;

    /// <summary>The chunk's fields plus a four-tile margin, wide enough for the smoothing stencil and the warped biome lookup to never leave it.</summary>
    private const int FieldStride = Chunk.Size + 8;
    private const int FieldMargin = 4;
    private readonly float[] _levelField = new float[FieldStride * FieldStride];

    /// <summary>The chunk's continuous fields (same margin): height for relief, climate to classify biomes within a boundary tile.</summary>
    private readonly float[] _heightField = new float[FieldStride * FieldStride];
    private readonly float[] _tempField = new float[FieldStride * FieldStride];
    private readonly float[] _moistField = new float[FieldStride * FieldStride];

    /// <summary>
    /// Which tiles are water: 0 on a water tile, 1 on dry land. The waterline is the boundary
    /// of the water tiles themselves rather than a height read off the terrain, so it holds
    /// for any body of water, wherever its surface happens to sit.
    /// </summary>
    private readonly float[] _wet = new float[FieldStride * FieldStride];

    /// <summary>
    /// <see cref="_wet"/> softened by one tent blur, then sampled smoothly: the continuous
    /// field the shoreline is cut from. Blurring first is what rounds the coast off — read
    /// straight, the raw 0/1 field can only bend inside a single tile and the shore comes
    /// out as the tile grid's own staircase.
    /// </summary>
    private readonly float[] _wetField = new float[FieldStride * FieldStride];

    /// <summary>A boundary tile is subdivided this fine so its biome edge follows the climate contour smoothly.</summary>
    private const int BlendSubdiv = Subdiv;

    /// <summary>How far the biome edge is warped off the (very smooth) climate contour so it wanders naturally. In tiles; kept inside the field margin.</summary>
    private const float BiomeWarpAmp = 1.4f;
    private const float BiomeWarpFreq = 0.11f;

    /// <summary>Scratch for one tile's sub-vertices while it is being emitted (sized for the finest grid used).</summary>
    private const int MaxSub = CoastSubdiv > BlendSubdiv ? CoastSubdiv : BlendSubdiv;
    private readonly Vector2[] _subPos = new Vector2[(MaxSub + 1) * (MaxSub + 1)];
    /// <summary>
    /// Texture coordinates for a tile's sub-cells: four corners each, per cell rather than
    /// per shared sub-vertex, because a ground texture repeats within a tile and its
    /// coordinates jump at each repeat (see <see cref="FillGroundUv"/>).
    /// </summary>
    private readonly Vector2[] _cellUv = new Vector2[MaxSub * MaxSub * 4];
    private readonly Vector3[] _subNrm = new Vector3[(MaxSub + 1) * (MaxSub + 1)];
    private readonly Color[] _subCol = new Color[(MaxSub + 1) * (MaxSub + 1)];

    /// <summary>Per-sub-vertex master alpha: 1 on dry ground, fading to 0 across the waterline on a shore tile.</summary>
    private readonly float[] _subA = new float[(MaxSub + 1) * (MaxSub + 1)];

    /// <summary>
    /// How many nearby points each sub-vertex samples the biome classifier at, and how far
    /// out (in tiles). Averaging them turns the hard per-point classification into a soft
    /// membership, so a texture bleeds a little across the border — including across a
    /// border that runs right along a tile edge, where a single sample would give a hard line.
    /// </summary>
    private const int BlendSamples = 5;
    private const float BlendSampleRadius = 0.6f;
    private static readonly (float dx, float dy)[] BlendOffsets =
    {
        (0f, 0f), (BlendSampleRadius, 0f), (-BlendSampleRadius, 0f), (0f, BlendSampleRadius), (0f, -BlendSampleRadius),
    };

    /// <summary>The texture each of a sub-vertex's samples classified to, and the resulting per-overlay membership 0..1.</summary>
    private readonly int[] _sampleTex = new int[(MaxSub + 1) * (MaxSub + 1) * BlendSamples];
    private readonly float[] _subMem = new float[(MaxSub + 1) * (MaxSub + 1) * MaxBlendOverlays];

    /// <summary>
    /// Snow is not a biome or a texture — it is weather. It lies wherever the ground is
    /// cold enough, whatever that ground is, and is dusted over it: first flakes at
    /// <see cref="SnowFirstCelsius"/>, lying unbroken by <see cref="SnowSolidCelsius"/>.
    /// It also needs moisture — nothing falls on a cold desert — and the cover breaks into
    /// drifts through a noise threshold, so the edge of its range is patchy, not a line.
    ///
    /// The temperatures are in degrees Celsius rather than in the climate field's own
    /// 0..1, so the snow line stays where it is if that field's mapping is retuned. They
    /// sit above literal freezing because the field is a yearly average: tundra averages a
    /// few degrees above zero and still lies under snow most of the year, and a snow line
    /// pinned to 0 °C left the cold band — the whole of tundra and taiga — nearly bare.
    ///
    /// The moisture floor is low for the same reason. Tundra is dry, drier than most
    /// deserts, but nothing thaws there, so what little falls stays.
    /// </summary>
    private const float SnowFirstCelsius = 6f;
    private const float SnowSolidCelsius = -1f;
    private const float SnowMoistMin = 0.08f;
    private const float SnowMoistFull = 0.30f;
    private const float SnowPatchSoft = 0.18f;
    private const float SnowGateCelsius = 10f; // tiles warmer than this never bother computing snow
    private const float SnowCanopy = 1.4f;    // branches catch snow a little more readily than open ground
    private static readonly Color SnowColor = new(249, 251, 255);

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

    /// <summary>How much taller than a broadleaf a palm stands — most of it is trunk.</summary>
    private const float PalmHeight = 1.25f;

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

        /// <summary>How much snow lies on this plant's crown, 0..1 — from the climate at its foot.</summary>
        public readonly float Snow;

        /// <summary>
        /// Which of its kind's painted forms the wood around this tree is grown from, 0..1
        /// (<see cref="Tile.ForestVariant"/>), or <see cref="NoVariant"/> for a plant that
        /// belongs to no stand and is simply rolled at random.
        /// </summary>
        public readonly float Variant;

        public VegItem(Vector2 pos, Vegetation veg, float scale, bool dead, uint seed, float snow, float variant)
        {
            Pos = pos;
            Veg = veg;
            Scale = scale;
            Dead = dead;
            Seed = seed;
            Snow = snow;
            Variant = variant;
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

        // The depth-tested foliage/mob pass writes depth, which cannot coexist with the
        // smooth alpha blend the painter path uses — a translucent leaf edge would stamp an
        // invisible occluder. So that pass alpha-tests instead: a texel is either kept whole
        // (and writes depth) or discarded. BasicEffect has no alpha test; AlphaTestEffect is
        // the stock effect that does, sharing the same vertex format and matrices. It does
        // not light, so the day's tint on the foliage is folded into DiffuseColor per frame.
        _foliageEffect = new AlphaTestEffect(gd)
        {
            VertexColorEnabled = true,
            Texture = _atlas.Texture,
            World = Matrix.Identity,
            ReferenceAlpha = 128,
            AlphaFunction = CompareFunction.GreaterEqual,
        };
    }

    /// <summary>
    /// Alpha-testing effect for the depth-tested foliage/mob pass. See the constructor.
    /// </summary>
    private readonly AlphaTestEffect _foliageEffect;

    /// <summary>
    /// Depth on for the ground and the foliage/mob billboards while the depth path is live:
    /// nearer (larger iso-Y) wins, and equal depths — a terrain overlay over its base, a
    /// plant's foot on the ground it stands on — still draw, so LessEqual rather than Less.
    /// </summary>
    private static readonly DepthStencilState DepthReadWrite = new()
    {
        DepthBufferEnable = true,
        DepthBufferWriteEnable = true,
        DepthBufferFunction = CompareFunction.LessEqual,
    };

    /// <summary>
    /// How much of the depth range one screen-height of iso-Y spends. The depth key is
    /// centred at 0.5 and drifts ±(1/2·margin) over the visible height, so margin = 2 keeps
    /// on-screen content inside [0.25, 0.75] — clear of the near and far planes with room for
    /// a billboard whose foot sits a little off the bottom of the screen.
    /// </summary>
    private const float DepthMargin = 2f;

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

        // Kept for picking, which happens on the update side and has no viewport of its own.
        _viewCenter = center;
        _viewFocus = focusIso;
        _viewZoom = zoom;

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
                DisposeChunkMeshes(chunk.Coord);
                BuildMesh(world, chunk, mask, out mesh, out ChunkMesh shadows, out ChunkMesh veg);
                _meshes[chunk.Coord] = mesh;
                _shadowMeshes[chunk.Coord] = shadows;
                _vegMeshes[chunk.Coord] = veg;
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

        // Iso-space → screen: translate to the focus, scale by zoom, centre in the viewport.
        Matrix view = Matrix.CreateTranslation(-focusIso.X, -focusIso.Y, 0f)
                    * Matrix.CreateScale(zoom, zoom, 1f)
                    * Matrix.CreateTranslation(center.X, center.Y, 0f);

        // The plain orthographic projection the painter path has always used: xy → screen,
        // z left flat. The two depth-coupled variants below are built from it.
        float vw = gd.Viewport.Width, vh = gd.Viewport.Height;
        Matrix baseProj = Matrix.CreateOrthographicOffCenter(0, vw, vh, 0, 0, 1);
        _effect.Projection = baseProj;

        ApplyLighting(world);

        // The mobs are the one thing here that moved since the last frame, so their
        // geometry is gathered now rather than cached. They join the layers below: their
        // shadows with the plants' shadows, they themselves after the plants.
        BuildMobGeometry(world, isoMin, isoMax);

        float shadowStrength = ShadowStrength(world.Sky);

        // The depth path is only meaningful in the lit game view; the flat data views keep
        // the painter path (and carry no plants or mobs anyway).
        bool depth = world.DepthBuffer && world.MapView == MapView.Biome;

        // Two ways to turn iso-Y into a depth key, one formula: depth = 0.5 − (isoY − focusY)
        // · zoom / (viewportHeight · margin). The ground reads it per fragment from the
        // screen-Y it lands at; a billboard reads it once from its foot (baked into z), so a
        // whole plant or mob resolves at the ground it stands on. Both agree, so terrain in
        // front of a plant (a hill between it and the camera) hides it, and terrain behind
        // does not — the one thing the painter's sort cannot express.
        //
        // The foliage view subtracts the camera focus from z first: the world's iso-Y runs
        // to millions of pixels out, and folding the focus in before the projection keeps the
        // subtraction (and the depth precision) from happening as (huge − huge) on the GPU.
        Matrix viewFoliage = view;
        viewFoliage.M43 = -focusIso.Y;

        Matrix projFoliage = baseProj;
        projFoliage.M33 = -zoom / (vh * DepthMargin);   // z (= footIsoY − focusY) → depth
        projFoliage.M43 = 0.5f;

        Matrix projGround = baseProj;
        if (depth)
        {
            projGround.M23 = -1f / (vh * DepthMargin);      // screen-Y → depth
            projGround.M33 = 0f;                            // the ground's own z is unused
            projGround.M43 = 0.5f + 0.5f / DepthMargin;
        }

        if (depth)
            DrawDepthPath(gd, world, view, viewFoliage, projGround, projFoliage, baseProj, shadowStrength);
        else
            DrawPainterPath(gd, world, view, viewFoliage, baseProj, projFoliage, shadowStrength);

        // Clouds and their shadows sit above the ground, so they come after it — and
        // only in the game view, where they belong (the data views stay unclouded).
        if (world.MapView == MapView.Biome)
        {
            // The poles, in iso space. A row of the world is a line of constant x + y,
            // and Iso maps that straight to iso Y, so the whole band is the strip
            // between these two — which is what the sheet is cut to.
            float isoTop = Iso.TileToIso(0, 0, 0f).Y;
            float isoBottom = Iso.TileToIso(world.Bounds.Span, 0, 0f).Y;
            _clouds.DrawShadows(gd, focusIso, center, zoom, world.Sky, isoTop, isoBottom);
            _clouds.DrawClouds(gd, focusIso, center, zoom, world.Sky, isoTop, isoBottom);
        }
    }

    /// <summary>
    /// The original painter path: three layers — ground, then shadows, then plants with the
    /// mobs sliced in among them by foot-Y — drawn back to front with the depth buffer off,
    /// so draw order alone decides what covers what. Unchanged in behaviour; the plants now
    /// draw under the foliage projection so the foot-depth baked into their z (which the
    /// depth path needs) stays inside the clip range here, where it is otherwise ignored.
    /// </summary>
    private void DrawPainterPath(GraphicsDevice gd, World world, Matrix view, Matrix viewFoliage,
                                 Matrix baseProj, Matrix projFoliage, float shadowStrength)
    {
        gd.DepthStencilState = DepthStencilState.None;
        foreach (EffectPass pass in _effect.CurrentTechnique.Passes)
        {
            _effect.View = view;
            _effect.Projection = baseProj;
            pass.Apply();
            foreach (Chunk chunk in _visible)
                _meshes[chunk.Coord].Draw(gd);

            // Sheared away from the sun (the shadows carry in z how far they may lean;
            // everything else sits at z = 0 and would pass through untouched anyway) and
            // faded by how high the sun has climbed, down to nothing once it has set.
            //
            // The shear rides in the view rather than the world matrix on purpose. It is
            // singular by design (it flattens z), and BasicEffect derives the matrix it
            // lights normals with from the world matrix: given this one it inverts a
            // flattened matrix and every normal comes out garbage, drawing the map black.
            if (shadowStrength > 0f)
            {
                _effect.View = SunShear(world.Sky) * view;
                _effect.Alpha = shadowStrength;
                pass.Apply();
                foreach (Chunk chunk in _visible)
                    _shadowMeshes[chunk.Coord].Draw(gd);

                // A mob's pool is drawn with the plants' — same shear, same strength — so
                // it swings round with the day exactly as theirs do.
                _mobShadows.Draw(gd);
                _effect.Alpha = 1f;
            }

            // The plants, with the mobs standing among them: each chunk's mesh is drawn in
            // slices, broken wherever a mob is standing, so a sheep is covered by the trees
            // in front of it and covers the ones behind.
            _effect.View = viewFoliage;
            _effect.Projection = projFoliage;
            pass.Apply();
            BeginMobPass();
            foreach (Chunk chunk in _visible)
                DrawVegetationWithMobs(gd, chunk.Coord);
            DrawRemainingMobs(gd);

            // The overlay goes last so it sits on top of terrain and vegetation alike.
            if (world.ShowTileGrid)
            {
                _effect.View = view;
                _effect.Projection = baseProj;
                pass.Apply();
                foreach (Chunk chunk in _visible)
                    _gridMeshes[chunk.Coord].Draw(gd);
            }
        }
    }

    /// <summary>
    /// The depth-tested path (toggled with B). The ground writes depth so a plant or a mob
    /// standing behind a hill is hidden by it — the case the painter path cannot draw at all;
    /// the foliage and the mobs then test and write depth too, so they resolve against the
    /// terrain and one another on the GPU, and the whole veg mesh and every mob can be drawn
    /// straight through with no foot-Y sort and no slice interleave.
    ///
    /// Depth and the smooth alpha blend do not mix — a translucent leaf edge would write an
    /// invisible occluder — so the foliage/mob layer alpha-tests through <see cref="_foliageEffect"/>
    /// instead: a texel is kept whole or dropped. Its edges are therefore harder than the
    /// painter path's, and anything it carries below the alpha threshold (a faint snow cap, the
    /// soft selection ring) thins out — the honest cost of writing depth from a billboard.
    /// </summary>
    private void DrawDepthPath(GraphicsDevice gd, World world, Matrix view, Matrix viewFoliage,
                               Matrix projGround, Matrix projFoliage, Matrix baseProj, float shadowStrength)
    {
        // Ground first, writing its per-fragment depth for everything above to test against.
        gd.DepthStencilState = DepthReadWrite;
        foreach (EffectPass pass in _effect.CurrentTechnique.Passes)
        {
            _effect.View = view;
            _effect.Projection = projGround;
            pass.Apply();
            foreach (Chunk chunk in _visible)
                _meshes[chunk.Coord].Draw(gd);
        }

        // Shadows lie on the ground, drawn right after it with depth off — a pool of shade
        // does not occlude, and staying out of the buffer keeps it from fighting the ground.
        if (shadowStrength > 0f)
        {
            gd.DepthStencilState = DepthStencilState.None;
            foreach (EffectPass pass in _effect.CurrentTechnique.Passes)
            {
                _effect.View = SunShear(world.Sky) * view;
                _effect.Projection = baseProj;
                _effect.Alpha = shadowStrength;
                pass.Apply();
                foreach (Chunk chunk in _visible)
                    _shadowMeshes[chunk.Coord].Draw(gd);
                _mobShadows.Draw(gd);
                _effect.Alpha = 1f;
            }
        }

        // Foliage + mobs, depth-tested and depth-writing. Order no longer matters, so the
        // slice machinery is bypassed entirely: all the plants, then all the mobs.
        gd.DepthStencilState = DepthReadWrite;
        _foliageEffect.View = viewFoliage;
        _foliageEffect.Projection = projFoliage;
        _foliageEffect.DiffuseColor = FoliageLight(world.Sky);
        foreach (EffectPass pass in _foliageEffect.CurrentTechnique.Passes)
        {
            pass.Apply();
            foreach (Chunk chunk in _visible)
                _vegMeshes[chunk.Coord].Draw(gd);
            _mobSprites.Draw(gd);
        }

        // The overlay on top, depth off.
        if (world.ShowTileGrid)
        {
            gd.DepthStencilState = DepthStencilState.None;
            foreach (EffectPass pass in _effect.CurrentTechnique.Passes)
            {
                _effect.View = view;
                _effect.Projection = baseProj;
                pass.Apply();
                foreach (Chunk chunk in _visible)
                    _gridMeshes[chunk.Coord].Draw(gd);
            }
        }

        // Hand the device back the way the rest of the frame (clouds, HUD) expects it.
        gd.DepthStencilState = DepthStencilState.None;
    }

    /// <summary>
    /// The flat tint the alpha-testing foliage effect stands in for the sun with, since it
    /// does no lighting of its own. A plant billboard faces straight up (its normal is
    /// <c>Up</c>), so the lit path only ever gave it one shade a frame anyway: the ambient
    /// plus the sun scaled by how much of it an upward face catches. This reproduces that, so
    /// foliage under the depth path tracks the day the same as under the painter path.
    /// </summary>
    private static Vector3 FoliageLight(SkyLight sky)
    {
        float up = MathF.Max(0f, -sky.LightDirection.Y);
        return Vector3.Clamp(sky.Ambient + sky.SunColor * up, Vector3.Zero, Vector3.One);
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
    /// <summary>
    /// Throws away every cached mesh, so the next frame builds them again from whatever the
    /// world now holds.
    ///
    /// The caches are keyed by chunk coordinate, and a regenerated world hands back chunks
    /// at those very same coordinates — so nothing here would otherwise notice that the
    /// ground under them had changed, and the old terrain would keep drawing over a map
    /// that no longer exists. See <see cref="World.Regenerate"/>.
    /// </summary>
    public void Invalidate()
    {
        DisposeAllChunkMeshes();
        foreach (ChunkMesh grid in _gridMeshes.Values) grid.Dispose();
        _gridMeshes.Clear();
    }

    private void DropStaleMeshes(World world)
    {
        bool resized = _isoVersion != Iso.Version;
        bool recoloured = _mapView != world.MapView;
        if (!resized && !recoloured)
            return;

        _isoVersion = Iso.Version;
        _mapView = world.MapView;

        DisposeAllChunkMeshes();

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
            DisposeChunkMeshes(coord);
            if (_gridMeshes.Remove(coord, out ChunkMesh? grid))
                grid.Dispose();
        }
    }

    /// <summary>Drops one chunk's three layers together — they are always built as a set.</summary>
    private void DisposeChunkMeshes(ChunkCoord coord)
    {
        if (_meshes.Remove(coord, out ChunkMesh? ground)) ground.Dispose();
        if (_shadowMeshes.Remove(coord, out ChunkMesh? shadows)) shadows.Dispose();
        if (_vegMeshes.Remove(coord, out ChunkMesh? veg)) veg.Dispose();
    }

    private void DisposeAllChunkMeshes()
    {
        foreach (ChunkMesh mesh in _meshes.Values) mesh.Dispose();
        foreach (ChunkMesh mesh in _shadowMeshes.Values) mesh.Dispose();
        foreach (ChunkMesh mesh in _vegMeshes.Values) mesh.Dispose();
        _meshes.Clear();
        _shadowMeshes.Clear();
        _vegMeshes.Clear();
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

    /// <summary>
    /// Builds one chunk's three layers in one sweep of its tiles: the <paramref name="ground"/>
    /// (terrain, water and surf), the <paramref name="shadows"/> its plants cast on that
    /// ground, and the <paramref name="veg"/> standing on top. They are separate meshes only
    /// so the shadow layer can be drawn under its own matrix and strength; the geometry is
    /// gathered exactly as it would be for a single mesh.
    /// </summary>
    private void BuildMesh(World world, Chunk chunk, byte neighborMask,
                           out ChunkMesh ground, out ChunkMesh shadows, out ChunkMesh veg)
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
                    _wet[i] = ht.IsWater ? 0f : 1f;
                }
                else
                {
                    _heightField[i] = WorldConstants.SeaLevel + 0.1f;
                    _tempField[i] = 0.5f;
                    _moistField[i] = 0.5f;
                    _wet[i] = 1f; // nothing loaded out there: assume land, never invent a coast
                }
            }

        BlurWet();

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

                // Past a pole there is no world to draw — the black shows through. The
                // tile still exists and still holds real terrain, so its neighbour inside
                // the world is shaped and lit against genuine ground; it simply stops
                // here, on the edge of its own diamond.
                if (tile.IsVoid)
                    continue;

                Vector2 a = CornerPos(world, tx, ty);
                Vector2 b = CornerPos(world, tx + 1, ty);
                Vector2 c = CornerPos(world, tx + 1, ty + 1);
                Vector2 d = CornerPos(world, tx, ty + 1);

                // A shore tile is one with water on one side of it and dry land on the
                // other, whichever of the two it is itself. Both kinds are drawn the same
                // way — the water sheet, then the ground over it cut back at the waterline —
                // so the coast is one line across them instead of each tile's own diamond.
                bool shore = IsShoreTile(lx, ly);

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
                    int baseTex = TerrainTextureIndex(tile.Biome, tx, ty);

                    if (!tile.IsWater)
                    {
                        // On a shore tile the water goes down first, over the whole tile, and
                        // the ground is then cut back to the waterline over it.
                        if (shore)
                            EmitWaterSheet(world, lx, ly, tx, ty, ShoreWaterTexture(world, tx, ty, baseTex));

                        // Any land tile near a biome border is subdivided and its textures
                        // blended along the climate contour; the calm interior of a biome
                        // keeps the cheap single quad.
                        if (shore || NearBiomeBorder(world, tx, ty, baseTex))
                            EmitLandTile(world, ox, oy, lx, ly, tx, ty, baseTex, shore);
                        else
                            EmitSmoothTile(lx, ly, tx, ty, _atlas.Terrain[baseTex], 1f, 1f, 1f, 1f, false);

                        // Snow is dusted over any biome that is cold and wet enough. Both land
                        // paths leave their sub-grid in place, so it just reuses that. Skipped
                        // outright anywhere it is too warm.
                        if (tile.TemperatureCelsius < SnowGateCelsius)
                            EmitSnow(ox, oy, lx, ly, Subdiv, Subdiv + 1, shore);
                    }
                    else
                    {
                        // Every water tile draws the same sheet — one rule, so two tiles
                        // always agree along the edge they share. Mixing a subdivided sheet
                        // near the coast with flat quads further out does not work: wherever
                        // the water surface is not dead level the two disagree by a fraction
                        // of a level along their shared edge, and a sliver of nothing shows
                        // between them.
                        EmitWaterSheet(world, lx, ly, tx, ty, baseTex);

                        // ...then whatever of the bank rises out of it. That is what moves
                        // the coast off the tile's diamond and onto the waterline.
                        if (shore)
                            EmitShoreGround(world, ox, oy, lx, ly, tx, ty);
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
                    AppendScatter(world, tile, a, b, c, d, tx, ty, ox, oy, lx, ly, shore);
            }
        }

        // Surf goes on after every tile's ground, or the next tile along would paint
        // over it.
        foreach (ShoreEdge edge in _shore)
            AppendShoreFoam(edge);

        // Culling is judged by the ground layer alone, so its bounds have to cover what
        // the other two put on top of it: a tree standing on the far edge of the chunk,
        // and the shadow it swings out of the chunk as the sun lowers.
        _builder.PadBounds(MaxSpriteHeight + MaxShadowReach);
        ground = _builder.Upload(neighborMask);
        _builder.Clear();

        // Vegetation goes after all terrain, sorted so lower-on-screen plants draw
        // over the ones behind them.
        _veg.Sort(static (p, q) => p.Pos.Y.CompareTo(q.Pos.Y));

        // Every plant's shadow first, as one layer on the ground, then the plants on
        // top: that way a shadow only ever darkens the terrain, never the trunk of a
        // tree standing in front of it.
        foreach (VegItem item in _veg)
            AppendShadow(item);
        shadows = _builder.Upload(neighborMask);
        _builder.Clear();

        // Each plant's place in the mesh is recorded as it goes in, so a mob standing among
        // them can be drawn at its own depth rather than over the lot of them.
        _vegSlices.Clear();
        foreach (VegItem item in _veg)
        {
            _vegSlices.Add(new ChunkMesh.Slice(item.Pos.Y, _builder.Mark()));
            // Every quad of this plant carries its foot's iso-Y as a depth key, so the
            // depth-tested path resolves the whole billboard at its foot rather than by its
            // own screen height (a tall crown must not read as "further back" than its base).
            _builder.DepthZ = item.Pos.Y;
            AppendVegetation(item);
        }
        _builder.DepthZ = 0f;
        veg = _builder.Upload(neighborMask, _vegSlices);
        _builder.Clear();
    }

    /// <summary>The white ground draws the texture at its own colour; only the sun shades it.</summary>
    private static readonly Color TerrainWhite = Color.White;

    /// <summary>How broad the ground's large-scale mottling is: one cycle every ~1/freq tiles.</summary>
    private const float GroundMacroFreq = 0.055f;

    /// <summary>The most the macro mottling darkens the ground (a vertex colour can only darken).</summary>
    private const float GroundMacroDepth = 0.20f;

    /// <summary>
    /// Broad, many-tiles-wide brightness variation folded into the ground so a simple
    /// repeating tile texture stops reading as a grid. A low-frequency fractal (a few octaves
    /// of value noise) darkens the ground in soft, wandering patches far larger than a tile —
    /// like the natural mottling of real land — so no two stretches of the same texture look
    /// quite alike. It only ever darkens, because a vertex colour cannot brighten a texture.
    /// </summary>
    private static float GroundMacro(float worldX, float worldY)
    {
        float x = worldX * GroundMacroFreq, y = worldY * GroundMacroFreq;
        float n = 0.6f * ValueNoise(x, y) + 0.4f * ValueNoise(x * 2.3f + 11.5f, y * 2.3f + 4.7f);
        return 1f - GroundMacroDepth * n;
    }

    /// <summary>
    /// Scales a colour's RGB while leaving its alpha untouched. The ground colours are
    /// premultiplied (rgb ≤ a), so darkening only the RGB dims the shade without eating into
    /// a layer's coverage — which is what the macro mottling wants, and what a plain colour
    /// multiply (which would also scale the alpha, thinning shore cuts and blend edges) does not.
    /// </summary>
    private static Color ScaleRgb(Color c, float m) => new((byte)(c.R * m), (byte)(c.G * m), (byte)(c.B * m), c.A);

    /// <summary>
    /// Emits one land tile as a <see cref="Subdiv"/>×<see cref="Subdiv"/> grid whose
    /// interior heights and normals come from a smooth (Catmull-Rom) interpolation of
    /// the level field. The interpolation passes exactly through the integer corner
    /// heights, so the tile still meets its neighbours — and the flat water beside it —
    /// seamlessly; only the ground in between is rounded off. Each sub-vertex samples the
    /// tile's ground texture <paramref name="tex"/>, repeated across it at a fixed size
    /// (see <see cref="FillGroundUv"/>).
    /// </summary>
    private void EmitSmoothTile(int lx, int ly, int tx, int ty, TextureAtlas.Region tex,
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
                _subNrm[k] = SmoothNormal(fx, fy);
                _subCol[k] = ScaleRgb(overlay ? BlendColor(wTL, wTR, wBR, wBL, u, v, tx, ty) : TerrainWhite,
                                      GroundMacro(tx + u, ty + v));
                _subA[k] = 1f; // no shore cut here, and the snow drawn after reads this
            }
        }

        FillGroundUv(tex, tx, ty, n);
        EmitSubGrid(n, span);
    }

    /// <summary>Emits the sub-vertex grid as two triangles per cell, from whatever is in the scratch arrays.</summary>
    private void EmitSubGrid(int n, int span)
    {
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                int p00 = i * span + j, p01 = p00 + 1, p10 = p00 + span, p11 = p10 + 1;
                int c = (i * n + j) * 4;
                _builder.Triangle(_subPos[p00], _subCol[p00], _subNrm[p00], _cellUv[c],
                                  _subPos[p01], _subCol[p01], _subNrm[p01], _cellUv[c + 1],
                                  _subPos[p11], _subCol[p11], _subNrm[p11], _cellUv[c + 2]);
                _builder.Triangle(_subPos[p00], _subCol[p00], _subNrm[p00], _cellUv[c],
                                  _subPos[p11], _subCol[p11], _subNrm[p11], _cellUv[c + 2],
                                  _subPos[p10], _subCol[p10], _subNrm[p10], _cellUv[c + 3]);
            }
    }

    /// <summary>
    /// How many iso pixels of ground one repeat of a ground texture covers. The art is
    /// painted for a tile of <see cref="Iso.BaseTileWidth"/>: at that size, one repeat
    /// stretched across a tile puts a painted stone at a few pixels, which is what it is
    /// meant to be. Stretched instead across a 312-pixel tile, the same stone comes out
    /// bigger than a tree. So the texture repeats within the tile rather than growing with
    /// it, and the detail stays the size it was drawn at whatever the tiles are set to.
    /// </summary>
    private const float GroundRepeatPixels = Iso.BaseTileWidth;

    /// <summary>
    /// Repeat counts a tile can be divided into: a whole number of sub-cells has to fall in
    /// each repeat, or a cell would straddle the join and the texture would jump inside it.
    /// </summary>
    private static readonly int[] GroundRepeatSteps = { 1, 2, 3, Subdiv };

    /// <summary>How many times the ground texture repeats across a tile at the current tile size.</summary>
    private static int GroundRepeats
    {
        get
        {
            float want = Iso.TileWidth / GroundRepeatPixels;
            int best = GroundRepeatSteps[0];
            foreach (int step in GroundRepeatSteps)
                if (MathF.Abs(step - want) < MathF.Abs(best - want))
                    best = step;
            return best;
        }
    }

    /// <summary>
    /// Works out the texture coordinates of every sub-cell's corners for one ground layer,
    /// tiling the texture <see cref="GroundRepeats"/> times across the tile.
    ///
    /// They are per *cell* rather than per sub-vertex because the coordinates jump back to
    /// the start of the texture at every repeat, so the two cells either side of a join
    /// disagree about the vertex between them. Nothing is shared: the mesh builder writes
    /// its own vertices per triangle anyway.
    ///
    /// Each repeat is flipped by its own position hash, so a tile does not read as the same
    /// square stamped six times over. The ground art tiles seamlessly, and mirroring a
    /// seamless tile leaves its edges matching, so the repeats still join invisibly.
    /// </summary>
    private void FillGroundUv(in TextureAtlas.Region tex, int tx, int ty, int n)
    {
        int repeats = GroundRepeats, cells = n / repeats;
        for (int i = 0; i < n; i++)
        {
            float v0 = i % cells / (float)cells, v1 = v0 + 1f / cells;
            for (int j = 0; j < n; j++)
            {
                TileFlip(tx * repeats + j / cells, ty * repeats + i / cells, out bool flipU, out bool flipV);
                float u0 = j % cells / (float)cells, u1 = u0 + 1f / cells;
                int c = (i * n + j) * 4;
                _cellUv[c] = TileUv(tex, u0, v0, flipU, flipV);
                _cellUv[c + 1] = TileUv(tex, u1, v0, flipU, flipV);
                _cellUv[c + 2] = TileUv(tex, u1, v1, flipU, flipV);
                _cellUv[c + 3] = TileUv(tex, u0, v1, flipU, flipV);
            }
        }
    }

    /// <summary>Points every sub-cell corner at one texel — for a layer that carries no texture of its own.</summary>
    private void FillFlatUv(Vector2 uv, int n)
    {
        for (int c = 0; c < n * n * 4; c++)
            _cellUv[c] = uv;
    }

    /// <summary>Where in the master alpha the ground stops: the waterline itself.</summary>
    private const float ShoreCut = 0.5f;

    /// <summary>
    /// Emits the sub-grid with every cell cut along the waterline, keeping only the dry side.
    ///
    /// Fading the ground out instead is not enough on its own: a fade can be no narrower than
    /// the cell it is interpolated across, and where a bank plunges into a channel one cell
    /// covers a long stretch of screen, so the coast comes out as a smear of half-transparent
    /// ground trailing down into the water. Cutting the cell puts the edge exactly where the
    /// water is, however steep the ground is there. The cut runs through the points on the
    /// cell's own edges where the alpha passes <see cref="ShoreCut"/>, and the two tiles
    /// either side of a shared edge read the same alpha at its ends, so they cut it at the
    /// same point and the seam stays closed.
    /// </summary>
    private void EmitClippedSubGrid(int n, int span)
    {
        Span<int> corner = stackalloc int[4];
        Span<Vector2> pos = stackalloc Vector2[8];
        Span<Vector2> uv = stackalloc Vector2[8];
        Span<Vector3> nrm = stackalloc Vector3[8];
        Span<Color> col = stackalloc Color[8];

        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                int p00 = i * span + j, cell = (i * n + j) * 4;
                corner[0] = p00;            // north
                corner[1] = p00 + 1;        // east
                corner[2] = p00 + span + 1; // south
                corner[3] = p00 + span;     // west

                // Walk the cell's four corners in order, keeping the dry ones and adding the
                // point where each wet/dry edge crosses the waterline.
                int count = 0;
                for (int c = 0; c < 4; c++)
                {
                    int a = corner[c], b = corner[(c + 1) & 3];
                    float aA = _subA[a], aB = _subA[b];
                    bool dryA = aA >= ShoreCut, dryB = aB >= ShoreCut;

                    if (dryA)
                    {
                        pos[count] = _subPos[a]; uv[count] = _cellUv[cell + c];
                        nrm[count] = _subNrm[a]; col[count] = _subCol[a];
                        count++;
                    }

                    if (dryA != dryB)
                    {
                        float t = (ShoreCut - aA) / (aB - aA);
                        pos[count] = Vector2.Lerp(_subPos[a], _subPos[b], t);
                        uv[count] = Vector2.Lerp(_cellUv[cell + c], _cellUv[cell + ((c + 1) & 3)], t);
                        nrm[count] = Vector3.Normalize(Vector3.Lerp(_subNrm[a], _subNrm[b], t));
                        col[count] = Color.Lerp(_subCol[a], _subCol[b], t);
                        count++;
                    }
                }

                for (int k = 2; k < count; k++)
                    _builder.Triangle(pos[0], col[0], nrm[0], uv[0],
                                      pos[k - 1], col[k - 1], nrm[k - 1], uv[k - 1],
                                      pos[k], col[k], nrm[k], uv[k]);
            }
    }

    /// <summary>
    /// Softens <see cref="_wet"/> into <see cref="_wetField"/> with a 3×3 tent, leaving the
    /// outermost ring alone (nothing samples that far out, and it has no neighbours to
    /// average). One pass is enough: it spreads the step over about two tiles, which is
    /// what lets the waterline curve across a tile instead of turning at its corners.
    /// </summary>
    private void BlurWet()
    {
        Array.Copy(_wet, _wetField, _wet.Length);
        for (int y = 1; y < FieldStride - 1; y++)
            for (int x = 1; x < FieldStride - 1; x++)
            {
                int i = y * FieldStride + x;
                float sum = 4f * _wet[i]
                          + 2f * (_wet[i - 1] + _wet[i + 1] + _wet[i - FieldStride] + _wet[i + FieldStride])
                          + _wet[i - FieldStride - 1] + _wet[i - FieldStride + 1]
                          + _wet[i + FieldStride - 1] + _wet[i + FieldStride + 1];
                _wetField[i] = sum / 16f;
            }
    }

    /// <summary>
    /// How dry a point is, as an alpha: 1 on land, 0 out in the water, crossing over
    /// <see cref="ShoreFeather"/> at the waterline itself. Every tile along a coast cuts its
    /// ground against this one field, so the two sides of the line agree.
    ///
    /// It is sampled through a position warp, for the same reason the biome borders are (see
    /// <see cref="SubTextureIndex"/>): the field is built from whole tiles, so read straight
    /// its waterline runs along tile edges and the coast comes out as the grid's own
    /// staircase. Displacing the lookup by a smooth noise of about a tile pushes the line
    /// off the edges and lets it wander across them. The warp is a function of world
    /// position, so two tiles — or two chunks — agree on the coast they share.
    /// </summary>
    private float ShoreAlpha(int ox, int oy, float flx, float fly, float waterLevel)
    {
        float wx = ox + flx, wy = oy + fly;
        float dx = (ValueNoise(wx * ShoreWarpFreq, wy * ShoreWarpFreq) - 0.5f) * (2f * ShoreWarpAmp)
                 + (ValueNoise(wx * ShoreWarpFreq * 3.1f + 11.7f, wy * ShoreWarpFreq * 3.1f + 5.3f) - 0.5f) * ShoreWarpAmp;
        float dy = (ValueNoise(wx * ShoreWarpFreq + 53.3f, wy * ShoreWarpFreq + 17.1f) - 0.5f) * (2f * ShoreWarpAmp)
                 + (ValueNoise(wx * ShoreWarpFreq * 3.1f + 71.9f, wy * ShoreWarpFreq * 3.1f + 41.3f) - 0.5f) * ShoreWarpAmp;
        float wet = Smoothstep01((SmoothField(_wetField, flx + dx, fly + dy) - ShoreLine) / ShoreFeather);

        // ...and no ground below the surface either way. Which of the two bites first
        // depends on the bank: on a steep one the ground climbs out of the water within a
        // few pixels and this is what draws the line, keeping it crisp instead of letting
        // the wet field smear it up the slope; on a flat shore the ground barely rises and
        // the wet field decides.
        float rise = SmoothLevel(flx, fly) - waterLevel;
        float above = Smoothstep01(rise / ShoreRise);

        // Ground well clear of the surface stays whatever the wet field thinks: a field built
        // from whole tiles should not be able to flood a bank. Left unbounded it cuts the
        // ground away up the slope, and the water sheet — which climbs the bank behind the
        // ground so the cut always has water under it — is left showing, stretched up it.
        float bank = Smoothstep01((rise - ShoreBank) / ShoreRise);
        return MathF.Max(bank, MathF.Min(wet, above));
    }

    /// <summary>Does this tile sit on a shoreline — is there both water and dry land within <paramref name="reach"/> tiles of it?</summary>
    private bool IsShoreTile(int lx, int ly, int reach = 1)
    {
        bool water = false, land = false;
        for (int dy = -reach; dy <= reach; dy++)
            for (int dx = -reach; dx <= reach; dx++)
            {
                if (_wet[(ly + FieldMargin + dy) * FieldStride + (lx + FieldMargin + dx)] < 0.5f)
                    water = true;
                else
                    land = true;
            }
        return water && land;
    }

    /// <summary>
    /// The ground texture to lay under the shore on a water tile: the one most of its dry
    /// neighbours wear, so the beach continues the coast it belongs to. -1 when every
    /// neighbour is water.
    /// </summary>
    private static int ShoreGroundTexture(World world, int tx, int ty)
    {
        Span<int> tally = stackalloc int[TerrainTextureCount];
        tally.Clear();
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (!world.TryGetTile(tx + dx, ty + dy, out Tile n) || n.IsWater)
                    continue;
                tally[TerrainTextureIndex(n.Biome, tx + dx, ty + dy)]++;
            }

        int best = -1, bestCount = 0;
        for (int t = 0; t < tally.Length; t++)
            if (tally[t] > bestCount) { best = t; bestCount = tally[t]; }
        return best;
    }

    /// <summary>
    /// The elevation the water surface sits at, at one tile corner: the lowest of the water
    /// tiles meeting there, or 0 where none do. The sea is level 0 everywhere, so this comes
    /// out flat along a coast — which is the point, since reading the corner off the terrain
    /// instead dragged the water up wherever a bank happened to meet it. Taking it from the
    /// water tiles also means water that sits above sea level lies at its own level.
    /// </summary>
    private static bool TryWaterLevel(World world, int vx, int vy, out float level)
    {
        int found = int.MaxValue;
        for (int dy = -1; dy <= 0; dy++)
            for (int dx = -1; dx <= 0; dx++)
                if (world.TryGetTile(vx + dx, vy + dy, out Tile n) && n.IsWater && n.Level < found)
                    found = n.Level;

        level = found == int.MaxValue ? 0f : found;
        return found != int.MaxValue;
    }

    /// <summary>
    /// The water surface at a tile's four corners. A corner with no water at all takes the
    /// level of the ones that have it — the inland corners of a bank tile touch no water,
    /// and left at 0 they would tilt the surface down to sea level across the tile, which
    /// says the whole bank is above water and leaves the ground trailing down into it.
    /// </summary>
    private static void TileWaterLevels(World world, int tx, int ty,
                                        out float tl, out float tr, out float br, out float bl)
    {
        bool hasTL = TryWaterLevel(world, tx, ty, out tl);
        bool hasTR = TryWaterLevel(world, tx + 1, ty, out tr);
        bool hasBR = TryWaterLevel(world, tx + 1, ty + 1, out br);
        bool hasBL = TryWaterLevel(world, tx, ty + 1, out bl);

        float known = float.MaxValue;
        if (hasTL) known = MathF.Min(known, tl);
        if (hasTR) known = MathF.Min(known, tr);
        if (hasBR) known = MathF.Min(known, br);
        if (hasBL) known = MathF.Min(known, bl);
        if (known == float.MaxValue)
            return; // no water anywhere near: the levels are all 0 already

        if (!hasTL) tl = known;
        if (!hasTR) tr = known;
        if (!hasBR) br = known;
        if (!hasBL) bl = known;
    }

    /// <summary>
    /// The water texture to lay over a shore: the one the water beside this tile wears, so
    /// the sheet drawn over a bank matches the open water it runs into. Falls back to
    /// <paramref name="fallback"/> (this tile's own texture) if nothing near is water.
    /// </summary>
    private static int ShoreWaterTexture(World world, int tx, int ty, int fallback)
    {
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if (world.TryGetTile(tx + dx, ty + dy, out Tile n) && n.IsWater)
                    return TerrainTextureIndex(n.Biome, tx + dx, ty + dy);
        return fallback;
    }

    /// <summary>Is any tile within a couple of tiles wearing a different, blendable land texture (so this tile sits on a biome border)?</summary>
    private bool NearBiomeBorder(World world, int tx, int ty, int baseTex)
    {
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                if (world.TryGetTile(tx + dx, ty + dy, out Tile n))
                {
                    int t = TerrainTextureIndex(n.Biome, tx + dx, ty + dy);
                    if (t != baseTex && CanBlend(baseTex, t)) return true;
                }
            }
        return false;
    }

    /// <summary>
    /// A land tile on a biome border, and/or one that meets the water. It is subdivided fine,
    /// each sub-vertex softly classified to the biome textures around it, and (on a shore
    /// tile) faded out across the waterline. The tile's own texture is laid down opaque,
    /// then each neighbouring texture over it by its soft membership — so borders are soft
    /// contours, even where they run straight along a tile edge — the whole thing cut at the
    /// waterline where there is one.
    /// </summary>
    private void EmitLandTile(World world, int ox, int oy, int lx, int ly, int tx, int ty, int baseTex, bool shore)
    {
        int n = shore ? CoastSubdiv : BlendSubdiv, span = n + 1;
        FillSubGrid(world, ox, oy, lx, ly, tx, ty, n, span, shore);
        int overlays = ComputeMembership(ox, oy, lx, ly, n, span, baseTex);
        EmitLandLayers(baseTex, overlays, tx, ty, n, span, shore);
    }

    /// <summary>
    /// The ground on a *water* tile: the bank does not stop at the water's edge, it carries
    /// on under it, and near a shore part of it is still dry. Laid in the texture the coast
    /// beside this tile wears, with the water drawn over it afterwards.
    ///
    /// Without it a water tile paints its whole diamond and the coastline is the tile grid —
    /// the sawtooth of diamond edges the shore used to be.
    /// </summary>
    private void EmitShoreGround(World world, int ox, int oy, int lx, int ly, int tx, int ty)
    {
        int baseTex = ShoreGroundTexture(world, tx, ty);
        if (baseTex < 0)
            return;

        const int n = CoastSubdiv, span = n + 1;
        FillSubGrid(world, ox, oy, lx, ly, tx, ty, n, span, shore: true);
        int overlays = ComputeMembership(ox, oy, lx, ly, n, span, baseTex);
        EmitLandLayers(baseTex, overlays, tx, ty, n, span, shore: true);
    }

    /// <summary>
    /// The water under a shore tile: an opaque sheet across the whole tile, lying level with
    /// the open water it joins (see <see cref="WaterLevel"/>). The ground is drawn over it
    /// and cut back at the waterline, so what is left showing is the water — and since the
    /// tiles on both sides of a coast lay the same sheet and cut against the same field, the
    /// coastline is the waterline and not the edge of anybody's diamond.
    ///
    /// It is subdivided rather than a single quad so that it can climb behind the bank and
    /// so that its corners can follow water that is not at sea level; on the sea itself
    /// every vertex of it is at level 0.
    /// </summary>
    private void EmitWaterSheet(World world, int lx, int ly, int tx, int ty, int waterTex)
    {
        const int n = CoastSubdiv, span = n + 1;
        TileWaterLevels(world, tx, ty, out float lTL, out float lTR, out float lBR, out float lBL);

        for (int i = 0; i <= n; i++)
        {
            float v = i / (float)n;
            for (int j = 0; j <= n; j++)
            {
                float u = j / (float)n;
                int k = i * span + j;

                // Flat at the water's own level over the channel, but climbing with the
                // ground once the ground is the higher of the two. The climbing half is
                // never seen — the ground over it is opaque there — and it is what keeps
                // the sheet covering every pixel the shore cut takes out of the bank. A
                // sheet that stayed flat would sit whole levels down the screen from the
                // bank it is meant to be behind, and the cut would show the sky through it.
                float water = MathHelper.Lerp(MathHelper.Lerp(lTL, lTR, u), MathHelper.Lerp(lBL, lBR, u), v);
                float level = MathF.Max(water, SmoothLevel(lx + u, ly + v));
                _subPos[k] = Iso.TileToIso(tx + u, ty + v, level);
                _subNrm[k] = Vector3.Up; // water lies flat however the bed under it rolls
            }
        }

        // Deep and shallow blend into one another over the sheet exactly as they do over the
        // open water beyond it, or the coast would wear a ring of a different blue.
        int overlays = GatherBlend(world, tx, ty, waterTex);
        WaterLayer(waterTex, -1);
        for (int o = 0; o < overlays; o++)
            WaterLayer(_blendTex[o], o);

        void WaterLayer(int tex, int overlay)
        {
            for (int i = 0; i <= n; i++)
            {
                float v = i / (float)n;
                for (int j = 0; j <= n; j++)
                {
                    float u = j / (float)n;
                    int k = i * span + j;
                    _subCol[k] = overlay < 0
                        ? TerrainWhite
                        : BlendColor(_blendW[overlay * 4], _blendW[overlay * 4 + 1],
                                     _blendW[overlay * 4 + 2], _blendW[overlay * 4 + 3], u, v, tx, ty);
                }
            }
            FillGroundUv(_atlas.Terrain[tex], tx, ty, n);
            EmitSubGrid(n, span);
        }
    }

    /// <summary>A smoothly interpolated value from one of the cached fields, at a chunk-local fractional position (bicubic Catmull-Rom).</summary>
    private float SmoothField(float[] field, float fx, float fy)
    {
        int ix = (int)MathF.Floor(fx), iy = (int)MathF.Floor(fy);
        float u = fx - ix, v = fy - iy;
        float r0 = CatmullRom(Cf(field, ix - 1, iy - 1), Cf(field, ix, iy - 1), Cf(field, ix + 1, iy - 1), Cf(field, ix + 2, iy - 1), u);
        float r1 = CatmullRom(Cf(field, ix - 1, iy), Cf(field, ix, iy), Cf(field, ix + 1, iy), Cf(field, ix + 2, iy), u);
        float r2 = CatmullRom(Cf(field, ix - 1, iy + 1), Cf(field, ix, iy + 1), Cf(field, ix + 1, iy + 1), Cf(field, ix + 2, iy + 1), u);
        float r3 = CatmullRom(Cf(field, ix - 1, iy + 2), Cf(field, ix, iy + 2), Cf(field, ix + 1, iy + 2), Cf(field, ix + 2, iy + 2), u);
        return CatmullRom(r0, r1, r2, r3, v);
    }

    /// <summary>Reads a cached field by chunk-local tile coordinate, clamped to the margin.</summary>
    private static float Cf(float[] field, int localX, int localY)
    {
        int fx = Math.Clamp(localX + FieldMargin, 0, FieldStride - 1);
        int fy = Math.Clamp(localY + FieldMargin, 0, FieldStride - 1);
        return field[fy * FieldStride + fx];
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

    /// <summary>How many ground textures the terrain sheet holds.</summary>
    private const int TerrainTextureCount = 12;

    /// <summary>Which of the 12 ground textures a biome wears. Indices are the terrain sheet's row-major order.</summary>
    private static int TerrainTextureIndex(Biome biome, int tx, int ty) => BiomeTextureIndex[(int)biome];

    /// <summary>Biome → terrain-sheet tile. See the sheet: 0 grass · 1 dirt · 2 snow · 3 dry grass · 4 sand · 5 frost-dirt · 6 rock · 7 orange flowers · 8 deep water · 9 shallow water · 10 swamp · 11 flower meadow.</summary>
    private static readonly int[] BiomeTextureIndex =
    {
        8,  // Ocean
        9,  // ShallowWater
        9,  // Lake → shallow water: a lake is never the deep blue of open sea
        4,  // Beach → sand
        0,  // Grassland → grass
        3,  // Savanna → dry grass
        4,  // Desert → sand
        0,  // TemperateForest → grass
        1,  // Taiga → dirt
        5,  // Tundra → frost-dirt: frozen ground, pale with frost even above freezing
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
    private float SmoothLevel(float fx, float fy) => SmoothField(_levelField, fx, fy);

    /// <summary>The surface normal of the smoothed height field, from a central difference of <see cref="SmoothLevel"/>.</summary>
    private Vector3 SmoothNormal(float fx, float fy)
    {
        const float e = 0.5f;
        float slopeX = SmoothLevel(fx + e, fy) - SmoothLevel(fx - e, fy);
        float slopeY = SmoothLevel(fx, fy + e) - SmoothLevel(fx, fy - e);
        return Vector3.Normalize(new Vector3(-slopeX * 2f * NormalRelief, 2f, -slopeY * 2f * NormalRelief));
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
    /// Fills the sub-vertex grid — position, normal and master alpha — for a patch of ground.
    /// On a shore tile the alpha falls to 0 across the waterline, so the ground is cut there
    /// and the water sheet already laid down shows through; everywhere else it is 1.
    /// </summary>
    private void FillSubGrid(World world, int ox, int oy, int lx, int ly, int tx, int ty, int n, int span, bool shore)
    {
        float lTL = 0f, lTR = 0f, lBR = 0f, lBL = 0f;
        if (shore)
            TileWaterLevels(world, tx, ty, out lTL, out lTR, out lBR, out lBL);

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
                _subA[k] = shore
                    ? ShoreAlpha(ox, oy, flx, fly,
                                 MathHelper.Lerp(MathHelper.Lerp(lTL, lTR, u), MathHelper.Lerp(lBL, lBR, u), v))
                    : 1f;
            }
        }

    }

    /// <summary>
    /// Classifies each sub-vertex against the biome textures, but softly: it samples the
    /// classifier at a small ring of points and takes the fraction landing on each texture.
    /// So a texture's membership fades across the border instead of switching at a point,
    /// and a texture from just across a tile edge still bleeds into this tile. Fills
    /// <see cref="_blendTex"/> with the neighbouring textures found and <see cref="_subMem"/>
    /// with each one's per-sub-vertex membership; returns how many there are.
    /// </summary>
    private int ComputeMembership(int ox, int oy, int lx, int ly, int n, int span, int baseTex)
    {
        int verts = span * span;
        for (int i = 0; i <= n; i++)
            for (int j = 0; j <= n; j++)
            {
                float flx = lx + j / (float)n, fly = ly + i / (float)n;
                int k = i * span + j;
                for (int s = 0; s < BlendSamples; s++)
                    _sampleTex[k * BlendSamples + s] =
                        SubTextureIndex(ox, oy, flx + BlendOffsets[s].dx, fly + BlendOffsets[s].dy);
            }

        // The set of neighbouring land textures that turned up in any sample.
        int count = 0;
        for (int k = 0; k < verts && count < MaxBlendOverlays; k++)
            for (int s = 0; s < BlendSamples; s++)
            {
                int t = _sampleTex[k * BlendSamples + s];
                if (t == baseTex || !CanBlend(baseTex, t)) continue;
                bool seen = false;
                for (int q = 0; q < count; q++) if (_blendTex[q] == t) { seen = true; break; }
                if (!seen && count < MaxBlendOverlays) _blendTex[count++] = t;
            }

        for (int k = 0; k < verts; k++)
            for (int o = 0; o < count; o++)
            {
                int hits = 0;
                for (int s = 0; s < BlendSamples; s++)
                    if (_sampleTex[k * BlendSamples + s] == _blendTex[o]) hits++;
                _subMem[k * MaxBlendOverlays + o] = hits / (float)BlendSamples;
            }
        return count;
    }

    /// <summary>
    /// Paints the sub-grid: the tile's own texture opaque as a base, then each neighbouring
    /// texture over it by its per-sub-vertex membership. The whole thing is scaled by the
    /// master alpha, so a shore tile's blended land still fades out at the waterline.
    /// </summary>
    private void EmitLandLayers(int baseTex, int overlays, int tx, int ty, int n, int span, bool shore)
    {
        EmitTexLayer(n, span, _atlas.Terrain[baseTex], tx, ty, overlayIndex: -1, shore);
        for (int o = 0; o < overlays; o++)
            EmitTexLayer(n, span, _atlas.Terrain[_blendTex[o]], tx, ty, overlayIndex: o, shore);
    }

    /// <summary>
    /// Emits one texture layer over the sub-grid: fully opaque for the base (overlayIndex
    /// &lt; 0), or at each sub-vertex's membership for an overlay. On a shore tile the layer
    /// is cut at the waterline instead of faded out across it.
    /// </summary>
    private void EmitTexLayer(int n, int span, TextureAtlas.Region tex, int tx, int ty, int overlayIndex, bool shore)
    {
        for (int i = 0; i <= n; i++)
            for (int j = 0; j <= n; j++)
            {
                int k = i * span + j;
                float a = overlayIndex < 0 ? 1f : _subMem[k * MaxBlendOverlays + overlayIndex];
                float macro = GroundMacro(tx + j / (float)n, ty + i / (float)n);
                _subCol[k] = ScaleRgb(TerrainWhite * (shore ? a : a * _subA[k]), macro);
            }

        FillGroundUv(tex, tx, ty, n);

        if (shore)
            EmitClippedSubGrid(n, span);
        else
            EmitSubGrid(n, span);
    }

    /// <summary>
    /// Dusts snow over a land tile that has just been drawn, reusing its sub-grid. Snow
    /// coverage is climate-driven (see <see cref="SnowCover"/>) and drifted by noise, drawn
    /// as a lit white layer over whatever biome is underneath. On a shore tile the water
    /// goes down after it, so none of it is left lying on the water. Skips the draw
    /// entirely when nothing is cold enough.
    /// </summary>
    private void EmitSnow(int ox, int oy, int lx, int ly, int n, int span, bool shore)
    {
        bool any = false;
        for (int i = 0; i <= n; i++)
            for (int j = 0; j <= n; j++)
            {
                int k = i * span + j;
                float flx = lx + j / (float)n, fly = ly + i / (float)n;
                float s = SnowCover(ox, oy, flx, fly) * (shore ? 1f : _subA[k]);
                if (s <= 0.004f) { _subCol[k] = Color.Transparent; continue; }
                any = true;

                // Break the flat white with a fine brightness grain (wind-packed drifts,
                // little shadows) so a snowfield reads as snow and not as fog.
                float grain = 0.80f + 0.20f * ValueNoise((ox + flx) * 0.9f, (oy + fly) * 0.9f);
                float a = s * 255f;
                _subCol[k] = new Color((byte)(SnowColor.R * grain * s),
                                       (byte)(SnowColor.G * grain * s),
                                       (byte)(SnowColor.B * grain * s),
                                       (byte)a);
            }

        if (!any)
            return;

        FillFlatUv(_atlas.White.CenterUv, n); // the snow layer is a colour, not a texture
        if (shore)
            EmitClippedSubGrid(n, span);
        else
            EmitSubGrid(n, span);
    }

    /// <summary>
    /// How thickly snow lies at a world point: none until it is cold enough and there is
    /// moisture to fall, then more the colder it gets, broken into drifts by a fractal
    /// threshold so it is patchy at the edge of its range and solid deep in the cold.
    /// </summary>
    private float SnowCover(int ox, int oy, float flx, float fly)
    {
        float celsius = WorldConstants.Celsius(SampleField(_tempField, flx, fly));
        float cold = Math.Clamp((SnowFirstCelsius - celsius) / (SnowFirstCelsius - SnowSolidCelsius), 0f, 1f);
        if (cold <= 0f) return 0f;
        float wet = Smoothstep01((SampleField(_moistField, flx, fly) - SnowMoistMin) / (SnowMoistFull - SnowMoistMin));
        float amount = cold * wet;
        if (amount <= 0f) return 0f;

        // A drift forms only where the fractal threshold dips below how much snow the climate
        // wants here, so cover grows from scattered patches (marginal cold) to near-solid
        // (deep cold) with bare ground always breaking up anything but the coldest places.
        float nz = SnowNoise(ox + flx, oy + fly);
        return 0.92f * Smoothstep01((amount - nz) / SnowPatchSoft);
    }

    /// <summary>Fractal value noise for the snow drifts (a few tiles across), in 0..1.</summary>
    private static float SnowNoise(float x, float y)
    {
        float sum = 0f, amp = 0.5f, freq = 0.22f, norm = 0f;
        for (int o = 0; o < 3; o++)
        {
            sum += amp * ValueNoise(x * freq, y * freq);
            norm += amp;
            amp *= 0.5f;
            freq *= 2.3f;
        }
        // A little contrast pushes the field toward its extremes, so drifts have decisive
        // edges and clear bare ground between them rather than a foggy wash.
        return Smoothstep01((sum / norm - 0.5f) * 1.5f + 0.5f);
    }

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

                // An edge is drawn only if one of the two tiles it divides is in the
                // world, or the grid would go on being ruled across the black.
                WorldBounds bounds = world.Bounds;

                if (lx < Chunk.Size && !(ly == Chunk.Size && southLoaded)
                    && (bounds.Contains(vx, vy - 1) || bounds.Contains(vx, vy)))
                {
                    _builder.Line(p, CornerPos(world, vx + 1, vy), GridLineWidth, GridColor, uv);
                }

                if (ly < Chunk.Size && !(lx == Chunk.Size && eastLoaded)
                    && (bounds.Contains(vx - 1, vy) || bounds.Contains(vx, vy)))
                {
                    _builder.Line(p, CornerPos(world, vx, vy + 1), GridLineWidth, GridColor, uv);
                }
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
    /// meeting there, with ties going to water so a narrow channel keeps its width.
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
        // Only the sea breaks on its shore. A lake has no swell behind it to break with,
        // so its edge is left as still water meeting the bank.
        if (!tile.IsSea)
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
    ///
    /// The biome decides how many trees; it does not decide which. That is the wood the
    /// tile stands in (see <see cref="Forests"/>) — so one temperate forest comes up
    /// evergreen and the next one over comes up broadleaf, off the same case below.
    /// </summary>
    private void AppendScatter(World world, in Tile tile, Vector2 a, Vector2 b, Vector2 c, Vector2 d, int tx, int ty,
                               int ox, int oy, int lx, int ly, bool shore)
    {
        // Seeded from the tile's canonical position, not its raw one, so a wood looks the
        // same tree for tree on every lap round the world (see WorldBounds.Canonical).
        world.Bounds.Canonical(tx, ty, out int hx, out int hy);
        var rng = new XorShift((uint)((hx * 73856093) ^ (hy * 19349663) ^ 0x9e37));
        float dens = tile.VegetationDensity;
        float area = Iso.TileAreaScale;

        // The wood this tile stands in: which tree owns it, and which painted form of that
        // tree it is grown from. Held as locals because the scatter's inner functions read
        // them, and an `in` parameter cannot be captured.
        ForestKind forest = tile.Forest;
        float forestVariant = tile.ForestVariant;
        bool couldSnow = tile.TemperatureCelsius < SnowGateCelsius;
        float waterLevel = 0f;
        if (shore)
        {
            TileWaterLevels(world, tx, ty, out float wTL, out float wTR, out float wBR, out float wBL);
            waterLevel = 0.25f * (wTL + wTR + wBR + wBL);
        }

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
                float s = MinVegScale + (MaxVegScale - MinVegScale) * rng.Next();
                bool dead = rng.Next() < deadChance;

                // Nothing grows below the waterline: on a shore tile the ground this plant
                // would stand on may have been cut away, and it would be left in the water.
                if (shore && ShoreAlpha(ox, oy, lx + u, ly + v, waterLevel) < 0.6f)
                    continue;

                // Snow lies on the plant to match the ground it stands on. Each plant also
                // rolls its own dusting, so a stand comes out patchy — some trees laden,
                // some bare — rather than all frosted to the same degree.
                float snow = 0f;
                if (couldSnow)
                    snow = Smoothstep01(SnowCover(ox, oy, lx + u, ly + v) * (0.7f + 0.6f * rng.Next()) * SnowCanopy);

                _veg.Add(new VegItem(Bilerp(a, b, c, d, u, v), kind, s, dead, rng.NextBits(), snow,
                                     IsTree(kind) ? forestVariant : NoVariant));
            }
        }

        // A wood is not an even mix of what will grow in it: one tree has the canopy and
        // a second grows through it in smaller numbers. Splitting the tile's whole tree
        // budget between the two by share is what holds that proportion however thickly
        // the tile happens to be planted.
        void AddTrees(float rate, float deadChance)
        {
            Forests.Mix(forest, out Vegetation primary, out Vegetation secondary, out float share);
            Add(primary, rate * share, deadChance);
            Add(secondary, rate * (1f - share), deadChance);
        }

        // The dead-chance argument is how many of a kind come up as bare wood. Standing
        // deadwood is normal in a real wood; it is the rule on tundra, where nothing
        // holds a leaf, and common in swamp and savanna where trees drown or bake.
        switch (tile.Biome)
        {
            case Biome.TemperateForest:
                AddTrees(5 + Ri(dens * 6), deadChance: 0.07f);
                Add(Vegetation.Bush, Ri(dens * 2));
                Add(Vegetation.Rock, RockRateForest);
                break;
            case Biome.Taiga:
                AddTrees(5 + Ri(dens * 6), deadChance: 0.10f);
                // Undergrowth is sparse in the cold: only the odd frost-bitten shrub.
                if (rng.Next() > 0.9f) Add(Vegetation.Bush, 1, deadChance: 0.5f);
                Add(Vegetation.Rock, RockRateForest);
                break;
            case Biome.Jungle:
                AddTrees(6 + Ri(dens * 6), deadChance: 0.04f);
                Add(Vegetation.Bush, 1 + Ri(dens * 2));
                break;
            case Biome.Grassland:
                // A lone tree here and there across open grass, thickening into light
                // woodland where the clumping field peaks. This used to be a hard
                // switch at dens 0.5 — half the grassland bare, half of it a forest.
                AddTrees(0.05f + 0.7f * MathF.Max(0f, dens - 0.5f), deadChance: 0.09f);
                if (dens > 0.35f && rng.Next() > 0.5f) Add(Vegetation.Bush, 1);
                Add(Vegetation.Rock, RockRateGrassland);
                break;
            case Biome.Savanna:
                if (dens > 0.55f) AddTrees(dens > 0.8f ? 2 : 1, deadChance: 0.18f);
                break;
            case Biome.Swamp:
                Add(Vegetation.Bush, 1 + Ri(dens * 3), deadChance: 0.25f);
                if (dens > 0.6f) AddTrees(1, deadChance: 0.30f);
                break;
            case Biome.Tundra:
                // Nothing up here keeps a leaf: tundra scrub is bare twigs, always —
                // and thin on the ground, only the rare hardy shrub in the frost. What it
                // does have is stone — frost-heaved rock breaking through the thin soil.
                if (rng.Next() > 0.92f) Add(Vegetation.Bush, 1, deadChance: 1f);
                Add(Vegetation.Rock, RockRateTundra);
                break;
            // Desert stays bare stone-wise: shifting sand carries no scattered rock.
            case Biome.Desert: if (dens > 0.7f) Add(Vegetation.Cactus, dens > 0.88f ? 2 : 1); break;
            // The mountains are made of the stuff: rock thickly over the bare peaks.
            case Biome.Rock: Add(Vegetation.Rock, RockRateMountain); break;
        }
    }

    /// <summary>How dark a plant's ground shadow is, premultiplied over the terrain.</summary>
    private static readonly Color ShadowTint = new Color(6, 10, 8) * 0.30f;

    /// <summary>
    /// A plant's shadow, pooled on the ground at its foot: a row of soft discs squashed
    /// flat to lie in the ground plane, from a broad one under the plant to a small one at
    /// the tip. Their size tracks the plant, so a tall tree throws a wide pool and a shrub
    /// only a small one; grass is too slight to shadow at all.
    /// </summary>
    private void AppendShadow(in VegItem item)
    {
        float radius = ShadowRadius(item);
        if (radius <= 0f)
            return;

        // All of them are squashed to the ground plane and laid at the plant's own foot.
        // Which way they then fan out, and how far, is the sun's business, applied as a
        // shear at draw time (see <see cref="SunShear"/>) off the lean each one carries in
        // its z — so the whole thing swings round and lengthens as the day passes without
        // the mesh being rebuilt.
        float lean = ShadowLean(item, radius);
        for (int i = 0; i < ShadowSteps; i++)
        {
            float r = radius * ShadowTaper[i];
            var size = new Vector2(r * 2f, r * 2f * ShadowSquash);
            _builder.ShadowSprite(item.Pos, size, Center, _atlas.SoftDisc,
                                  ShadowTint * ShadowFade[i], lean * ShadowStep[i]);
        }
    }

    /// <summary>
    /// A shadow is not one disc but a short row of them, laid from the plant's foot out
    /// along the way the light is going. The near one never leaves the foot; the rest carry
    /// more and more lean in their z, so a low sun draws the row out into a long tapering
    /// smear while a high one packs it back into a single pool. It is the only way this
    /// mesh can stretch: the shear that moves shadows can translate a quad, not scale one,
    /// so the stretch has to come from geometry that was always several pieces long.
    /// </summary>
    private const int ShadowSteps = 4;

    /// <summary>How far along the lean each disc sits, nearest to furthest.</summary>
    private static readonly float[] ShadowStep = { 0.06f, 0.38f, 0.70f, 1f };

    /// <summary>Each disc's radius as a fraction of the plant's, tapering to the tip.</summary>
    private static readonly float[] ShadowTaper = { 1f, 0.90f, 0.76f, 0.58f };

    /// <summary>
    /// Each disc's share of the shadow's darkness. They are faint on their own and stack up
    /// where they overlap, which is what gives a dark core at the foot easing away down the
    /// tail — and, once the sun is low enough to pull them apart, a shadow that thins out
    /// as it lengthens, the way a real one does.
    /// </summary>
    private static readonly float[] ShadowFade = { 0.50f, 0.42f, 0.30f, 0.18f };

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
    /// The unit a plant's shadow is drawn out in, in iso pixels: its own radius, tuned by
    /// how top-heavy the caster is. A squat rock's shadow barely leaves its foot; a cactus
    /// is all height over a small footprint, so its lean is the most pronounced.
    ///
    /// It is deliberately measured against the shadow rather than the plant's height. Off
    /// height alone a tree wrote thirty-odd pixels here, and once <see cref="ShadowReachMax"/>
    /// had multiplied that the whole pool sat a clear tile away from the trunk — a detached
    /// smudge on the grass rather than shade. Against its own radius the row can only ever
    /// grow out of the plant it belongs to.
    /// </summary>
    private static float ShadowLean(in VegItem item, float radius) => radius * (item.Veg switch
    {
        Vegetation.Cactus => MaxLeanFactor,
        Vegetation.Rock => 0.45f,
        Vegetation.Bush => 0.70f,
        _ => 1f, // trees
    });

    /// <summary>The largest <see cref="ShadowLean"/> multiplier any plant uses.</summary>
    private const float MaxLeanFactor = 1.30f;

    /// <summary>
    /// How far a shadow's tip leans at most, as a multiple of <see cref="ShadowLean"/>, and
    /// how little it leans under a high sun. The long end is clamped far below what the real
    /// geometry gives — at a few degrees of altitude 1/tan runs away to ten times the
    /// caster's height, and at that length the row of discs pulls apart into a dotted line.
    /// </summary>
    private const float ShadowReachMax = 2.6f;
    private const float ShadowReachMin = 0.12f;

    /// <summary>Bounds padding for the furthest a shadow's tip can be swung from its plant.</summary>
    private const float MaxShadowReach = TreeHeight * 0.30f * MaxVegScale * MaxLeanFactor * ShadowReachMax;

    /// <summary>
    /// How strongly shadows are laid down right now: full once the sun is properly up,
    /// fading to nothing as it touches the horizon and gone entirely while it is below.
    /// Without a sun there is nothing to cast a shadow, and a pool of darkness left under
    /// every tree at midnight reads as grime on the grass.
    /// </summary>
    private static float ShadowStrength(SkyLight sky) => sky.Sunlight;

    /// <summary>The size jitter a scattered plant can roll, smallest to largest.</summary>
    private const float MinVegScale = 0.8f;
    private const float MaxVegScale = 1.3f;

    /// <summary>
    /// How many scattered rocks a base-sized tile grows, per biome — a rate, settled with a
    /// coin toss below one whole rock so it holds at any tile size. The mountains are thick
    /// with stone; grassland shows only the occasional weathered boulder; the desert none at
    /// all (its sand carries nothing loose).
    /// </summary>
    private const float RockRateMountain = 2.5f;   // very high — the peaks are bare rock
    private const float RockRateForest = 0.10f;    // low — the odd mossy stone among the trees
    private const float RockRateTundra = 0.14f;    // low — frost-heaved rock through thin soil
    private const float RockRateGrassland = 0.03f; // very low — a lone boulder in open country

    /// <summary>
    /// A shear that turns each shadow vertex's z (how far its plant's pool may lean) into
    /// an offset across the ground, away from the sun.
    ///
    /// Everything else in the mesh sits at z = 0 and so passes through untouched, which is
    /// what lets one matrix move every shadow on the map and nothing else. It also flattens
    /// z, keeping the result inside the orthographic projection's 0..1 depth range.
    /// </summary>
    private static Matrix SunShear(SkyLight sky)
    {
        // Where the light is heading across the ground, in world axes (x = east, z = south).
        var ground = new Vector2(sky.LightDirection.X, sky.LightDirection.Z);
        if (ground.LengthSquared() < 1e-6f)
            ground = new Vector2(0f, 1f);
        ground.Normalize();

        // Into iso pixels, as a unit step that is then squashed the same way the shadow
        // disc itself is. That makes the path the shadow's centre walks the very ellipse
        // the disc is drawn as, so a lean of one radius puts the pool's near edge exactly
        // on the plant's foot whichever way round the sun is — the shadow changes sides
        // without ever letting go.
        var direction = new Vector2(ground.X - ground.Y, ground.X + ground.Y);
        direction.Normalize();
        direction.Y *= ShadowSquash;

        // Longest at dawn and dusk, shortest at noon — 1/tan of the sun's altitude, held to
        // the range above. Below the horizon it is pinned to the short end: there is no sun
        // to cast anything, and the night is dark enough that what is left reads as the
        // plant's own footprint.
        float altitude = MathF.Max(sky.Altitude, 0.02f);
        float reach = Math.Clamp(1f / MathF.Tan(altitude), ShadowReachMin, ShadowReachMax);

        Matrix shear = Matrix.Identity;
        shear.M31 = direction.X * reach;
        shear.M32 = direction.Y * reach;
        shear.M33 = 0f;
        return shear;
    }

    // ---- mobs -----------------------------------------------------------------

    /// <summary>
    /// How tall a sheep stands, in iso pixels — a little under a bush, which is about
    /// right for something that shelters under one. Like every other sprite height here
    /// this is absolute and does not follow the tile size: a bigger tile is more ground,
    /// not a closer camera, so the sheep on it stay the size they were and simply have
    /// more room (see <see cref="Iso"/>).
    /// </summary>
    private const float SheepHeight = 4.5f;

    /// <summary>Iso pixels a walking mob rises and falls by over its stride.</summary>
    private const float MobBob = 0.4f;

    /// <summary>A mob's shadow pool, as a fraction of its height, and how far that pool leans.</summary>
    private const float MobShadowRadius = 0.34f;
    private const float MobShadowLean = 0.70f;

    /// <summary>The pool of light under the selected mob: how wide, as a fraction of its height, and how bright.</summary>
    private const float SelectionRadius = 0.85f;
    private static readonly Color SelectionGlow = new Color(190, 235, 255) * 0.55f;

    /// <summary>
    /// Iso pixels beyond the screen a mob's feet may be before it is dropped. Its art
    /// stands up the screen from its feet and its shadow reaches away from them, so the
    /// margin has to cover both or a sheep pops out at the edge of the view.
    /// </summary>
    private const float MobCullMargin = 96f;

    /// <summary>One mob worked out for this frame: what to draw, and where the ground put it.</summary>
    private readonly struct MobDraw
    {
        public readonly Mob Mob;

        /// <summary>Iso-space position of its feet, on the drawn surface of the ground.</summary>
        public readonly Vector2 Foot;

        public readonly TextureAtlas.Region Art;
        public readonly float Height;

        /// <summary>The chunk it is standing on — whose plants it has to be drawn in among.</summary>
        public readonly ChunkCoord Chunk;

        public MobDraw(Mob mob, Vector2 foot, TextureAtlas.Region art, float height, ChunkCoord chunk)
        {
            Mob = mob;
            Foot = foot;
            Art = art;
            Height = height;
            Chunk = chunk;
        }
    }

    /// <summary>
    /// Where each visible mob's quads sit in <see cref="_mobSprites"/>, in step with
    /// <see cref="_visibleMobs"/>. Kept beside the list rather than in it because the ranges
    /// are only known once the geometry has been appended, after the mobs are sorted.
    /// </summary>
    private readonly List<(int Start, int Count)> _mobSpriteRanges = new();

    /// <summary>
    /// Gathers every mob that is on screen and turns it into quads for this frame: the
    /// shadows as one layer, then the mobs themselves on top, the same order and for the
    /// same reason as the plants — a pool of shade belongs on the grass, not on the animal
    /// standing in front of it.
    /// </summary>
    private void BuildMobGeometry(World world, Vector2 isoMin, Vector2 isoMax)
    {
        _mobSprites.Clear();
        _mobShadows.Clear();
        _visibleMobs.Clear();

        // The data views are about the ground itself. Plants are left off them; so is
        // anything walking about on it.
        if (world.MapView != MapView.Biome)
            return;

        foreach (Mob mob in world.Mobs)
        {
            // Ground that has streamed out is ground we can't put feet on: it would read
            // as level 0 and the mob would hang in the air over nothing. Ground past a
            // pole is not drawn at all, so nothing standing on it is either.
            if (!world.TryGetTile((int)MathF.Floor(mob.Position.X), (int)MathF.Floor(mob.Position.Y), out Tile under)
                || under.IsVoid)
            {
                continue;
            }

            Vector2 foot = Iso.TileToIso(mob.Position.X, mob.Position.Y,
                                         SurfaceLevel(world, mob.Position.X, mob.Position.Y));

            if (foot.X < isoMin.X - MobCullMargin || foot.X > isoMax.X + MobCullMargin ||
                foot.Y < isoMin.Y - MobCullMargin || foot.Y > isoMax.Y + MobCullMargin)
                continue;

            (TextureAtlas.Region art, float height) = MobArt(mob.Kind);
            var chunk = new ChunkCoord(FloorDiv((int)MathF.Floor(mob.Position.X), Chunk.Size),
                                       FloorDiv((int)MathF.Floor(mob.Position.Y), Chunk.Size));
            _visibleMobs.Add(new MobDraw(mob, foot, art, height * mob.Scale, chunk));
        }

        // Further up the screen first, so a mob in front covers the one behind it — and so
        // that the mobs of any one chunk come out in the order its plants were sorted into.
        _visibleMobs.Sort(static (p, q) => p.Foot.Y.CompareTo(q.Foot.Y));

        foreach (MobDraw draw in _visibleMobs)
            AppendMobShadow(draw);

        // Each mob's quads are appended on their own so its range can be recorded, which is
        // what lets it be drawn at its own depth among a chunk's plants.
        _mobSpriteRanges.Clear();
        foreach (MobDraw draw in _visibleMobs)
        {
            int start = _mobSprites.IndexCount;
            AppendMob(draw, world.SelectedMob == draw.Mob);
            _mobSpriteRanges.Add((start, _mobSprites.IndexCount - start));
        }
    }

    /// <summary>
    /// Draws one chunk's plants with its mobs standing among them: the plants up to the
    /// first mob's feet, that mob, the plants up to the next, and so on.
    ///
    /// Both are sorted by the same screen y, so this is a merge of two ordered lists — and
    /// it is what stops a sheep walking over the front of a tree it is standing behind.
    /// A chunk with no mobs on it draws in exactly one call, as it always did.
    /// </summary>
    private void DrawVegetationWithMobs(GraphicsDevice gd, ChunkCoord coord)
    {
        ChunkMesh veg = _vegMeshes[coord];
        ChunkMesh.Cursor cursor = ChunkMesh.Cursor.Zero;

        for (int i = 0; i < _visibleMobs.Count; i++)
        {
            MobDraw draw = _visibleMobs[i];
            if (!draw.Chunk.Equals(coord))
                continue;

            ChunkMesh.Cursor upTo = veg.CursorAt(draw.Foot.Y);
            veg.DrawRange(gd, cursor, upTo);
            cursor = upTo;

            (int start, int count) = _mobSpriteRanges[i];
            _mobSprites.Draw(gd, start, count);
            _mobDrawn[i] = true;
        }

        veg.DrawRange(gd, cursor, veg.End);
    }

    /// <summary>Which mobs have already been drawn this frame, in step with <see cref="_visibleMobs"/>.</summary>
    private readonly List<bool> _mobDrawn = new();

    /// <summary>The view the last frame was drawn with, which is what a click is read against.</summary>
    private Vector2 _viewCenter, _viewFocus;
    private float _viewZoom = 1f;

    /// <summary>
    /// The least a mob may measure on screen for the purpose of being clicked on, in screen
    /// pixels. A sheep is a dozen pixels tall at most and a lamb far less; asking anyone to
    /// hit that exactly is asking them to miss, so the pick box is grown to something a
    /// finger or a mouse can actually land on.
    /// </summary>
    private const float MinPickSize = 22f;

    /// <summary>
    /// The mob under a screen point, or null. Frontmost first — the same order they are
    /// drawn in, reversed — so clicking a huddle picks the one on top of it.
    ///
    /// It reads the positions worked out for the last frame drawn, which is what the person
    /// clicking was looking at.
    /// </summary>
    public Mob? PickMob(Vector2 screenPos)
    {
        Vector2 iso = (screenPos - _viewCenter) / _viewZoom + _viewFocus;

        // A pick box that is never smaller than MinPickSize on screen, in iso units.
        float minHalf = MinPickSize * 0.5f / _viewZoom;

        for (int i = _visibleMobs.Count - 1; i >= 0; i--)
        {
            MobDraw draw = _visibleMobs[i];
            float halfWidth = MathF.Max(draw.Height * draw.Art.Aspect * 0.5f, minHalf);
            float height = MathF.Max(draw.Height, minHalf * 2f);

            if (iso.X >= draw.Foot.X - halfWidth && iso.X <= draw.Foot.X + halfWidth &&
                iso.Y >= draw.Foot.Y - height && iso.Y <= draw.Foot.Y)
                return draw.Mob;
        }

        return null;
    }

    private void BeginMobPass()
    {
        _mobDrawn.Clear();
        for (int i = 0; i < _visibleMobs.Count; i++)
            _mobDrawn.Add(false);
    }

    /// <summary>
    /// Draws any mob whose chunk was not drawn this frame. Its ground can be culled while
    /// the mob itself is still on screen — it stands up out of its tile — and one that fell
    /// through the merge above would simply vanish.
    /// </summary>
    private void DrawRemainingMobs(GraphicsDevice gd)
    {
        for (int i = 0; i < _visibleMobs.Count; i++)
        {
            if (_mobDrawn[i])
                continue;

            (int start, int count) = _mobSpriteRanges[i];
            _mobSprites.Draw(gd, start, count);
        }
    }

    /// <summary>The art a kind of mob is drawn from, and how tall it stands in iso pixels.</summary>
    private (TextureAtlas.Region Art, float Height) MobArt(MobKind kind) => kind switch
    {
        _ => (_atlas.Sheep, SheepHeight),
    };

    /// <summary>
    /// One mob, as a single billboard standing on its own feet — mirrored when it is
    /// walking left, and bobbing along its stride while it walks at all. The art is
    /// painted facing right, so the flip is the whole of turning round.
    /// </summary>
    private void AppendMob(in MobDraw draw, bool selected)
    {
        Mob mob = draw.Mob;
        var rng = new XorShift((uint)mob.Seed);

        // The selected mob stands in a pale ring of light on the ground. It goes in first,
        // inside this mob's own slice of the layer, so it is covered by exactly what the mob
        // itself is covered by.
        if (selected)
        {
            float r = draw.Height * SelectionRadius;
            _mobSprites.Sprite(draw.Foot, new Vector2(r * 2f, r * 2f * ShadowSquash), Center,
                               _atlas.SoftDisc, SelectionGlow, depthZ: draw.Foot.Y);
        }

        // The bob rides on the walk cycle, which only advances while ground is being
        // covered — so a grazing sheep holds still instead of bouncing on the spot.
        float bob = mob.IsWalking ? MathF.Abs(MathF.Sin(mob.WalkPhase)) * MobBob : 0f;
        var at = new Vector2(draw.Foot.X, draw.Foot.Y - bob);
        var size = new Vector2(draw.Height * draw.Art.Aspect, draw.Height);

        // A light per-mob tint, exactly as the plants get: a flock reads better when no
        // two of them are quite the same shade of white.
        _mobSprites.Sprite(at, size, BottomCenter, draw.Art, Vary(Color.White, ref rng, 0.06f),
                           flipX: mob.Facing < 0f, depthZ: draw.Foot.Y);
    }

    /// <summary>
    /// A mob's shadow: the same row of squashed discs a plant gets, laid at its feet and
    /// leaning in its z so the sun's shear swings it round with everything else. It stays
    /// at the feet rather than under the bob — the animal lifts off the ground, its shadow
    /// does not.
    /// </summary>
    private void AppendMobShadow(in MobDraw draw)
    {
        float radius = draw.Height * MobShadowRadius;
        float lean = radius * MobShadowLean;

        for (int i = 0; i < ShadowSteps; i++)
        {
            float r = radius * ShadowTaper[i];
            var size = new Vector2(r * 2f, r * 2f * ShadowSquash);
            _mobShadows.Sprite(draw.Foot, size, Center, _atlas.SoftDisc,
                               ShadowTint * ShadowFade[i], lean: lean * ShadowStep[i]);
        }
    }

    /// <summary>
    /// The height of the *drawn* ground at a fractional tile position, in elevation levels.
    ///
    /// It is the same Catmull-Rom of the level field that <see cref="SmoothLevel"/> gives
    /// the terrain mesh, only read straight out of the world instead of out of a chunk's
    /// cached field — which is what a mob needs, since it walks across chunks and is not
    /// built with any one of them. Sampling the same curve is what puts its feet on the
    /// surface the ground was actually drawn at, rather than on the flat top of its tile.
    /// </summary>
    /// <summary>Which chunk a world-tile coordinate falls in — floor division, so it holds for negatives too.</summary>
    private static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);

    private static float SurfaceLevel(World world, float tx, float ty)
    {
        int ix = (int)MathF.Floor(tx), iy = (int)MathF.Floor(ty);
        float u = tx - ix, v = ty - iy;

        Span<float> rows = stackalloc float[4];
        for (int r = 0; r < 4; r++)
        {
            int y = iy + r - 1;
            rows[r] = CatmullRom(LevelAt(world, ix - 1, y), LevelAt(world, ix, y),
                                 LevelAt(world, ix + 1, y), LevelAt(world, ix + 2, y), u);
        }

        return CatmullRom(rows[0], rows[1], rows[2], rows[3], v);
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
                Plant(at, s, item.Dead ? _atlas.DeadBushes : _atlas.Bushes, BushHeight, item.Snow, ref rng);
                break;

            case Vegetation.PineTree:
                Tree(at, s, item, _atlas.Conifers, TreeHeight, ref rng);
                break;

            case Vegetation.BroadleafTree:
                Tree(at, s, item, _atlas.Broadleaves, TreeHeight, ref rng);
                break;

            // Palms stand taller than the broadleaves beside them — they are mostly
            // trunk, so drawn to the same height their crowns come out tiny.
            case Vegetation.PalmTree:
                Tree(at, s, item, _atlas.Palms, TreeHeight * PalmHeight, ref rng);
                break;

            case Vegetation.Cactus: Cactus(at, s, ref rng); break;
            case Vegetation.Rock: Rock(at, s, ref rng); break;
        }
    }

    /// <summary>
    /// One tree, drawn from the stock of the wood it stands in. A living tree mostly takes
    /// the form its stand is grown from, so a wood comes out as one recognisable tree with
    /// a few others among it rather than a bag of everything on the sheet; standing
    /// deadwood is bare either way, and is rolled at random.
    /// </summary>
    private void Tree(Vector2 at, float s, in VegItem item, IReadOnlyList<TextureAtlas.PlantArt> set,
                      float height, ref XorShift rng)
    {
        if (item.Dead)
            Plant(at, s, _atlas.DeadTrees, height, item.Snow, ref rng);
        else
            Plant(at, s, set, height, item.Snow, ref rng, item.Variant);
    }

    /// <summary>
    /// Draws one painted plant: a single quad standing on its own base, at a height
    /// in iso pixels with the art's own proportions.
    /// </summary>
    private void Plant(Vector2 at, float s, IReadOnlyList<TextureAtlas.PlantArt> set, float height, float snow,
                       ref XorShift rng, float variant = NoVariant)
    {
        if (set.Count == 0)
            return;

        TextureAtlas.PlantArt plant = set[Form(set.Count, variant, ref rng)];
        float h = height * s * (0.85f + 0.3f * rng.Next());
        var size = new Vector2(h * plant.Art.Aspect, h);

        // Only a light tint: the art already carries its own colour and variation.
        _builder.Sprite(at, size, BottomCenter, plant.Art, Vary(Color.White, ref rng, 0.07f));

        if (snow > 0.02f)
            SnowCap(at, size, plant, snow, ref rng);
    }

    /// <summary>
    /// Which piece of a kind's art one plant is drawn as. With no stand behind it that is a
    /// straight roll over the sheet — a bush is a bush. A tree standing in a wood mostly
    /// takes the wood's own form instead: <paramref name="variant"/> is the stand's slot in
    /// the species field, mapped onto the sheet, so every wood is made of one tree; the
    /// rolls that go the other way leave the rest of the sheet scattered thinly through it,
    /// which is what keeps a stand from looking stamped.
    /// </summary>
    private static int Form(int count, float variant, ref XorShift rng)
    {
        float pick = variant >= 0f && rng.Next() < StandPurity ? variant : rng.Next();
        return Math.Min((int)(pick * count), count - 1);
    }

    /// <summary>
    /// How much of a wood is the one tree it is grown from. High enough that the stand
    /// reads as that tree at a glance, low enough that every few trees is something else.
    /// </summary>
    private const float StandPurity = 0.78f;

    /// <summary>A plant that belongs to no stand, and so takes its form at random.</summary>
    private const float NoVariant = -1f;

    /// <summary>
    /// Lays snow over a plant, as one white quad drawn over it at exactly its own size and
    /// sampling the cap baked from its art (see <see cref="TextureAtlas.PlantArt"/>). Because
    /// the cap carries the plant's own outline and its own upward-facing surfaces, the snow
    /// lands only on branches the plant actually paints — never on the transparent corners of
    /// the billboard, which is what a cluster of loose white blobs could not avoid. How white
    /// it goes scales with <paramref name="snow"/> and jitters per plant, so a stand looks
    /// unevenly laden rather than uniformly frosted.
    ///
    /// Deep in the cold it goes on twice. One coat can never be more opaque than the mask it
    /// samples, and the mask thins out down the plant and over bare twigs, which left tundra
    /// scrub — twiggy by definition, and standing in the deepest snow on the map — looking
    /// dusted rather than plastered. A second coat over the same mask covers what the first
    /// left: the crown goes solid and the thin snow further down comes up to meet it.
    /// </summary>
    private void SnowCap(Vector2 at, Vector2 size, in TextureAtlas.PlantArt plant, float snow, ref XorShift rng)
    {
        float a = Math.Min(1f, snow * (0.75f + 0.4f * rng.Next()));
        _builder.Sprite(at, size, BottomCenter, plant.Snow, SnowColor * a);

        if (snow <= SnowSecondCoat)
            return;

        float b = a * Smoothstep01((snow - SnowSecondCoat) / (1f - SnowSecondCoat));
        _builder.Sprite(at, size, BottomCenter, plant.Snow, SnowColor * b);
    }

    /// <summary>
    /// How laden a plant has to be before the snow on it gets a second coat, and how far
    /// past that the second coat takes to come fully in. Set high enough that a plant at the
    /// patchy edge of the snow's range keeps its single light dusting.
    /// </summary>
    private const float SnowSecondCoat = 0.55f;

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

    /// <summary>
    /// A rock, built as real low-poly geometry rather than a flat sprite: a ragged ring of
    /// points on the ground drawn up to an off-centre summit, so it stands as a faceted
    /// boulder that the sun catches face by face. Each rock rolls its own silhouette, summit
    /// lean and facet count off its seed, and a size class scales the whole thing — most come
    /// up as low cobbles, the odd one as a big craggy boulder — so a scatter of them reads as
    /// a field of individual stones rather than one shape stamped over and over.
    ///
    /// The face shading is baked into the vertex colour (brightest where a facet faces the
    /// sky, darker down the near-vertical sides) as well as carried in a real world-space
    /// normal, so the stone keeps its form both in the lit painter path and in the flat-lit
    /// depth path — the same reason the painted plants carry their shading in their art.
    /// </summary>
    private void Rock(Vector2 at, float s, ref XorShift rng)
    {
        Color stone = Vary(RockStone, ref rng, 0.08f);

        int sides = rng.Range(5, 7);
        // Most rocks are low cobbles; roughly one in seven is a big boulder.
        float size = (rng.Next() > 0.86f ? 9f + 5f * rng.Next() : 4.5f + 3.5f * rng.Next()) * s;
        float height = size * (0.7f + 0.5f * rng.Next());

        // The summit, leaned off-centre so the rock is lopsided rather than a neat cone.
        float leanAng = rng.Next() * MathF.PI * 2f;
        float leanR = size * 0.30f * rng.Next();
        var apex = new Vector3(MathF.Cos(leanAng) * leanR, height, MathF.Sin(leanAng) * leanR);

        // The ragged base ring, sitting on the ground (up = 0).
        Span<Vector3> ring = stackalloc Vector3[sides];
        float a0 = rng.Next() * MathF.PI * 2f;
        for (int i = 0; i < sides; i++)
        {
            float ang = a0 + i / (float)sides * MathF.PI * 2f;
            float r = size * (0.72f + 0.5f * rng.Next());
            ring[i] = new Vector3(MathF.Cos(ang) * r, 0f, MathF.Sin(ang) * r);
        }

        // One flat-shaded facet per side, its base edge drawn up to the summit.
        for (int i = 0; i < sides; i++)
            RockFace(at, stone, ring[i], ring[(i + 1) % sides], apex);
    }

    /// <summary>
    /// One flat-shaded rock facet, from three points in the rock's own (east, up, south)
    /// frame: its outward-and-up normal for the sun, and a top-light baked into the colour so
    /// the facet still reads as stone where nothing is lighting it.
    /// </summary>
    private void RockFace(Vector2 at, Color stone, Vector3 p0, Vector3 p1, Vector3 p2)
    {
        // The local frame is already the world lighting frame (x = east, y = up, z = south),
        // so the facet's own normal lights it directly. Rocks are only ever seen from above,
        // so a face is flipped to point up rather than sorted for winding.
        Vector3 nrm = Vector3.Cross(p1 - p0, p2 - p0);
        if (nrm.LengthSquared() < 1e-6f)
            return;
        nrm.Normalize();
        if (nrm.Y < 0f)
            nrm = -nrm;

        float shade = 0.68f + 0.32f * Math.Clamp(nrm.Y, 0f, 1f);
        Color c = Scale(stone, shade);

        Vector2 s0 = at + Iso.ObjectOffset(p0.X, p0.Y, p0.Z);
        Vector2 s1 = at + Iso.ObjectOffset(p1.X, p1.Y, p1.Z);
        Vector2 s2 = at + Iso.ObjectOffset(p2.X, p2.Y, p2.Z);
        _builder.Triangle(s0, c, nrm, s1, c, nrm, s2, c, nrm, _atlas.White.CenterUv);
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
        DisposeAllChunkMeshes();
        foreach (ChunkMesh grid in _gridMeshes.Values) grid.Dispose();
        _gridMeshes.Clear();
        _clouds.Dispose();
        _effect.Dispose();
        _foliageEffect.Dispose();
        _atlas.Texture.Dispose();
    }
}
