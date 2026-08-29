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

        // Wheel zoom.
        if (input.ScrollDelta > 0) Camera.ZoomBy(1.1f);
        else if (input.ScrollDelta < 0) Camera.ZoomBy(1f / 1.1f);

        StreamChunks();
    }

    public virtual void Draw(SpriteBatch spriteBatch, GraphicsDevice graphicsDevice)
        => _renderer?.Draw(spriteBatch, graphicsDevice, this);

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

    /// <summary>Converts a screen-space delta (pixels) into a world-tile delta.</summary>
    private Vector2 ScreenVectorToTile(Vector2 screenDelta)
    {
        Vector2 iso = screenDelta / Camera.Zoom;
        return Iso.IsoToTile(iso.X, iso.Y);
    }

    private static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);
}
