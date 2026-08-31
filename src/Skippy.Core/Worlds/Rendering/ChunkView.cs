using System;

namespace Skippy.Core.Worlds.Rendering;

/// <summary>
/// A fixed window onto one chunk and the eight around it — everything building that
/// chunk's geometry is allowed to read, and nothing else.
///
/// <para>
/// It exists because the mesh is built on a worker thread while the update thread goes
/// on streaming: chunks are added to and removed from the world's dictionary every
/// frame, and reading that dictionary from another thread while it is being written is
/// exactly the race this avoids. A view is taken on the update thread, in one moment,
/// and holds the chunks themselves — which never change once generated (only the
/// generator ever writes a tile, and only before the chunk is delivered). So the worker
/// reads a world that cannot move under it, and a chunk unloaded meanwhile stays alive
/// as long as the view that is reading it.
/// </para>
/// <para>
/// Three by three is enough because the mesher never reaches further than
/// <c>ChunkMesher.FieldMargin</c> tiles outside the chunk it is building — a small
/// margin for the smoothing and the shore, nothing like a whole chunk. A read past the
/// window comes back false, exactly as a read of unloaded ground does, so the edge of
/// the view behaves like the edge of the loaded world.
/// </para>
/// <para>
/// The dials that colour the result — the map view and the world's bounds — are copied
/// in for the same reason: they are read live off the world elsewhere, and a worker must
/// not see them change halfway through a chunk.
/// </para>
/// </summary>
public readonly struct ChunkView
{
    /// <summary>The 3×3 block, row-major from (centre − 1, centre − 1). Nulls where not loaded.</summary>
    private readonly Chunk?[] _chunks;

    private readonly int _centerX, _centerY;

    /// <summary>The chunk this view was taken for — the one being built.</summary>
    public readonly Chunk Center;

    /// <summary>Where the world ends, as it stood when the view was taken.</summary>
    public readonly WorldBounds Bounds;

    /// <summary>What the terrain is being coloured by, as it stood when the view was taken.</summary>
    public readonly MapView MapView;

    private ChunkView(Chunk?[] chunks, Chunk center, WorldBounds bounds, MapView mapView)
    {
        _chunks = chunks;
        Center = center;
        _centerX = center.Coord.X;
        _centerY = center.Coord.Y;
        Bounds = bounds;
        MapView = mapView;
    }

    /// <summary>
    /// Takes the window around <paramref name="chunk"/>. Call on the update thread: it
    /// reads the world's chunk table, which only that thread may touch.
    /// </summary>
    public static ChunkView Around(World world, Chunk chunk)
    {
        var chunks = new Chunk?[9];
        int cx = chunk.Coord.X, cy = chunk.Coord.Y;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                chunks[(dy + 1) * 3 + (dx + 1)] =
                    world.TryGetChunk(new ChunkCoord(cx + dx, cy + dy), out Chunk c) ? c : null;
            }

        return new ChunkView(chunks, chunk, world.Bounds, world.MapView);
    }

    /// <summary>
    /// Reads a tile by world-tile coordinate. False outside the window, which is the same
    /// answer <see cref="World.TryGetTile"/> gives for ground that isn't loaded — so the
    /// mesher treats the two alike and needs to know about neither.
    /// </summary>
    public bool TryGetTile(int worldX, int worldY, out Tile tile)
    {
        int dx = FloorDiv(worldX, Chunk.Size) - _centerX;
        int dy = FloorDiv(worldY, Chunk.Size) - _centerY;

        if ((uint)(dx + 1) > 2u || (uint)(dy + 1) > 2u)
        {
            tile = default;
            return false;
        }

        Chunk? chunk = _chunks[(dy + 1) * 3 + (dx + 1)];
        if (chunk is null)
        {
            tile = default;
            return false;
        }

        tile = chunk.TileAt(worldX - chunk.OriginX, worldY - chunk.OriginY);
        return true;
    }

    private static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);
}
