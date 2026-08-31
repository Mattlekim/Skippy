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
///
/// <para>
/// <b>The geometry is not worked out here.</b> That is <see cref="ChunkMesher"/>, and it
/// runs on worker threads by way of <see cref="ChunkMeshQueue"/>: cutting a chunk's
/// vertices cost 28 ms for ordinary ground and over 200 ms for dense forest, which is
/// what made zooming out and dragging the map stutter. What is left on this thread is
/// asking for the meshes that are missing, copying the finished ones onto the GPU (see
/// <see cref="MeshUploadsPerFrame"/>), and the drawing itself — plus the mobs, which
/// move every frame and so are gathered fresh rather than cached.
/// </para>
/// </summary>
public sealed class WorldRenderer : IDisposable
{
    private readonly GraphicsDevice _gd;
    private readonly BasicEffect _effect;
    private readonly TextureAtlas _atlas;

    /// <summary>
    /// The workers that turn tiles into geometry, off this thread. What comes back is
    /// uploaded here — see <see cref="UploadFinishedMeshes"/>.
    /// </summary>
    private readonly ChunkMeshQueue _meshQueue;

    /// <summary>
    /// A mesher kept on this thread for the tile-grid overlay alone. The grid is a debug
    /// layer drawn only while it is switched on, and cheap next to the terrain, so it is
    /// not worth putting through the queue.
    /// </summary>
    private readonly ChunkMesher _gridMesher;

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

    private readonly List<ChunkCoord> _scratchCoords = new();
    private readonly List<Chunk> _visible = new();

    /// <summary>The <see cref="Iso.Version"/> and map view the cached meshes were built at.</summary>
    private int _isoVersion = Iso.Version;
    private MapView _mapView = MapView.Biome;

    public WorldRenderer(GraphicsDevice gd)
    {
        _gd = gd;
        _atlas = new TextureAtlas(gd);
        _meshQueue = new ChunkMeshQueue(_atlas);
        _gridMesher = new ChunkMesher(_atlas);
        _nearestFirst = (a, b) => DistanceToFocus(a).CompareTo(DistanceToFocus(b));
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

    /// <summary>
    /// How many finished chunks may be copied onto the GPU in one frame.
    ///
    /// Everything else about building a chunk's geometry has left this thread (see
    /// <see cref="ChunkMeshQueue"/>); this is what is left, and it can never leave,
    /// because the vertex buffers belong to the thread that owns the device. It is the
    /// cheap end of the job — measured at about an eighth of the build — but a burst of
    /// arrivals would still show, so they are spread.
    ///
    /// Two keeps up with a camera being dragged about while capping what any one frame
    /// can be asked to pay. Chunks still waiting their turn simply are not drawn, so the
    /// map fills in over a frame or two out of the black behind it.
    /// </summary>
    private const int MeshUploadsPerFrame = 2;

    /// <summary>
    /// Chunks whose geometry is missing or stale this frame, gathered before any is asked
    /// for so the nearest can go first. A field so the gathering allocates nothing.
    /// </summary>
    private readonly List<Chunk> _wantMesh = new();

    /// <summary>Held rather than written inline so sorting <see cref="_wantMesh"/> allocates no closure.</summary>
    private readonly Comparison<Chunk> _nearestFirst;

    /// <summary>Where the camera is looking, for that sort.</summary>
    private Vector2 _meshSortFocus;

    /// <summary>
    /// Puts one chunk's finished geometry on the GPU and files it under its coordinate.
    /// Stale arrivals — cut for a tile size or a map view that has since moved on — are
    /// dropped here rather than uploaded.
    /// </summary>
    private void UploadFinishedMeshes()
    {
        int budget = MeshUploadsPerFrame;
        while (budget > 0 && _meshQueue.TryTakeFinished(out ChunkMeshQueue.Built built))
        {
            if (built.IsoVersion != _isoVersion || built.MapView != _mapView)
            {
                built.Discard();
                continue;
            }

            budget--;
            DisposeChunkMeshes(built.Coord);
            _meshes[built.Coord] = built.Ground!.Upload(_gd);
            _shadowMeshes[built.Coord] = built.Shadows!.Upload(_gd);
            _vegMeshes[built.Coord] = built.Veg!.Upload(_gd);
        }
    }

    /// <summary>How many chunks the meshing workers have in hand right now.</summary>
    public int MeshingChunkCount => _meshQueue.InFlight;

    /// <summary>
    /// Sets the workers going on the chunks gathered this frame, nearest to the camera
    /// first, up to what the queue will take. Re-picked from scratch every frame, so a
    /// camera that turns around is followed rather than obeyed late.
    /// </summary>
    private void RequestMeshes(World world, Vector2 focusIso)
    {
        if (_wantMesh.Count == 0 || _meshQueue.AtCapacity)
            return;

        _meshSortFocus = focusIso;
        if (_wantMesh.Count > 1)
            _wantMesh.Sort(_nearestFirst);

        foreach (Chunk chunk in _wantMesh)
        {
            if (_meshQueue.AtCapacity)
                break;

            _meshQueue.Request(ChunkView.Around(world, chunk), chunk,
                               NeighborMask(world, chunk.Coord), _isoVersion);
        }
    }

    /// <summary>How far a chunk's middle sits from the camera, squared (only the order matters).</summary>
    private float DistanceToFocus(Chunk chunk)
    {
        Vector2 middle = Iso.TileToIso(chunk.OriginX + Chunk.Size / 2f,
                                       chunk.OriginY + Chunk.Size / 2f, 0f);
        return (middle - _meshSortFocus).LengthSquared();
    }

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

        // Take delivery of whatever the meshing workers finished since the last frame, and
        // put it on the GPU. This is the only part of building a chunk's geometry left on
        // this thread, and the only part that could never leave it.
        UploadFinishedMeshes();

        // Gather visible chunks, and ask for the geometry of any that is missing or stale.
        _visible.Clear();
        _wantMesh.Clear();
        foreach (Chunk chunk in world.LoadedChunks)
        {
            if (!ChunkMayBeVisible(chunk, isoMin, isoMax))
                continue;

            byte mask = NeighborMask(world, chunk.Coord);
            bool built = _meshes.TryGetValue(chunk.Coord, out ChunkMesh? mesh);
            if (!built || mesh!.NeighborMask != mask)
            {
                if (!_meshQueue.IsBuilding(chunk.Coord))
                    _wantMesh.Add(chunk);

                // Nothing to draw yet: a chunk that has never been meshed leaves the black
                // behind the map showing through until its geometry lands, and one whose
                // neighbour has just appeared goes on drawing the mesh it has — a frame or
                // two of stale edge blending is nothing to look at.
                if (!built)
                    continue;
            }

            if (mesh!.IsEmpty || !Overlaps(mesh.BoundsMin, mesh.BoundsMax, isoMin, isoMax))
                continue;

            if (world.ShowTileGrid &&
                (!_gridMeshes.TryGetValue(chunk.Coord, out ChunkMesh? grid) || grid.NeighborMask != mask))
            {
                // The grid is a debug overlay drawn only while it is switched on, so it is
                // still cut here and now rather than queued.
                grid?.Dispose();
                _gridMeshes[chunk.Coord] = _gridMesher
                    .BuildGridMesh(ChunkView.Around(world, chunk), chunk, mask).Upload(gd);
            }

            _visible.Add(chunk);
        }

        RequestMeshes(world, focusIso);

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
        // Before the meshes themselves, and this one matters: there may be workers part
        // way through chunks of the world that has just been thrown away. Nothing here
        // would otherwise notice — the tile size and the map view have not moved, which
        // is all an arriving mesh is checked against — and the old ground would be
        // uploaded on top of the new.
        _meshQueue.AbandonInFlight();

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

        // Everything in flight was cut for the old size or the old colouring. It would be
        // turned away on arrival by the check in UploadFinishedMeshes anyway; dropping it
        // now frees the workers to start on meshes that will actually be used.
        _meshQueue.AbandonInFlight();

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
        float minY = Iso.TileToIso(ox, oy, WorldConstants.MaxLevel).Y - ChunkMesher.MaxSpriteHeight;
        float maxY = Iso.TileToIso(ox + s, oy + s, 0).Y;
        return Overlaps(new Vector2(minX, minY), new Vector2(maxX, maxY), isoMin, isoMax);
    }

    private static bool Overlaps(Vector2 aMin, Vector2 aMax, Vector2 bMin, Vector2 bMax)
        => aMin.X <= bMax.X && aMax.X >= bMin.X && aMin.Y <= bMax.Y && aMax.Y >= bMin.Y;

    // ---- mesh construction ---------------------------------------------------

    /// <summary>
    /// How strongly shadows are laid down right now: full once the sun is properly up,
    /// fading to nothing as it touches the horizon and gone entirely while it is below.
    /// Without a sun there is nothing to cast a shadow, and a pool of darkness left under
    /// every tree at midnight reads as grime on the grass.
    /// </summary>
    private static float ShadowStrength(SkyLight sky) => sky.Sunlight;

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
        direction.Y *= ChunkMesher.ShadowSquash;

        // Longest at dawn and dusk, shortest at noon — 1/tan of the sun's altitude, held to
        // the range above. Below the horizon it is pinned to the short end: there is no sun
        // to cast anything, and the night is dark enough that what is left reads as the
        // plant's own footprint.
        float altitude = MathF.Max(sky.Altitude, 0.02f);
        float reach = Math.Clamp(1f / MathF.Tan(altitude), ChunkMesher.ShadowReachMin, ChunkMesher.ShadowReachMax);

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
            _mobSprites.Sprite(draw.Foot, new Vector2(r * 2f, r * 2f * ChunkMesher.ShadowSquash), ChunkMesher.Center,
                               _atlas.SoftDisc, SelectionGlow, depthZ: draw.Foot.Y);
        }

        // The bob rides on the walk cycle, which only advances while ground is being
        // covered — so a grazing sheep holds still instead of bouncing on the spot.
        float bob = mob.IsWalking ? MathF.Abs(MathF.Sin(mob.WalkPhase)) * MobBob : 0f;
        var at = new Vector2(draw.Foot.X, draw.Foot.Y - bob);
        var size = new Vector2(draw.Height * draw.Art.Aspect, draw.Height);

        // A light per-mob tint, exactly as the plants get: a flock reads better when no
        // two of them are quite the same shade of white.
        _mobSprites.Sprite(at, size, ChunkMesher.BottomCenter, draw.Art, ChunkMesher.Vary(Color.White, ref rng, 0.06f),
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

        for (int i = 0; i < ChunkMesher.ShadowSteps; i++)
        {
            float r = radius * ChunkMesher.ShadowTaper[i];
            var size = new Vector2(r * 2f, r * 2f * ChunkMesher.ShadowSquash);
            _mobShadows.Sprite(draw.Foot, size, ChunkMesher.Center, _atlas.SoftDisc,
                               ChunkMesher.ShadowTint * ChunkMesher.ShadowFade[i], lean: lean * ChunkMesher.ShadowStep[i]);
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
            rows[r] = ChunkMesher.CatmullRom(LevelAt(world, ix - 1, y), LevelAt(world, ix, y),
                                 LevelAt(world, ix + 1, y), LevelAt(world, ix + 2, y), u);
        }

        return ChunkMesher.CatmullRom(rows[0], rows[1], rows[2], rows[3], v);
    }

    /// <summary>
    /// A tile's elevation level, or 0 where the ground isn't loaded. The mesher has its
    /// own of these reading a <see cref="ChunkView"/>; this one reads the live world,
    /// which is what a mob needs — it walks across chunks and belongs to no one of them.
    /// </summary>
    private static int LevelAt(World world, int wx, int wy) => world.TryGetTile(wx, wy, out Tile t) ? t.Level : 0;

    // ---- species -------------------------------------------------------------

    // ---- sprite helpers (all append to the current chunk mesh) --------------

    // ---- maths -----------------------------------------------------------------

    public void Dispose()
    {
        // The workers are not waited on — they hold nothing but their own scratch and the
        // chunks they were given — but whatever they have already handed back has pooled
        // arrays in it that should go home.
        _meshQueue.AbandonInFlight();

        DisposeAllChunkMeshes();
        foreach (ChunkMesh grid in _gridMeshes.Values) grid.Dispose();
        _gridMeshes.Clear();
        _clouds.Dispose();
        _effect.Dispose();
        _foliageEffect.Dispose();
        _atlas.Texture.Dispose();
    }
}
