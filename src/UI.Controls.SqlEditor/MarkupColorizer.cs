using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace Eunomia.UI.Controls.SqlEditor;

/// <summary>
/// XML and HTML highlighting in the cell editor, with the SQL editor colours (theme tokens): tag names as keywords,
/// attributes as identifiers, values as strings, comments, entities as numbers and declarations (<c>&lt;?xml</c>,
/// <c>&lt;!DOCTYPE</c>, CDATA) as data types. Comments, CDATA and tags can continue on the next line: the state at the end
/// of each line is cached and invalidated from the changed line on. Lines longer than
/// <see cref="TextValueEditor.MaxHighlightedLineLength"/> stay plain (they only count for the state).
/// </summary>
public sealed class MarkupColorizer : DocumentColorizingTransformer
{
    private readonly List<State> _endStates = [];
    private readonly List<(int Start, int End, Kind Kind)> _spans = [];
    private int _validLines;

    private enum State
    {
        Text,
        Tag,
        AttributeDouble,
        AttributeSingle,
        Comment,
        CData,
        Instruction,
        Declaration,
    }

    private enum Kind
    {
        TagName,
        Attribute,
        Value,
        Comment,
        Entity,
        Declaration,
    }

    public SqlEditorPalette Palette { get; set; } = SqlEditorPalette.Light;

    /// <summary>Invalidates the state from the (1-based) line that changed.</summary>
    public void InvalidateFrom(int lineNumber) => _validLines = Math.Min(_validLines, Math.Max(0, lineNumber - 1));

    protected override void ColorizeLine(DocumentLine line)
    {
        if (line.Length == 0 || line.Length > TextValueEditor.MaxHighlightedLineLength)
        {
            return;
        }
        var document = CurrentContext.Document;
        var state = StateBefore(document, line.LineNumber);
        _spans.Clear();
        Scan(document.GetText(line.Offset, line.Length), state, _spans);
        foreach (var (start, end, kind) in _spans)
        {
            if (end > start)
            {
                var brush = BrushFor(kind);
                ChangeLinePart(line.Offset + start, line.Offset + end, e => e.TextRunProperties.SetForegroundBrush(brush));
            }
        }
    }

    private IBrush BrushFor(Kind kind) => kind switch
    {
        Kind.TagName => Palette.Keyword,
        Kind.Attribute => Palette.Identifier,
        Kind.Value => Palette.String,
        Kind.Comment => Palette.Comment,
        Kind.Entity => Palette.Number,
        _ => Palette.DataType,
    };

    private State StateBefore(TextDocument document, int lineNumber)
    {
        if (lineNumber <= 1)
        {
            return State.Text;
        }
        while (_endStates.Count < document.LineCount)
        {
            _endStates.Add(State.Text);
        }
        var state = _validLines == 0 ? State.Text : _endStates[_validLines - 1];
        for (var n = _validLines + 1; n < lineNumber; n++)
        {
            var l = document.GetLineByNumber(n);
            state = Scan(document.GetText(l.Offset, l.Length), state, null);
            _endStates[n - 1] = state;
            _validLines = n;
        }
        return _endStates[lineNumber - 2];
    }

    /// <summary>Scans a line from <paramref name="state"/>; collects the parts (when asked) and returns the state at its end.</summary>
    private static State Scan(string text, State state, List<(int, int, Kind)>? spans)
    {
        var i = 0;
        while (i < text.Length)
        {
            switch (state)
            {
                case State.Comment:
                    i = Until(text, i, "-->", Kind.Comment, spans, ref state);
                    break;
                case State.CData:
                    i = Until(text, i, "]]>", Kind.Declaration, spans, ref state);
                    break;
                case State.Instruction:
                    i = Until(text, i, "?>", Kind.Declaration, spans, ref state);
                    break;
                case State.Declaration:
                    i = Until(text, i, ">", Kind.Declaration, spans, ref state);
                    break;
                case State.AttributeDouble:
                case State.AttributeSingle:
                    var quote = state == State.AttributeDouble ? '"' : '\'';
                    var close = text.IndexOf(quote, i);
                    var valueEnd = close < 0 ? text.Length : close + 1;
                    spans?.Add((i, valueEnd, Kind.Value));
                    state = close < 0 ? state : State.Tag;
                    i = valueEnd;
                    break;
                case State.Tag:
                    i = ScanTag(text, i, spans, ref state);
                    break;
                default:
                    i = ScanText(text, i, spans, ref state);
                    break;
            }
        }
        return state;
    }

    /// <summary>Text up to the end of the block (<paramref name="end"/> included), which goes back to the text state.</summary>
    private static int Until(string text, int i, string end, Kind kind, List<(int, int, Kind)>? spans, ref State state)
    {
        var found = text.IndexOf(end, i, StringComparison.Ordinal);
        var stop = found < 0 ? text.Length : found + end.Length;
        spans?.Add((i, stop, kind));
        if (found >= 0)
        {
            state = State.Text;
        }
        return stop;
    }

    private static int ScanText(string text, int i, List<(int, int, Kind)>? spans, ref State state)
    {
        var c = text[i];
        if (c == '&')
        {
            var semicolon = text.IndexOf(';', i);
            if (semicolon > i && semicolon - i <= 32)
            {
                spans?.Add((i, semicolon + 1, Kind.Entity));
                return semicolon + 1;
            }
            return i + 1;
        }
        if (c != '<')
        {
            var next = text.IndexOfAny(['<', '&'], i);
            return next < 0 ? text.Length : next;
        }
        var rest = text.AsSpan(i);
        if (rest.StartsWith("<!--", StringComparison.Ordinal))
        {
            state = State.Comment;
            return Until(text, i, "-->", Kind.Comment, spans, ref state);
        }
        if (rest.StartsWith("<![CDATA[", StringComparison.Ordinal))
        {
            state = State.CData;
            return Until(text, i, "]]>", Kind.Declaration, spans, ref state);
        }
        if (rest.StartsWith("<?", StringComparison.Ordinal))
        {
            state = State.Instruction;
            return Until(text, i, "?>", Kind.Declaration, spans, ref state);
        }
        if (rest.StartsWith("<!", StringComparison.Ordinal))
        {
            state = State.Declaration;
            return Until(text, i, ">", Kind.Declaration, spans, ref state);
        }
        var end = i + (rest.StartsWith("</", StringComparison.Ordinal) ? 2 : 1);
        while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] is not ('>' or '/'))
        {
            end++;
        }
        spans?.Add((i, end, Kind.TagName));
        state = State.Tag;
        return end;
    }

    private static int ScanTag(string text, int i, List<(int, int, Kind)>? spans, ref State state)
    {
        var c = text[i];
        if (char.IsWhiteSpace(c) || c == '=')
        {
            return i + 1;
        }
        if (c == '>' || (c == '/' && i + 1 < text.Length && text[i + 1] == '>'))
        {
            var end = c == '>' ? i + 1 : i + 2;
            spans?.Add((i, end, Kind.TagName));
            state = State.Text;
            return end;
        }
        if (c is '"' or '\'')
        {
            state = c == '"' ? State.AttributeDouble : State.AttributeSingle;
            spans?.Add((i, i + 1, Kind.Value));
            return i + 1;
        }
        var nameEnd = i + 1;
        while (nameEnd < text.Length && !char.IsWhiteSpace(text[nameEnd]) && text[nameEnd] is not ('=' or '>' or '/' or '"' or '\''))
        {
            nameEnd++;
        }
        spans?.Add((i, nameEnd, Kind.Attribute));
        return nameEnd;
    }
}
