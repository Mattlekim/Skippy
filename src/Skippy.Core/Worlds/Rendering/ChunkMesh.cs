using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Skippy.Core.Worlds.Rendering;

/// <summary>
/// The GPU-resident geometry for one chunk: terrain quads followed by every
/// vegetation sprite, all in iso-space (world) coordinates so the buffers never
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

    private readonly List<Segment> _segments;

    /// <summary>Iso-space bounding box of everything in the mesh, for culling.</summary>
    public readonly Vector2 BoundsMin, BoundsMax;

    /// <summary>
    /// Which of the 8 neighbouring chunks were loaded when this mesh was built.
    /// Edge vertices blend across chunk borders, so when a neighbour appears the
    /// mesh must be rebuilt to pick up its colours and elevations.
    /// </summary>
    public readonly byte NeighborMask;

    public ChunkMesh(List<Segment> segments, Vector2 boundsMin, Vector2 boundsMax, byte neighborMask)
    {
        _segments = segments;
        BoundsMin = boundsMin;
        BoundsMax = boundsMax;
        NeighborMask = neighborMask;
    }

    public bool IsEmpty => _segments.Count == 0;

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
