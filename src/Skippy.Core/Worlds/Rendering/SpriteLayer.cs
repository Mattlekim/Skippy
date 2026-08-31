using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Skippy.Core.Worlds.Rendering;

/// <summary>
/// A handful of atlas sprites gathered and drawn again every single frame.
///
/// This is the counterpart to <see cref="MeshBuilder"/> + <see cref="ChunkMesh"/>, and
/// exists for the one thing they cannot carry: geometry that moves. A chunk's terrain
/// and plants are built once into static GPU buffers and then drawn for as long as the
/// chunk lives, which is exactly wrong for a mob — it is somewhere else next frame, so
/// its quad would have to be uploaded again, and a fresh <see cref="VertexBuffer"/> per
/// frame is pure churn.
///
/// So this keeps its vertices in plain arrays and hands them to the device directly.
/// The arrays are reused between frames, so a warmed-up layer allocates nothing at all;
/// it is only worth it for small counts (a flock, not a forest), which is precisely
/// what moves.
/// </summary>
public sealed class SpriteLayer
{
    private TerrainVertex[] _verts = new TerrainVertex[256];

    /// <summary>16-bit, for the widest platform support (WebGL included) — and signed, which is the only shape the user-primitives call takes.</summary>
    private short[] _indices = new short[384];
    private int _vertCount, _indexCount;

    /// <summary>Billboards face the screen, so like every other sprite they light as if facing straight up.</summary>
    private static readonly Vector3 Up = new(0f, 1f, 0f);

    public void Clear()
    {
        _vertCount = 0;
        _indexCount = 0;
    }

    public bool IsEmpty => _indexCount == 0;

    /// <summary>
    /// How much index data has been gathered so far. Taken either side of a sprite, this is
    /// the range to hand back to <see cref="Draw(GraphicsDevice, int, int)"/> to draw that
    /// one sprite on its own — which is how a mob gets drawn in among a chunk's plants
    /// rather than over all of them.
    /// </summary>
    public int IndexCount => _indexCount;

    /// <summary>
    /// A textured sprite quad, in iso space. <paramref name="origin"/> is the fraction of
    /// the quad (0..1) that sits at <paramref name="pos"/> — (0.5, 1) anchors the
    /// bottom-centre, the natural choice for something standing on the ground.
    ///
    /// <paramref name="flipX"/> mirrors the art, which is how a mob painted facing one way
    /// turns round. <paramref name="lean"/> goes into the otherwise unused z, for the same
    /// reason <see cref="MeshBuilder.ShadowSprite"/> does it: a shadow shears off it at
    /// draw time and swings round with the sun.
    /// </summary>
    public void Sprite(Vector2 pos, Vector2 size, Vector2 origin, in TextureAtlas.Region region,
                       Color color, bool flipX = false, float lean = 0f)
    {
        Vector2 tl = pos - size * origin;
        Vector2 br = tl + size;
        float u0 = flipX ? region.U1 : region.U0;
        float u1 = flipX ? region.U0 : region.U1;

        int b = Reserve(4, 6);
        _verts[b] = new TerrainVertex(new Vector3(tl.X, tl.Y, lean), color, Up, new Vector2(u0, region.V0));
        _verts[b + 1] = new TerrainVertex(new Vector3(br.X, tl.Y, lean), color, Up, new Vector2(u1, region.V0));
        _verts[b + 2] = new TerrainVertex(new Vector3(br.X, br.Y, lean), color, Up, new Vector2(u1, region.V1));
        _verts[b + 3] = new TerrainVertex(new Vector3(tl.X, br.Y, lean), color, Up, new Vector2(u0, region.V1));
        _indices[_indexCount++] = (short)b;
        _indices[_indexCount++] = (short)(b + 1);
        _indices[_indexCount++] = (short)(b + 2);
        _indices[_indexCount++] = (short)b;
        _indices[_indexCount++] = (short)(b + 2);
        _indices[_indexCount++] = (short)(b + 3);
    }

    /// <summary>
    /// Draws everything gathered since <see cref="Clear"/>. The caller has already bound the
    /// atlas and applied the effect pass, exactly as it does for a <see cref="ChunkMesh"/>.
    /// </summary>
    public void Draw(GraphicsDevice gd) => Draw(gd, 0, _indexCount);

    /// <summary>Draws one stretch of what was gathered — <paramref name="indexCount"/> indices from <paramref name="startIndex"/>.</summary>
    public void Draw(GraphicsDevice gd, int startIndex, int indexCount)
    {
        if (indexCount <= 0)
            return;

        gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, _verts, 0, _vertCount,
                                     _indices, startIndex, indexCount / 3);
    }

    /// <summary>
    /// Makes room for one quad and returns its base vertex. With 16-bit indices a layer
    /// holds at most ~8k sprites — far more than anything that moves will ever ask for.
    /// </summary>
    private int Reserve(int verts, int indices)
    {
        if (_vertCount + verts > short.MaxValue)
            throw new InvalidOperationException("SpriteLayer is full; it is meant for small, moving sets.");

        if (_vertCount + verts > _verts.Length) Array.Resize(ref _verts, _verts.Length * 2);
        if (_indexCount + indices > _indices.Length) Array.Resize(ref _indices, _indices.Length * 2);

        int b = _vertCount;
        _vertCount += verts;
        return b;
    }
}
