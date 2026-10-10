using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace Eunomia.UI.Controls.Grid;

/// <summary>
/// Cache of text already prepared per cell (section 15.1, Renderer: "glyph run cache"), so painting does not
/// repeat work for cells that were already visible. Plain text becomes a <see cref="GlyphRun"/>
/// without shaping (<see cref="GlyphRunFactory"/>), drawn with any brush; the rest uses
/// <see cref="TextLayout"/> with full shaping (font fallback, RTL, combining marks).
/// Entries unused for a few frames are dropped when the cache goes over capacity. UI thread only.
/// </summary>
internal sealed class CellTextCache
{
    private readonly Dictionary<long, Entry> _entries = [];
    private readonly List<long> _evict = [];
    private int _frame;

    public CellTextCache(int capacity) => Capacity = capacity;

    public int Capacity { get; }

    public int Count => _entries.Count;

    /// <summary>Entries created (for benchmarks).</summary>
    public long Created { get; private set; }

    /// <summary>Entries that needed a <see cref="TextLayout"/> (full shaping).</summary>
    public long CreatedWithShaping { get; private set; }

    /// <summary>Draws the cell text with the top of the row at <paramref name="origin"/>.</summary>
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

        // Only allocates the string when the cell is not cached.
        var text = span.ToString();
        if (glyphs.TryCreate(text, maxWidth, alignRight, out var run, out var offsetX))
        {
            entry = new Entry(text, null, maxWidth) { Run = run, OffsetX = offsetX };
        }
        else
        {
            // Text far larger than can fit is cut before shaping (the ellipsis still shows).
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

    /// <summary>End of a frame: evicts old entries if the cache is over capacity.</summary>
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

        /// <summary>Brush baked into the layout (only on the shaping path).</summary>
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
