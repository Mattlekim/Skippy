using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Skippy.Core.Diagnostics;

/// <summary>
/// A minimal 3×5 pixel font drawn with a 1×1 white texture.
///
/// The project ships no content assets (and the browser head skips the content
/// pipeline entirely), so there is no SpriteFont to use. Glyphs are bit patterns
/// stamped one pixel at a time — the same runtime-generated-art approach the world
/// renderer uses — so this works identically on every platform head. Digits and
/// upper-case letters are defined; anything else (lower case included) draws blank,
/// so keep HUD strings upper-case.
/// </summary>
public sealed class PixelFont : IDisposable
{
    public const int GlyphWidth = 3, GlyphHeight = 5;

    private readonly Texture2D _pixel;

    public PixelFont(GraphicsDevice gd)
    {
        _pixel = new Texture2D(gd, 1, 1);
        _pixel.SetData(new[] { Color.White });
    }

    /// <summary>Width in screen pixels of <paramref name="text"/> drawn at <paramref name="scale"/>.</summary>
    public static int Measure(string text, int scale)
        => text.Length == 0 ? 0 : text.Length * (GlyphWidth + 1) * scale - scale;

    public static int LineHeight(int scale) => GlyphHeight * scale;

    /// <summary>Draws <paramref name="text"/> over a one-cell drop shadow, so it reads on any background.</summary>
    public void DrawShadowed(SpriteBatch sb, string text, Vector2 at, int scale, Color color, Color shadow)
    {
        Draw(sb, text, at + new Vector2(scale), scale, shadow);
        Draw(sb, text, at, scale, color);
    }

    /// <summary>Fills a rectangle with the font's own white pixel — for panels and rules behind text.</summary>
    public void Fill(SpriteBatch sb, Rectangle rect, Color color)
        => sb.Draw(_pixel, rect, color);

    public void Draw(SpriteBatch sb, string text, Vector2 at, int scale, Color color)
    {
        var cell = new Vector2(scale);
        for (int i = 0; i < text.Length; i++)
        {
            byte[] rows = Glyph(text[i]);
            float gx = at.X + i * (GlyphWidth + 1) * scale;
            for (int row = 0; row < GlyphHeight; row++)
            {
                for (int col = 0; col < GlyphWidth; col++)
                {
                    if ((rows[row] & (0b100 >> col)) == 0) continue;
                    var pos = new Vector2(gx + col * scale, at.Y + row * scale);
                    sb.Draw(_pixel, pos, null, color, 0f, Vector2.Zero, cell, SpriteEffects.None, 0f);
                }
            }
        }
    }

    public void Dispose() => _pixel.Dispose();

    // 3-wide × 5-tall glyphs, one byte per row, most-significant bit = left column.
    private static byte[] Glyph(char c) => c switch
    {
        '0' => new byte[] { 0b111, 0b101, 0b101, 0b101, 0b111 },
        '1' => new byte[] { 0b010, 0b110, 0b010, 0b010, 0b111 },
        '2' => new byte[] { 0b111, 0b001, 0b111, 0b100, 0b111 },
        '3' => new byte[] { 0b111, 0b001, 0b111, 0b001, 0b111 },
        '4' => new byte[] { 0b101, 0b101, 0b111, 0b001, 0b001 },
        '5' => new byte[] { 0b111, 0b100, 0b111, 0b001, 0b111 },
        '6' => new byte[] { 0b111, 0b100, 0b111, 0b101, 0b111 },
        '7' => new byte[] { 0b111, 0b001, 0b001, 0b001, 0b001 },
        '8' => new byte[] { 0b111, 0b101, 0b111, 0b101, 0b111 },
        '9' => new byte[] { 0b111, 0b101, 0b111, 0b001, 0b111 },
        '-' => new byte[] { 0b000, 0b000, 0b111, 0b000, 0b000 },
        '.' => new byte[] { 0b000, 0b000, 0b000, 0b000, 0b010 },
        ':' => new byte[] { 0b000, 0b010, 0b000, 0b010, 0b000 },
        '<' => new byte[] { 0b001, 0b010, 0b100, 0b010, 0b001 },
        '>' => new byte[] { 0b100, 0b010, 0b001, 0b010, 0b100 },
        // Three columns is no room for two rings and a stroke, so it is read as the
        // stroke with a dot at each end of it — which at this size is what a percent
        // sign looks like anyway.
        '%' => new byte[] { 0b101, 0b001, 0b010, 0b100, 0b101 },
        'A' => new byte[] { 0b010, 0b101, 0b111, 0b101, 0b101 },
        'B' => new byte[] { 0b110, 0b101, 0b110, 0b101, 0b110 },
        'C' => new byte[] { 0b011, 0b100, 0b100, 0b100, 0b011 },
        'D' => new byte[] { 0b110, 0b101, 0b101, 0b101, 0b110 },
        'E' => new byte[] { 0b111, 0b100, 0b110, 0b100, 0b111 },
        'F' => new byte[] { 0b111, 0b100, 0b110, 0b100, 0b100 },
        'G' => new byte[] { 0b011, 0b100, 0b101, 0b101, 0b011 },
        'H' => new byte[] { 0b101, 0b101, 0b111, 0b101, 0b101 },
        'I' => new byte[] { 0b111, 0b010, 0b010, 0b010, 0b111 },
        'J' => new byte[] { 0b001, 0b001, 0b001, 0b101, 0b010 },
        'K' => new byte[] { 0b101, 0b101, 0b110, 0b101, 0b101 },
        'L' => new byte[] { 0b100, 0b100, 0b100, 0b100, 0b111 },
        'M' => new byte[] { 0b101, 0b111, 0b111, 0b101, 0b101 },
        'N' => new byte[] { 0b101, 0b111, 0b101, 0b101, 0b101 },
        'O' => new byte[] { 0b111, 0b101, 0b101, 0b101, 0b111 },
        'P' => new byte[] { 0b111, 0b101, 0b111, 0b100, 0b100 },
        'Q' => new byte[] { 0b111, 0b101, 0b101, 0b111, 0b001 },
        'R' => new byte[] { 0b110, 0b101, 0b110, 0b101, 0b101 },
        'S' => new byte[] { 0b111, 0b100, 0b111, 0b001, 0b111 },
        'T' => new byte[] { 0b111, 0b010, 0b010, 0b010, 0b010 },
        'U' => new byte[] { 0b101, 0b101, 0b101, 0b101, 0b111 },
        'V' => new byte[] { 0b101, 0b101, 0b101, 0b101, 0b010 },
        'W' => new byte[] { 0b101, 0b101, 0b111, 0b111, 0b101 },
        'X' => new byte[] { 0b101, 0b101, 0b010, 0b101, 0b101 },
        'Y' => new byte[] { 0b101, 0b101, 0b010, 0b010, 0b010 },
        'Z' => new byte[] { 0b111, 0b001, 0b010, 0b100, 0b111 },
        _   => new byte[] { 0, 0, 0, 0, 0 }, // space / unknown
    };
}
