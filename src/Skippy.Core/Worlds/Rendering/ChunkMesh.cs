using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Skippy.Core.Worlds.Rendering;

/// <summary>
/// The GPU-resident geometry for one layer of one chunk — its ground, its plant
/// shadows or its plants — all in iso-space (world) coordinates so the buffers never
/// change as the camera moves — only the effect's view matrix does.
///
/// Built once by <see cref="MeshBuilder"/>, drawn every frame, disposed when the
/// chunk unloads. Split into segments only when a chunk has more geometry than a
/// 16-bit index buffer can address (dense forests).
/// </summary>
public sealed class ChunkMesh : IDisposable
{
    public readonly struct Segment
    {
        public readonly VertexBuffer Vertices;
        public readonly IndexBuffer Indices;
        public readonly int PrimitiveCount;

        public Segment(VertexBuffer vertices, IndexBuffer indices, int primitiveCount)
        {
            Vertices = vertices;
            Indices = indices;
            PrimitiveCount = primitiveCount;
        }
    }

    /// <summary>
    /// A position inside the mesh's index data: which segment, and how far into it. Segments
    /// exist only because a 16-bit index buffer runs out on a dense chunk, so a cursor has to
    /// name both to point at "this far through the geometry".
    /// </summary>
    public readonly struct Cursor
    {
        public readonly int Segment;
        public readonly int Index;

        public Cursor(int segment, int index)
        {
            Segment = segment;
            Index = index;
        }

        /// <summary>The very start of the mesh.</summary>
        public static Cursor Zero => default;
    }

    /// <summary>
    /// Where each sprite in this mesh begins, paired with the screen y it was sorted by.
    /// Empty for meshes that carry no sprites (ground, grid).
    ///
    /// This is what lets something that is *not* baked into the mesh — a mob, which moves —
    /// be drawn in its right place among things that are: draw the plants up to the mob's
    /// own y, draw the mob, then draw the rest. Without it a sheep can only be drawn before
    /// or after a whole chunk's worth of plants, and it walks over the front of trees it is
    /// standing behind.
    /// </summary>
    private readonly List<Slice> _slices;

    /// <summary>One sprite's place in the mesh: the y it sorted at, and where its geometry starts.</summary>
    public readonly struct Slice
    {
        public readonly float Y;
        public readonly Cursor At;

        public Slice(float y, Cursor at)
        {
            Y = y;
            At = at;
        }
    }

    private readonly List<Segment> _segments;

    /// <summary>Iso-space bounding box of everything in the mesh, for culling.</summary>
    public readonly Vector2 BoundsMin, BoundsMax;

    /// <summary>
    /// Which of the 8 neighbouring chunks were loaded when this mesh was built.
    /// Edge vertices blend across chunk borders, so when a neighbour appears the
    /// mesh must be rebuilt to pick up its colours and elevations.
    /// </summary>
    public readonly byte NeighborMask;

    public ChunkMesh(List<Segment> segments, Vector2 boundsMin, Vector2 boundsMax, byte neighborMask,
                     List<Slice>? slices = null)
    {
        _segments = segments;
        BoundsMin = boundsMin;
        BoundsMax = boundsMax;
        NeighborMask = neighborMask;
        _slices = slices ?? EmptySlices;
    }

    private static readonly List<Slice> EmptySlices = new();

    public bool IsEmpty => _segments.Count == 0;

    /// <summary>The end of the mesh — everything is between <see cref="Cursor.Zero"/> and this.</summary>
    public Cursor End => _segments.Count == 0
        ? Cursor.Zero
        : new Cursor(_segments.Count - 1, _segments[^1].PrimitiveCount * 3);

    /// <summary>Issues the draw calls. The caller has already applied the effect pass.</summary>
    public void Draw(GraphicsDevice gd)
    {
        foreach (Segment s in _segments)
        {
            gd.SetVertexBuffer(s.Vertices);
            gd.Indices = s.Indices;
            gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, s.PrimitiveCount);
        }
    }

    /// <summary>
    /// Draws the slice of the mesh between two cursors — everything from
    /// <paramref name="from"/> up to, but not including, <paramref name="to"/>.
    /// </summary>
    public void DrawRange(GraphicsDevice gd, Cursor from, Cursor to)
    {
        for (int s = from.Segment; s <= to.Segment && s < _segments.Count; s++)
        {
            Segment seg = _segments[s];
            int start = s == from.Segment ? from.Index : 0;
            int end = s == to.Segment ? to.Index : seg.PrimitiveCount * 3;
            if (end <= start)
                continue;

            gd.SetVertexBuffer(seg.Vertices);
            gd.Indices = seg.Indices;
            gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, start, (end - start) / 3);
        }
    }

    /// <summary>
    /// Where in the mesh the first sprite standing at or below <paramref name="y"/> begins —
    /// which is to say, the point to stop drawing at if something at that y is to be drawn
    /// next. The slices are in y order, so this is a binary search.
    /// </summary>
    public Cursor CursorAt(float y)
    {
        int lo = 0, hi = _slices.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_slices[mid].Y < y) lo = mid + 1;
            else hi = mid;
        }
        return lo < _slices.Count ? _slices[lo].At : End;
    }

    public void Dispose()
    {
        foreach (Segment s in _segments)
        {
            s.Vertices.Dispose();
            s.Indices.Dispose();
        }
        _segments.Clear();
    }
}
