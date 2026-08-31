using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Skippy.Core.Input;
using Skippy.Core.Worlds.Generation;
using Skippy.Core.Worlds.Mobs;
using Skippy.Core.Worlds.Rendering;

namespace Skippy.Core.Worlds;

/// <summary>
/// Base class for a game world. A world owns everything about the map: its
/// generator, the set of loaded chunks, the camera, and its own update and draw.
/// Subclass it and override the virtual seams (<see cref="CreateGenerator"/>,
/// <see cref="Update"/>, <see cref="Draw"/>) to build a specific game world on
/// top of the streaming + isometric-rendering machinery.
///
/// The map is effectively infinite: chunks are generated on demand in a radius
/// around the camera and discarded once well outside it.
/// </summary>
public class World
{
    public int Seed { get; }
    public Camera Camera { get; } = new();

    /// <summary>The sun and sky. Advances every frame so the light climbs and sets on its own.</summary>
    public SkyLight Sky { get; } = new();

    /// <summary>Chunk radius kept loaded around the camera. 2 → a 5×5 chunk window.</summary>
    public int LoadRadius { get; set; } = 3;

    /// <summary>Draws one line along every tile edge, on top of the world. Toggled with G.</summary>
    public bool ShowTileGrid { get; set; }

    /// <summary>
    /// Resolve plants and mobs against the terrain and each other with a real GPU depth
    /// buffer instead of the painter's-order sort. The one thing it can do that the sort
    /// cannot: hide a plant or a mob standing behind a hill. Toggled with B (prototype).
    /// </summary>
    public bool DepthBuffer { get; set; }

    /// <summary>What the terrain is coloured by: the game view, or a raw generator field. Cycled with M.</summary>
    public MapView MapView { get; set; } = MapView.Biome;

    /// <summary>
    /// The dials this world is generated from — how warm, how wet, how old. The generator
    /// holds this very object and reads it as it builds, so turning a dial and calling
    /// <see cref="Regenerate"/> is all it takes to get the same seed back as another world.
    ///
    /// Initialized here rather than in the constructor body on purpose: field initializers
    /// run first, so it already exists when <see cref="CreateGenerator"/> is handed it.
    /// </summary>
    public WorldSettings Settings { get; } = new();

    protected IWorldGenerator Generator { get; }

    private readonly Dictionary<ChunkCoord, Chunk> _chunks = new();
    private readonly List<ChunkCoord> _unloadScratch = new();
    private WorldRenderer? _renderer;

    /// <summary>
    /// Everything alive in the world. Mobs live on the world rather than in a chunk: they
    /// walk, so the chunk they belong to changes under them, and a mob unloaded mid-stride
    /// with the ground it happened to be over would blink out of existence.
    /// </summary>
    private readonly List<Mob> _mobs = new();

    /// <summary>Every mob in the world, for the renderer to draw and anything else to read.</summary>
    public IReadOnlyList<Mob> Mobs => _mobs;

    /// <summary>
    /// The mob the player has clicked on, if any. It is drawn standing in a pool of light and
    /// its vitals go up on the read-out; nothing else about it changes.
    /// </summary>
    public Mob? SelectedMob { get; private set; }

    /// <summary>Sheep put down at the spawn point, and how far across they are scattered (in tiles).</summary>
    private const int FlockSize = 6;
    private const float FlockSpread = 1.2f;

    /// <summary>How far from the spawn point to look for dry land to put them on.</summary>
    private const int SpawnSearchRadius = 24;

    /// <summary>Names the mobs' seeds, so a given world seed always gets the same flock.</summary>
    private readonly Random _mobSeeds;
    private bool _flockSpawned;

    public World(int seed)
    {
        Seed = seed;
        Generator = CreateGenerator(seed);
        _mobSeeds = new Random(seed);
    }

    /// <summary>Override to plug in a custom terrain generator. <see cref="Settings"/> is already built.</summary>
    protected virtual IWorldGenerator CreateGenerator(int seed) => new DefaultWorldGenerator(seed, Settings);

    /// <summary>Must be called once a <see cref="GraphicsDevice"/> exists (e.g. in LoadContent).</summary>
    public void InitializeGraphics(GraphicsDevice graphicsDevice) => _renderer = new WorldRenderer(graphicsDevice);

    public int LoadedChunkCount => _chunks.Count;

    /// <summary>Returns the chunk at <paramref name="coord"/>, generating it if needed.</summary>
    public Chunk GetChunk(ChunkCoord coord)
    {
        if (!_chunks.TryGetValue(coord, out Chunk? chunk))
        {
            chunk = new Chunk(coord);
            Generator.Generate(chunk);
            _chunks[coord] = chunk;
            OnChunkGenerated(chunk);
        }
        return chunk;
    }

    /// <summary>Hook fired the first time a chunk is generated (e.g. to spawn entities).</summary>
    protected virtual void OnChunkGenerated(Chunk chunk) { }

    /// <summary>Returns the chunk at <paramref name="coord"/> only if it is already loaded (never generates).</summary>
    public bool TryGetChunk(ChunkCoord coord, out Chunk chunk)
    {
        if (_chunks.TryGetValue(coord, out Chunk? c))
        {
            chunk = c;
            return true;
        }
        chunk = null!;
        return false;
    }

    public bool IsChunkLoaded(ChunkCoord coord) => _chunks.ContainsKey(coord);

    /// <summary>Every currently loaded chunk (unordered).</summary>
    public Dictionary<ChunkCoord, Chunk>.ValueCollection LoadedChunks => _chunks.Values;

    /// <summary>Reads a tile by world-tile coordinate. False if its chunk isn't loaded.</summary>
    public bool TryGetTile(int worldX, int worldY, out Tile tile)
    {
        var coord = new ChunkCoord(FloorDiv(worldX, Chunk.Size), FloorDiv(worldY, Chunk.Size));
        if (_chunks.TryGetValue(coord, out Chunk? chunk))
        {
            tile = chunk.TileAt(worldX - chunk.OriginX, worldY - chunk.OriginY);
            return true;
        }
        tile = default;
        return false;
    }

    public virtual void Update(GameTime gameTime, InputState input)
    {
        float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;

        // The sun runs on its own clock, so the world is never done changing even
        // when the camera is still.
        Sky.Advance(dt);

        // Keyboard pan (screen-space direction → tile delta).
        if (input.MoveDirection != Vector2.Zero)
            Camera.PanTiles(ScreenVectorToTile(input.MoveDirection * (320f * dt)));

        // A press picks whatever mob is under the pointer, and clears the selection when it
        // lands on empty ground. A drag begins with a press too, so dragging off a sheep
        // selects it — which is what clicking one means anyway.
        if (input.PointerPressed && input.PointerPosition is Vector2 pressed)
            SelectedMob = _renderer?.PickMob(pressed);

        // Drag pan: keep the world pinned under the pointer.
        if (input.PointerDown && input.PointerDelta != Vector2.Zero)
            Camera.PanTiles(-ScreenVectorToTile(input.PointerDelta));

        if (input.ToggleTileGridRequested)
            ShowTileGrid = !ShowTileGrid;

        if (input.ToggleDepthBufferRequested)
            DepthBuffer = !DepthBuffer;

        if (input.CycleMapViewRequested)
            MapView = MapView.Next();

        UpdateTileSize(input.TileSizeDirection, dt);
        UpdateClock(input, dt);

        // Wheel zoom.
        if (input.ScrollDelta > 0) Camera.ZoomBy(1.1f);
        else if (input.ScrollDelta < 0) Camera.ZoomBy(1f / 1.1f);

        StreamChunks();

        // After the streaming, always: a mob asks the world what it may walk onto, and
        // this frame's answer should come from this frame's chunks.
        UpdateMobs(dt);
    }

    /// <summary>
    /// Spawns the starter flock if it is still owed, gives every mob its frame, and clears
    /// away the ones whose health ran out — starved, worn out by old age, or killed by
    /// whatever else gets at them later.
    /// </summary>
    protected virtual void UpdateMobs(float dt)
    {
        SpawnStarterFlock();

        foreach (Mob mob in _mobs)
            mob.Update(this, dt);

        // Backwards, so removing one doesn't shuffle the ones not yet looked at.
        for (int i = _mobs.Count - 1; i >= 0; i--)
        {
            if (_mobs[i].IsAlive)
                continue;

            if (_mobs[i] == SelectedMob)
                SelectedMob = null;
            _mobs.RemoveAt(i);
        }
    }

    /// <summary>
    /// Puts a small flock of sheep down at the spawn point, once, as soon as there is
    /// ground under it for them to stand on.
    ///
    /// It runs from the update rather than the constructor because nothing exists to stand
    /// on until the first frame has streamed the chunks in — and if the spawn point turns
    /// out to be at sea, this simply finds nothing and tries again next frame, by which
    /// time the camera may well have been driven somewhere with a coastline on it.
    /// </summary>
    private void SpawnStarterFlock()
    {
        if (_flockSpawned || !TryFindGroundNear(Camera.FocusTile, SpawnSearchRadius, out Vector2 spot))
            return;

        for (int i = 0; i < FlockSize; i++)
        {
            // Scattered around the spot, but never into the sea: one that lands badly is
            // put on the spot itself and walks off it on its own.
            var at = new Vector2(spot.X + NextSpread(), spot.Y + NextSpread());
            if (!Mob.CanStand(this, at))
                at = spot;

            _mobs.Add(new Sheep(at, _mobSeeds.Next()));
        }

        _flockSpawned = true;
    }

    private float NextSpread() => (float)(_mobSeeds.NextDouble() * 2f - 1f) * FlockSpread;

    /// <summary>
    /// The nearest loaded, dry tile to <paramref name="near"/>, searched outward in
    /// square rings so the first thing found really is the closest. Its centre, so
    /// whatever stands there stands in the middle of the tile rather than on its corner.
    /// </summary>
    private bool TryFindGroundNear(Vector2 near, int radius, out Vector2 found)
    {
        int cx = (int)MathF.Floor(near.X), cy = (int)MathF.Floor(near.Y);
        for (int r = 0; r <= radius; r++)
        {
            for (int y = cy - r; y <= cy + r; y++)
            {
                for (int x = cx - r; x <= cx + r; x++)
                {
                    // Only the ring itself: the inside of it was covered by a smaller r.
                    if (r > 0 && Math.Abs(x - cx) != r && Math.Abs(y - cy) != r)
                        continue;

                    if (TryGetTile(x, y, out Tile tile) && !tile.IsWater)
                    {
                        found = new Vector2(x + 0.5f, y + 0.5f);
                        return true;
                    }
                }
            }
        }

        found = default;
        return false;
    }

    public virtual void Draw(SpriteBatch spriteBatch, GraphicsDevice graphicsDevice)
        => _renderer?.Draw(graphicsDevice, this);

    /// <summary>Iso pixels added to the tile width per step, and the key press-and-repeat timings.</summary>
    private const int TileSizeStep = 8;
    private const float TileResizeDelay = 0.35f, TileResizeRepeat = 0.12f;

    private float _tileResizeTimer;
    private int _prevTileDirection;

    /// <summary>
    /// Grows or shrinks the tiles while [ or ] is held, acting at once on the press
    /// and then repeating. The repeat is deliberately unhurried: each step throws
    /// away every cached chunk mesh, so the visible chunks are rebuilt per step.
    /// </summary>
    private void UpdateTileSize(int direction, float dt)
    {
        if (direction == 0)
        {
            _prevTileDirection = 0;
            return;
        }

        _tileResizeTimer -= dt;
        if (direction != _prevTileDirection)
            _tileResizeTimer = 0f; // a fresh press (or a reversal) acts immediately

        if (_tileResizeTimer <= 0f)
        {
            bool repeating = direction == _prevTileDirection;
            Iso.TileWidth += direction * TileSizeStep;
            _tileResizeTimer = repeating ? TileResizeRepeat : TileResizeDelay;
        }

        _prevTileDirection = direction;
    }

    /// <summary>Days the clock sweeps per second while a scrub key is held — a full day in about four seconds.</summary>
    private const float ClockScrubRate = 0.25f;

    /// <summary>
    /// Sets the time of day by hand: `,` and `.` wind the sun back and forward, and P holds
    /// the clock or lets it run again.
    ///
    /// Scrubbing holds the clock on its own, so a time picked by eye stays where it was put
    /// instead of drifting off it; P is how the day is started again afterwards.
    /// </summary>
    private void UpdateClock(InputState input, float dt)
    {
        if (input.ToggleClockRequested)
            Sky.Paused = !Sky.Paused;

        if (input.TimeDirection == 0)
            return;

        Sky.Paused = true;
        Sky.Scrub(input.TimeDirection * ClockScrubRate * dt);
    }

    /// <summary>
    /// Throws every generated chunk away so the map is built again from the current
    /// settings. Anything baked in at generation time only changes by way of this.
    /// </summary>
    /// <remarks>
    /// The mobs go with it. They were put down on ground that is about to be replaced,
    /// and a flock left standing where a coastline used to be would be grazing the sea.
    /// </remarks>
    public void Regenerate()
    {
        _chunks.Clear();
        _mobs.Clear();
        SelectedMob = null;
        _flockSpawned = false;

        // The renderer caches a mesh per chunk *coordinate*, and regeneration hands those
        // same coordinates back — so without this it would go on drawing the old ground,
        // and nothing on screen would change.
        _renderer?.Invalidate();
    }

    /// <summary>Loads chunks within <see cref="LoadRadius"/> of the camera and unloads the rest.</summary>
    protected virtual void StreamChunks()
    {
        int fcx = FloorDiv((int)MathF.Floor(Camera.FocusTile.X), Chunk.Size);
        int fcy = FloorDiv((int)MathF.Floor(Camera.FocusTile.Y), Chunk.Size);

        for (int cy = -LoadRadius; cy <= LoadRadius; cy++)
            for (int cx = -LoadRadius; cx <= LoadRadius; cx++)
                GetChunk(new ChunkCoord(fcx + cx, fcy + cy));

        _unloadScratch.Clear();
        foreach (KeyValuePair<ChunkCoord, Chunk> kv in _chunks)
        {
            if (Math.Abs(kv.Key.X - fcx) > LoadRadius + 1 || Math.Abs(kv.Key.Y - fcy) > LoadRadius + 1)
                _unloadScratch.Add(kv.Key);
        }
        foreach (ChunkCoord key in _unloadScratch)
            _chunks.Remove(key);
    }

    /// <summary>
    /// The tile under a screen-space point (the mouse, normally). False when that
    /// spot's chunk isn't loaded.
    ///
    /// Elevation is ignored, the same approximation <see cref="Iso.IsoToTile"/>
    /// makes, so over steep ground the pick lands a tile or so downhill of what is
    /// drawn there — the terrain is lifted up the screen, the maths isn't.
    /// </summary>
    public bool TryGetTileAtScreen(Vector2 screenPos, Viewport viewport, out Tile tile)
        => TryGetTileAtScreen(screenPos, viewport, out tile, out _);

    /// <summary>
    /// As <see cref="TryGetTileAtScreen(Vector2, Viewport, out Tile)"/>, and also reports the
    /// world-tile coordinates the point falls on. Those are known whether or not the tile
    /// itself is loaded, so they are filled in even when this returns false.
    /// </summary>
    public bool TryGetTileAtScreen(Vector2 screenPos, Viewport viewport, out Tile tile, out Point coord)
    {
        var center = new Vector2(viewport.Width / 2f, viewport.Height / 2f);
        Vector2 iso = (screenPos - center) / Camera.Zoom + Camera.FocusIso;
        Vector2 t = Iso.IsoToTile(iso.X, iso.Y);
        coord = new Point((int)MathF.Floor(t.X), (int)MathF.Floor(t.Y));
        return TryGetTile(coord.X, coord.Y, out tile);
    }

    /// <summary>Converts a screen-space delta (pixels) into a world-tile delta.</summary>
    private Vector2 ScreenVectorToTile(Vector2 screenDelta)
    {
        Vector2 iso = screenDelta / Camera.Zoom;
        return Iso.IsoToTile(iso.X, iso.Y);
    }

    private static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);
}
