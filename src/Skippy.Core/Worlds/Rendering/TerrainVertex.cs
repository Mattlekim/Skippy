using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Skippy.Core.Worlds.Rendering;

/// <summary>
/// One mesh vertex: an iso-space <see cref="Position"/>, a base <see cref="Color"/>,
/// a world-space surface <see cref="Normal"/> for the sun to light, and an atlas
/// <see cref="TextureCoordinate"/>.
///
/// The position is the flattened isometric projection the whole renderer works in,
/// but the normal is a genuine 3-D direction in a world frame (x = east, y = up,
/// z = south). A directional light only needs the normal and the light direction to
/// live in the same frame to compute N·L — the vertex's screen position is
/// irrelevant to that — so lighting comes out correct even though we never build a
/// real 3-D scene. That is what lets the sun move and the terrain light up without
/// abandoning the iso projection (and its picking, culling and HUD).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct TerrainVertex : IVertexType
{
    public Vector3 Position;
    public Color Color;
    public Vector3 Normal;
    public Vector2 TextureCoordinate;

    public TerrainVertex(Vector3 position, Color color, Vector3 normal, Vector2 textureCoordinate)
    {
        Position = position;
        Color = color;
        Normal = normal;
        TextureCoordinate = textureCoordinate;
    }

    public static readonly VertexDeclaration VertexDeclaration = new(
        new VertexElement(0, VertexElementFormat.Vector3, VertexElementUsage.Position, 0),
        new VertexElement(12, VertexElementFormat.Color, VertexElementUsage.Color, 0),
        new VertexElement(16, VertexElementFormat.Vector3, VertexElementUsage.Normal, 0),
        new VertexElement(28, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0));

    readonly VertexDeclaration IVertexType.VertexDeclaration => VertexDeclaration;
}
