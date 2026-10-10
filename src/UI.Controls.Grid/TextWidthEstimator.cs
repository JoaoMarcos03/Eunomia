using Avalonia.Media;

namespace Eunomia.UI.Controls.Grid;

/// <summary>
/// Approximate text width from the glyph advances (no shaping or kerning), for automatic column
/// widths (section 15.2). The table is built once on the UI thread; after that
/// <see cref="Measure"/> is just arithmetic and can run in the background.
/// </summary>
public sealed class TextWidthEstimator
{
    private const int TableSize = 0x0250; // Basic Latin, Latin-1 and Latin Extended-A/B.
    private readonly float[] _advances = new float[TableSize];
    private readonly float _average;

    public TextWidthEstimator(Typeface typeface, double fontSize)
    {
        var glyphs = typeface.GlyphTypeface;
        var scale = fontSize / glyphs.Metrics.DesignEmHeight;
        double sum = 0;
        var count = 0;
        for (var c = 0x20; c < TableSize; c++)
        {
            if (glyphs.CharacterToGlyphMap.TryGetGlyph(c, out var glyph)
                && glyphs.TryGetHorizontalGlyphAdvance(glyph, out var advance))
            {
                _advances[c] = (float)(advance * scale);
                if (c is >= 'a' and <= 'z')
                {
                    sum += _advances[c];
                    count++;
                }
            }
        }
        _average = count > 0 ? (float)(sum / count) : (float)(fontSize * 0.55);
        for (var c = 0; c < TableSize; c++)
        {
            if (_advances[c] == 0)
            {
                _advances[c] = _average;
            }
        }
    }

    public double Measure(ReadOnlySpan<char> text)
    {
        double width = 0;
        foreach (var c in text)
        {
            width += c < TableSize ? _advances[c] : _average * 1.6;
        }
        return width;
    }
}
