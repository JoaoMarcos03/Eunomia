using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace Eunomia.UI.Controls.Grid;

/// <summary>
/// Cache de texto já preparado por célula (secção 15.1, Renderer: "cache de glyph runs"), para o paint
/// não repetir trabalho em células que já estavam visíveis. Texto simples vira um <see cref="GlyphRun"/>
/// sem shaping (<see cref="GlyphRunFactory"/>), desenhado com qualquer pincel; o resto usa
/// <see cref="TextLayout"/> com shaping completo (fallback de fontes, RTL, marcas combinadas).
/// Entradas não usadas há alguns frames saem quando a cache passa da capacidade. Só na UI thread.
/// </summary>
internal sealed class CellTextCache
{
    private readonly Dictionary<long, Entry> _entries = [];
    private readonly List<long> _evict = [];
    private int _frame;

    public CellTextCache(int capacity) => Capacity = capacity;

    public int Capacity { get; }

    public int Count => _entries.Count;

    /// <summary>Entradas criadas (para benchmarks).</summary>
    public long Created { get; private set; }

    /// <summary>Entradas que precisaram de <see cref="TextLayout"/> (shaping completo).</summary>
    public long CreatedWithShaping { get; private set; }

    /// <summary>Desenha o texto da célula com o topo da linha em <paramref name="origin"/>.</summary>
    public void Draw(
        DrawingContext context,
        long row,
        int column,
        ReadOnlySpan<char> text,
        GlyphRunFactory glyphs,
        Typeface typeface,
        double fontSize,
        IBrush brush,
        bool alignRight,
        double maxWidth,
        Point origin,
        double rowHeight)
    {
        var entry = Get(row, column, text, glyphs, typeface, fontSize, brush, alignRight, maxWidth);
        if (entry.Run is { } run)
        {
            var top = origin.Y + ((rowHeight - glyphs.LineHeight) / 2);
            using (context.PushTransform(Matrix.CreateTranslation(origin.X + entry.OffsetX, Math.Round(top + glyphs.Ascent))))
            {
                context.DrawGlyphRun(brush, run);
            }
        }
        else
        {
            var layout = entry.Layout!;
            layout.Draw(context, new Point(origin.X, origin.Y + ((rowHeight - layout.Height) / 2)));
        }
    }

    private Entry Get(long row, int column, ReadOnlySpan<char> span, GlyphRunFactory glyphs, Typeface typeface, double fontSize, IBrush brush, bool alignRight, double maxWidth)
    {
        var key = (row << 12) | (uint)column;
        if (_entries.TryGetValue(key, out var entry)
            && entry.Text.AsSpan().SequenceEqual(span)
            && entry.MaxWidth == maxWidth
            && (entry.Run is not null || ReferenceEquals(entry.Brush, brush)))
        {
            entry.Frame = _frame;
            return entry;
        }
        entry?.Dispose();

        // Só aloca a string quando a célula não está em cache.
        var text = span.ToString();
        if (glyphs.TryCreate(text, maxWidth, alignRight, out var run, out var offsetX))
        {
            entry = new Entry(text, null, maxWidth) { Run = run, OffsetX = offsetX };
        }
        else
        {
            // Texto muito maior do que pode caber é cortado antes do shaping (a elipse continua a aparecer).
            var maxChars = (int)(maxWidth / 2.5) + 4;
            var shaped = text.Length > maxChars ? text[..SafeCut(text, maxChars)] + "…" : text;
            entry = new Entry(text, brush, maxWidth)
            {
                Layout = new TextLayout(
                    shaped,
                    typeface,
                    fontSize,
                    brush,
                    textAlignment: alignRight ? TextAlignment.Right : TextAlignment.Left,
                    textWrapping: TextWrapping.NoWrap,
                    textTrimming: TextTrimming.CharacterEllipsis,
                    maxWidth: Math.Max(1, maxWidth),
                    maxLines: 1),
            };
            CreatedWithShaping++;
        }
        entry.Frame = _frame;
        _entries[key] = entry;
        Created++;
        return entry;
    }

    /// <summary>Fim de um frame: despeja entradas antigas se a cache estiver acima da capacidade.</summary>
    public void EndFrame()
    {
        _frame++;
        if (_entries.Count <= Capacity)
        {
            return;
        }
        var minFrame = _frame - 2;
        _evict.Clear();
        foreach (var (key, entry) in _entries)
        {
            if (entry.Frame < minFrame)
            {
                _evict.Add(key);
            }
        }
        foreach (var key in _evict)
        {
            _entries[key].Dispose();
            _entries.Remove(key);
        }
    }

    public void Clear()
    {
        foreach (var entry in _entries.Values)
        {
            entry.Dispose();
        }
        _entries.Clear();
    }

    private static int SafeCut(string text, int length) =>
        char.IsHighSurrogate(text[length - 1]) ? length - 1 : length;

    private sealed class Entry(string text, IBrush? brush, double maxWidth)
    {
        public string Text { get; } = text;

        /// <summary>Pincel cozido no layout (só no caminho com shaping).</summary>
        public IBrush? Brush { get; } = brush;

        public double MaxWidth { get; } = maxWidth;

        public GlyphRun? Run { get; init; }

        public double OffsetX { get; init; }

        public TextLayout? Layout { get; init; }

        public int Frame { get; set; }

        public void Dispose()
        {
            Run?.Dispose();
            Layout?.Dispose();
        }
    }
}
