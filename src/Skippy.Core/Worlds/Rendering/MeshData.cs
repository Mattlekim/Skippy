using System;
using System.Buffers;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Skippy.Core.Worlds.Rendering;

/// <summary>
/// A finished mesh that has not touched the graphics device yet: the vertices and
/// indices, in plain arrays, with the bounds and slices worked out.
///
/// <para>
/// This is the hand-off between the two halves of building a chunk's geometry. Working
/// out where every vertex goes — the terrain subdivision, the coast, the blended ground
/// textures, the scattered plants and their shadows — is arithmetic over tiles, and it
/// is the expensive half by a long way (measured at ~87% of the build). None of it needs
/// a graphics device, so it is done on a worker thread and lands here. Only
/// <see cref="Upload"/> touches the device, and that has to happen on the thread that
/// owns it.
/// </para>
/// <para>
/// The arrays are rented from <see cref="ArrayPool{T}"/> and handed back the moment the
/// data is on the GPU, because a dense chunk's vertices run to megabytes and allocating
/// that per chunk would move the stutter from the mesher into the garbage collector.
/// That makes ownership real: exactly one of <see cref="Upload"/> or
/// <see cref="Discard"/> must be called on every instance, and the instance is spent
/// afterwards.
/// </para>
/// </summary>
public sealed class MeshData
{
    /// <summary>
    /// One segment's geometry. The arrays are rented and so are usually longer than the
    /// data in them, which is why the counts are carried separately — everything that
    /// reads these must go by the count, never by <c>Length</c>.
    /// </summary>
    public readonly struct Segment
    {
        public readonly TerrainVertex[] Vertices;
        public readonly int VertexCount;
        public readonly ushort[] Indices;
        public readonly int IndexCount;

        public Segment(TerrainVertex[] vertices, int vertexCount, ushort[] indices, int indexCount)
        {
            Vertices = vertices;
            VertexCount = vertexCount;
            Indices = indices;
            IndexCount = indexCount;
        }
    }

    private readonly List<Segment> _segments;
    private readonly List<ChunkMesh.Slice>? _slices;

    public readonly Vector2 BoundsMin, BoundsMax;
    public readonly byte NeighborMask;

    /// <summary>Set once the rented arrays have gone back, so a second hand-back can't happen.</summary>
    private bool _spent;

    public MeshData(List<Segment> segments, Vector2 boundsMin, Vector2 boundsMax, byte neighborMask,
                    List<ChunkMesh.Slice>? slices)
    {
        _segments = segments;
        BoundsMin = boundsMin;
        BoundsMax = boundsMax;
        NeighborMask = neighborMask;
        _slices = slices;
    }

    /// <summary>
    /// The geometry itself, for anything that wants to read it before it goes to the GPU.
    /// Reading does not spend the data; uploading or discarding it does.
    /// </summary>
    public IReadOnlyList<Segment> Segments => _segments;

    /// <summary>Roughly how much GPU memory this will take, for budgeting the uploads.</summary>
    public int VertexCount
    {
        get
        {
            int n = 0;
            foreach (Segment s in _segments)
                n += s.VertexCount;
            return n;
        }
    }

    /// <summary>
    /// Copies the geometry into GPU buffers and hands back the drawable mesh. **Main
    /// thread only** — this is the one part of building a chunk that has to be, because
    /// the buffers belong to the thread that owns the device.
    /// </summary>
    public ChunkMesh Upload(GraphicsDevice gd)
    {
        if (_spent)
            throw new InvalidOperationException("This MeshData has already been uploaded or discarded.");

        var uploaded = new List<ChunkMesh.Segment>(_segments.Count);
        foreach (Segment s in _segments)
        {
            var vb = new VertexBuffer(gd, TerrainVertex.VertexDeclaration, s.VertexCount, BufferUsage.WriteOnly);
            vb.SetData(s.Vertices, 0, s.VertexCount);
            var ib = new IndexBuffer(gd, IndexElementSize.SixteenBits, s.IndexCount, BufferUsage.WriteOnly);
            ib.SetData(s.Indices, 0, s.IndexCount);
            uploaded.Add(new ChunkMesh.Segment(vb, ib, s.IndexCount / 3));
        }

        Release();
        return new ChunkMesh(uploaded, BoundsMin, BoundsMax, NeighborMask, _slices);
    }

    /// <summary>
    /// Throws the geometry away without uploading it — for a mesh that was overtaken
    /// while it was being built (the tiles resized under it, the world was regenerated,
    /// the chunk unloaded). The arrays must still go back to the pool.
    /// </summary>
    public void Discard() => Release();

    private void Release()
    {
        if (_spent)
            return;

        _spent = true;
        foreach (Segment s in _segments)
        {
            ArrayPool<TerrainVertex>.Shared.Return(s.Vertices);
            ArrayPool<ushort>.Shared.Return(s.Indices);
        }
        _segments.Clear();
    }
}
