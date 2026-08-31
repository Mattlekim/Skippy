using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Skippy.Core.Worlds.Rendering;

/// <summary>
/// CPU-side accumulator for textured, vertex-coloured triangles. Geometry is
/// appended in iso-space, then <see cref="Upload"/> hands back a
/// <see cref="ChunkMesh"/> whose data lives in static GPU buffers.
///
/// One instance is reused for every chunk build, so a warmed-up builder does no
/// per-build allocation beyond the GPU buffers themselves. Indices are 16-bit
/// for the widest platform support (WebGL included); if a chunk needs more than
/// ~65k vertices (a dense forest) the geometry is transparently split into
/// several segments that draw back to back.
/// </summary>
public sealed class MeshBuilder
{
    private const int MaxVertsPerSegment = 65532; // multiple of 4, under ushort.MaxValue

    /// <summary>Sprites and lines are flat, screen-facing billboards; they light as if facing straight up.</summary>
    private static readonly Vector3 Up = new(0f, 1f, 0f);

    private readonly GraphicsDevice _gd;
    private TerrainVertex[] _verts = new TerrainVertex[16384];
    private ushort[] _indices = new ushort[24576];
    private int _vertCount, _indexCount;

    private readonly List<ChunkMesh.Segment> _segments = new();
    private Vector2 _min, _max;

    public MeshBuilder(GraphicsDevice gd) => _gd = gd;

    public void Clear()
    {
        _vertCount = 0;
        _indexCount = 0;
        _segments.Clear();
        _min = new Vector2(float.MaxValue);
        _max = new Vector2(float.MinValue);
    }

    /// <summary>
    /// A triangle with per-vertex colour and per-vertex world-space normal (so a
    /// sloped face lights by its own tilt). All three vertices sample <paramref name="uv"/>.
    /// </summary>
    public void Triangle(Vector2 p0, Color c0, Vector3 n0,
                         Vector2 p1, Color c1, Vector3 n1,
                         Vector2 p2, Color c2, Vector3 n2, Vector2 uv)
    {
        int b = Reserve(3, 3);
        _verts[b] = new TerrainVertex(new Vector3(p0, 0f), c0, n0, uv);
        _verts[b + 1] = new TerrainVertex(new Vector3(p1, 0f), c1, n1, uv);
        _verts[b + 2] = new TerrainVertex(new Vector3(p2, 0f), c2, n2, uv);
        _indices[_indexCount++] = (ushort)b;
        _indices[_indexCount++] = (ushort)(b + 1);
        _indices[_indexCount++] = (ushort)(b + 2);
        Grow(p0); Grow(p1); Grow(p2);
    }

    /// <summary>
    /// A triangle with per-vertex colour, normal AND texture coordinate — for textured
    /// ground, where each corner samples a different point of the tile's ground texture.
    /// </summary>
    public void Triangle(Vector2 p0, Color c0, Vector3 n0, Vector2 uv0,
                         Vector2 p1, Color c1, Vector3 n1, Vector2 uv1,
                         Vector2 p2, Color c2, Vector3 n2, Vector2 uv2)
    {
        int b = Reserve(3, 3);
        _verts[b] = new TerrainVertex(new Vector3(p0, 0f), c0, n0, uv0);
        _verts[b + 1] = new TerrainVertex(new Vector3(p1, 0f), c1, n1, uv1);
        _verts[b + 2] = new TerrainVertex(new Vector3(p2, 0f), c2, n2, uv2);
        _indices[_indexCount++] = (ushort)b;
        _indices[_indexCount++] = (ushort)(b + 1);
        _indices[_indexCount++] = (ushort)(b + 2);
        Grow(p0); Grow(p1); Grow(p2);
    }

    /// <summary>
    /// A textured sprite quad. <paramref name="origin"/> is the fraction of the
    /// quad (0..1) that sits at <paramref name="pos"/> — (0.5, 1) anchors the
    /// bottom-centre, the natural choice for plants standing on the ground.
    /// </summary>
    public void Sprite(Vector2 pos, Vector2 size, Vector2 origin, in TextureAtlas.Region region, Color color)
    {
        Vector2 tl = pos - size * origin;
        Vector2 br = tl + size;
        int b = Reserve(4, 6);
        _verts[b] = new TerrainVertex(new Vector3(tl.X, tl.Y, 0f), color, Up, new Vector2(region.U0, region.V0));
        _verts[b + 1] = new TerrainVertex(new Vector3(br.X, tl.Y, 0f), color, Up, new Vector2(region.U1, region.V0));
        _verts[b + 2] = new TerrainVertex(new Vector3(br.X, br.Y, 0f), color, Up, new Vector2(region.U1, region.V1));
        _verts[b + 3] = new TerrainVertex(new Vector3(tl.X, br.Y, 0f), color, Up, new Vector2(region.U0, region.V1));
        _indices[_indexCount++] = (ushort)b;
        _indices[_indexCount++] = (ushort)(b + 1);
        _indices[_indexCount++] = (ushort)(b + 2);
        _indices[_indexCount++] = (ushort)b;
        _indices[_indexCount++] = (ushort)(b + 2);
        _indices[_indexCount++] = (ushort)(b + 3);
        Grow(tl); Grow(br);
    }

    /// <summary>
    /// A sprite that leans with the sun: the same quad as <see cref="Sprite"/>, but with
    /// <paramref name="lean"/> written into the vertex's otherwise unused z.
    ///
    /// Nothing else in the mesh uses z — everything is drawn flat — so the renderer can put
    /// a shear in the world matrix that turns z into an offset across the ground and moves
    /// *only* these vertices. That is what lets a plant's shadow swing round its foot as
    /// the sun crosses the sky without a single vertex being rebuilt; see
    /// <c>WorldRenderer.SunShear</c>. The shear flattens z back to 0 on the way through, so
    /// these vertices still land in the orthographic projection's depth range.
    /// </summary>
    public void ShadowSprite(Vector2 pos, Vector2 size, Vector2 origin, in TextureAtlas.Region region, Color color, float lean)
    {
        Vector2 tl = pos - size * origin;
        Vector2 br = tl + size;
        int b = Reserve(4, 6);
        _verts[b] = new TerrainVertex(new Vector3(tl.X, tl.Y, lean), color, Up, new Vector2(region.U0, region.V0));
        _verts[b + 1] = new TerrainVertex(new Vector3(br.X, tl.Y, lean), color, Up, new Vector2(region.U1, region.V0));
        _verts[b + 2] = new TerrainVertex(new Vector3(br.X, br.Y, lean), color, Up, new Vector2(region.U1, region.V1));
        _verts[b + 3] = new TerrainVertex(new Vector3(tl.X, br.Y, lean), color, Up, new Vector2(region.U0, region.V1));
        _indices[_indexCount++] = (ushort)b;
        _indices[_indexCount++] = (ushort)(b + 1);
        _indices[_indexCount++] = (ushort)(b + 2);
        _indices[_indexCount++] = (ushort)b;
        _indices[_indexCount++] = (ushort)(b + 2);
        _indices[_indexCount++] = (ushort)(b + 3);
        Grow(tl); Grow(br);
    }

    /// <summary>
    /// Widens the bounds this mesh reports, for geometry that moves after it is built: a
    /// shadow swung out by a low sun still has to count as visible, or its chunk is culled
    /// while part of it is on screen.
    /// </summary>
    public void PadBounds(float amount)
    {
        _min -= new Vector2(amount);
        _max += new Vector2(amount);
    }

    /// <summary>
    /// A flat-capped line of <paramref name="width"/> iso pixels from <paramref name="a"/>
    /// to <paramref name="b"/>, emitted as one quad. Used by the tile-grid overlay.
    /// </summary>
    public void Line(Vector2 a, Vector2 b, float width, Color color, Vector2 uv)
    {
        Vector2 d = b - a;
        float len = d.Length();
        if (len < 1e-4f) return;

        Vector2 n = new Vector2(-d.Y, d.X) * (width * 0.5f / len);
        int i = Reserve(4, 6);
        _verts[i] = new TerrainVertex(new Vector3(a + n, 0f), color, Up, uv);
        _verts[i + 1] = new TerrainVertex(new Vector3(b + n, 0f), color, Up, uv);
        _verts[i + 2] = new TerrainVertex(new Vector3(b - n, 0f), color, Up, uv);
        _verts[i + 3] = new TerrainVertex(new Vector3(a - n, 0f), color, Up, uv);
        _indices[_indexCount++] = (ushort)i;
        _indices[_indexCount++] = (ushort)(i + 1);
        _indices[_indexCount++] = (ushort)(i + 2);
        _indices[_indexCount++] = (ushort)i;
        _indices[_indexCount++] = (ushort)(i + 2);
        _indices[_indexCount++] = (ushort)(i + 3);
        Grow(a); Grow(b);
    }

    /// <summary>
    /// Where the next primitive will land. Recorded before each sprite is appended, so the
    /// finished mesh can be drawn in slices and something that moves can be drawn among its
    /// sprites — see <see cref="ChunkMesh.Slice"/>.
    /// </summary>
    public ChunkMesh.Cursor Mark() => new(_segments.Count, _indexCount);

    /// <summary>Copies everything accumulated since <see cref="Clear"/> into GPU buffers.</summary>
    public ChunkMesh Upload(byte neighborMask, List<ChunkMesh.Slice>? slices = null)
    {
        FlushSegment();
        bool empty = _segments.Count == 0;
        var mesh = new ChunkMesh(new List<ChunkMesh.Segment>(_segments),
                                 empty ? Vector2.Zero : _min,
                                 empty ? Vector2.Zero : _max,
                                 neighborMask,
                                 slices is null ? null : new List<ChunkMesh.Slice>(slices));
        _segments.Clear(); // ownership of the buffers has moved to the mesh
        return mesh;
    }

    // ---- internals ---------------------------------------------------------

    /// <summary>Makes room for one primitive, starting a new segment if needed, and returns its base vertex.</summary>
    private int Reserve(int verts, int indices)
    {
        if (_vertCount + verts > MaxVertsPerSegment)
            FlushSegment();
        if (_vertCount + verts > _verts.Length) Array.Resize(ref _verts, _verts.Length * 2);
        if (_indexCount + indices > _indices.Length) Array.Resize(ref _indices, _indices.Length * 2);
        int b = _vertCount;
        _vertCount += verts;
        return b;
    }

    private void FlushSegment()
    {
        if (_vertCount == 0) return;

        var vb = new VertexBuffer(_gd, TerrainVertex.VertexDeclaration, _vertCount, BufferUsage.WriteOnly);
        vb.SetData(_verts, 0, _vertCount);
        var ib = new IndexBuffer(_gd, IndexElementSize.SixteenBits, _indexCount, BufferUsage.WriteOnly);
        ib.SetData(_indices, 0, _indexCount);

        _segments.Add(new ChunkMesh.Segment(vb, ib, _indexCount / 3));
        _vertCount = 0;
        _indexCount = 0;
    }

    private void Grow(Vector2 p)
    {
        if (p.X < _min.X) _min.X = p.X;
        if (p.Y < _min.Y) _min.Y = p.Y;
        if (p.X > _max.X) _max.X = p.X;
        if (p.Y > _max.Y) _max.Y = p.Y;
    }
}
