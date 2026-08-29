using System;

namespace Skippy.Core.Worlds;

/// <summary>Integer position of a chunk on the infinite chunk grid.</summary>
public readonly struct ChunkCoord : IEquatable<ChunkCoord>
{
    public readonly int X;
    public readonly int Y;

    public ChunkCoord(int x, int y)
    {
        X = x;
        Y = y;
    }

    public bool Equals(ChunkCoord other) => X == other.X && Y == other.Y;
    public override bool Equals(object? obj) => obj is ChunkCoord c && Equals(c);
    public override int GetHashCode() => unchecked((X * 397) ^ Y);
    public override string ToString() => $"({X}, {Y})";
}

/// <summary>
/// A fixed <see cref="Size"/>×<see cref="Size"/> block of tiles. Chunks are the
/// unit of streaming: the world generates them on demand near the camera and
/// discards them when far away, which is what makes the map effectively infinite.
///
/// 32 is a deliberate middle ground — large enough that per-chunk bookkeeping
/// (dictionary entries, generation setup) is amortized over 1024 tiles, small
/// enough that a single chunk generates in well under a frame and culls cleanly.
/// </summary>
public sealed class Chunk
{
    public const int Size = 32;

    public readonly ChunkCoord Coord;
    public readonly Tile[] Tiles = new Tile[Size * Size];
    public bool Generated;

    public Chunk(ChunkCoord coord) => Coord = coord;

    /// <summary>World-tile X of this chunk's local (0,0).</summary>
    public int OriginX => Coord.X * Size;

    /// <summary>World-tile Y of this chunk's local (0,0).</summary>
    public int OriginY => Coord.Y * Size;

    public ref Tile TileAt(int localX, int localY) => ref Tiles[localY * Size + localX];
}
