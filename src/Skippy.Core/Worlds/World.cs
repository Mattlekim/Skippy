using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Skippy.Core.Input;
using Skippy.Core.Worlds.Generation;
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

    /// <summary>Chunk radius kept loaded around the camera. 2 → a 5×5 chunk window.</summary>
    public int LoadRadius { get; set; } = 3;

    /// <summary>Draws one line along every tile edge, on top of the world. Toggled with G.</summary>
    public bool ShowTileGrid { get; set; }

    /// <summary>What the terrain is coloured by: the game view, or a raw generator field. Cycled with M.</summary>
    public MapView MapView { get; set; } = MapView.Biome;

    protected IWorldGenerator Generator { get; }

    private readonly Dictionary<ChunkCoord, Chunk> _chunks = new();
    private readonly List<ChunkCoord> _unloadScratch = new();
    private WorldRenderer? _renderer;

    public World(int seed)
    {
        Seed = seed;
        Generator = CreateGenerator(seed);
    }

    /// <summary>Override to plug in a custom terrain generator.</summary>
    protected virtual IWorldGenerator CreateGenerator(int seed) => new DefaultWorldGenerator(seed);

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

        // Keyboard pan (screen-space direction → tile delta).
        if (input.MoveDirection != Vector2.Zero)
            Camera.PanTiles(ScreenVectorToTile(input.MoveDirection * (320f * dt)));

        // Drag pan: keep the world pinned under the pointer.
        if (input.PointerDown && input.PointerDelta != Vector2.Zero)
            Camera.PanTiles(-ScreenVectorToTile(input.PointerDelta));

        if (input.ToggleTileGridRequested)
            ShowTileGrid = !ShowTileGrid;

        if (input.CycleMapViewRequested)
            MapView = MapView.Next();

        UpdateTileSize(input.TileSizeDirection, dt);
        UpdateRiverWidth(input.RiverWidthDirection, dt);

        // Wheel zoom.
        if (input.ScrollDelta > 0) Camera.ZoomBy(1.1f);
        else if (input.ScrollDelta < 0) Camera.ZoomBy(1f / 1.1f);

        StreamChunks();
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

    /// <summary>Tiles added to the river width per step, and its press-and-repeat timings.</summary>
    private const float RiverWidthStep = 0.5f;
    private const float RiverResizeDelay = 0.35f, RiverResizeRepeat = 0.18f;

    private float _riverResizeTimer;
    private int _prevRiverDirection;

    /// <summary>
    /// Widens or narrows rivers while , or . is held. River channels are cut during
    /// generation, so a change means throwing the map away and building it again —
    /// which is why this repeats more slowly than the tile-size keys.
    /// </summary>
    private void UpdateRiverWidth(int direction, float dt)
    {
        if (direction == 0)
        {
            _prevRiverDirection = 0;
            return;
        }

        _riverResizeTimer -= dt;
        if (direction != _prevRiverDirection)
            _riverResizeTimer = 0f;

        if (_riverResizeTimer <= 0f)
        {
            bool repeating = direction == _prevRiverDirection;
            float width = Math.Clamp(WorldConstants.RiverWidth + direction * RiverWidthStep,
                                     WorldConstants.MinRiverWidth, WorldConstants.MaxRiverWidth);
            if (width != WorldConstants.RiverWidth)
            {
                WorldConstants.RiverWidth = width;
                Regenerate();
            }

            _riverResizeTimer = repeating ? RiverResizeRepeat : RiverResizeDelay;
        }

        _prevRiverDirection = direction;
    }

    /// <summary>
    /// Throws every generated chunk away so the map is built again from the current
    /// settings. Anything baked in at generation time — river channels, for one —
    /// only changes by way of this.
    /// </summary>
    public void Regenerate() => _chunks.Clear();

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
    {
        var center = new Vector2(viewport.Width / 2f, viewport.Height / 2f);
        Vector2 iso = (screenPos - center) / Camera.Zoom + Camera.FocusIso;
        Vector2 t = Iso.IsoToTile(iso.X, iso.Y);
        return TryGetTile((int)MathF.Floor(t.X), (int)MathF.Floor(t.Y), out tile);
    }

    /// <summary>Converts a screen-space delta (pixels) into a world-tile delta.</summary>
    private Vector2 ScreenVectorToTile(Vector2 screenDelta)
    {
        Vector2 iso = screenDelta / Camera.Zoom;
        return Iso.IsoToTile(iso.X, iso.Y);
    }

    private static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);
}
