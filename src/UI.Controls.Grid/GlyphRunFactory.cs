using System.Globalization;
using Avalonia;
using Avalonia.Media;

namespace Eunomia.UI.Controls.Grid;

/// <summary>
/// Fast text path of the grid (section 15.2: "glyph run cache", "zero allocations in paint"):
/// for plain text (only characters the font has, no combining marks, no RTL, no control characters),
/// creates the <see cref="GlyphRun"/> straight from the font's cmap and advances, without going through
/// HarfBuzz shaping. Cuts with an ellipsis when it does not fit. The rest goes through <c>TextLayout</c>.
/// </summary>
internal sealed class GlyphRunFactory
{
    private const char Ellipsis = '…';
    private readonly GlyphTypeface _glyphs;
    private readonly double _fontSize;
    private readonly double _scale;
    private readonly float[] _advances;
    private readonly ushort _ellipsisGlyph;
    private readonly double _ellipsisWidth;

    public GlyphRunFactory(Typeface typeface, double fontSize)
    {
        _glyphs = typeface.GlyphTypeface;
        _fontSize = fontSize;
        _scale = fontSize / _glyphs.Metrics.DesignEmHeight;
        _advances = new float[_glyphs.GlyphCount];
        Array.Fill(_advances, float.NaN);
        Ascent = Math.Abs(_glyphs.Metrics.Ascent) * _scale;
        LineHeight = (Math.Abs(_glyphs.Metrics.Ascent) + Math.Abs(_glyphs.Metrics.Descent)) * _scale;
        if (_glyphs.CharacterToGlyphMap.TryGetGlyph(Ellipsis, out _ellipsisGlyph))
        {
            _ellipsisWidth = Advance(_ellipsisGlyph);
        }
    }

    /// <summary>Distance from the top of the row to the baseline.</summary>
    public double Ascent { get; }

    public double LineHeight { get; }

    /// <summary>
    /// Creates the glyph run if the text is plain. <paramref name="offsetX"/> is the horizontal offset
    /// within the cell (right alignment).
    /// </summary>
    public bool TryCreate(string text, double maxWidth, bool alignRight, out GlyphRun? run, out double offsetX)
    {
        run = null;
        offsetX = 0;
        if (text.Length == 0 || text.Length > 4096 || _ellipsisWidth <= 0)
        {
            return false;
        }

        // Only measures up to what can fit (plus a margin), so it does not go through 256 characters.
        var glyphs = new ushort[Math.Min(text.Length, (int)(maxWidth / 1.5) + 2)];
        double width = 0;
        var count = 0;
        var fitsWithEllipsis = 0;
        var widthWithEllipsis = 0.0;
        var truncated = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (!IsSimple(c) || !_glyphs.CharacterToGlyphMap.TryGetGlyph(c, out var glyph))
            {
                return false;
            }
            var advance = Advance(glyph);
            if (width + advance + _ellipsisWidth <= maxWidth)
            {
                fitsWithEllipsis = count + 1;
                widthWithEllipsis = width + advance;
            }
            if (width + advance > maxWidth || count == glyphs.Length)
            {
                truncated = true;
                break;
            }
            glyphs[count++] = glyph;
            width += advance;
        }

        ReadOnlyMemory<char> characters;
        ushort[] indices;
        if (truncated)
        {
            // Checking that the rest of the text is also plain is not needed: only the start is drawn.
            indices = new ushort[fitsWithEllipsis + 1];
            Array.Copy(glyphs, indices, fitsWithEllipsis);
            indices[fitsWithEllipsis] = _ellipsisGlyph;
            characters = string.Concat(text.AsSpan(0, fitsWithEllipsis), "…").AsMemory();
            width = widthWithEllipsis + _ellipsisWidth;
        }
        else
        {
            indices = count == glyphs.Length ? glyphs : glyphs[..count];
            characters = text.AsMemory();
        }

        offsetX = alignRight ? Math.Max(0, maxWidth - width) : 0;
        run = new GlyphRun(_glyphs, _fontSize, characters, indices, new Point(0, 0), 0);
        return true;
    }

    private double Advance(ushort glyph)
    {
        var cached = _advances[glyph];
        if (float.IsNaN(cached))
        {
            cached = _glyphs.TryGetHorizontalGlyphAdvance(glyph, out var advance) ? (float)(advance * _scale) : 0f;
            _advances[glyph] = cached;
        }
        return cached;
    }

    private static bool IsSimple(char c)
    {
        if (c < 0x20 || char.IsSurrogate(c) || (c >= 0x0590 && c <= 0x08FF) || (c >= 0xFB1D && c <= 0xFEFF))
        {
            return false;
        }
        if (c < 0x0300)
        {
            return true;
        }
        var category = CharUnicodeInfo.GetUnicodeCategory(c);
        return category is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark or UnicodeCategory.Format or UnicodeCategory.Control);
    }
}
