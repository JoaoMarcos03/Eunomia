using Avalonia.Media;

namespace Eunomia.UI.Controls.Grid;

/// <summary>
/// Largura aproximada de texto a partir dos avanços dos glifos (sem shaping nem kerning), para a largura
/// automática das colunas (secção 15.2). A tabela é construída uma vez na UI thread; depois
/// <see cref="Measure"/> é só aritmética e pode correr em background.
/// </summary>
public sealed class TextWidthEstimator
{
    private const int TableSize = 0x0250; // Latim básico, Latin-1 e Latin Extended-A/B.
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
