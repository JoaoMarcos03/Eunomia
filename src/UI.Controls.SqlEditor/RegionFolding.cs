using AvaloniaEdit.Document;
using Eunomia.Engine.Sql.Lexing;

namespace Eunomia.UI.Controls.SqlEditor;

/// <summary>
/// Code folding regions (Query ▸ Code folding; spec section 6: only by <c>#region</c>/<c>#endregion</c> markers,
/// which are comments in MySQL and do not change the SQL): a line is a marker when it starts, after whitespace, with
/// <c>#region</c> or <c>#endregion</c> and is not inside a string or a block comment. Pairs are matched with a stack
/// (regions can be nested); a marker without a pair is left out.
/// </summary>
internal static class RegionFolding
{
    public const string Start = "#region";
    public const string End = "#endregion";

    /// <summary>A region: from the <c>#</c> of the <c>#region</c> to the end of the <c>#endregion</c> line; the name is the <c>#region</c> line.</summary>
    public readonly record struct Region(int StartOffset, int EndOffset, string Name);

    /// <param name="stateAt">Lexer state at the start of a line (1-based).</param>
    public static List<Region> Find(TextDocument document, Func<int, LexerState> stateAt)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(stateAt);
        var regions = new List<Region>();
        var open = new Stack<(int Offset, string Name)>();
        foreach (var line in document.Lines)
        {
            var text = document.GetTextAsMemory(line.Offset, line.Length).Span;
            var indent = text.Length - text.TrimStart().Length;
            var trimmed = text[indent..].TrimEnd();
            var isStart = IsMarker(trimmed, Start);
            var isEnd = !isStart && IsMarker(trimmed, End);
            if ((!isStart && !isEnd) || stateAt(line.LineNumber) != LexerState.Normal)
            {
                continue;
            }
            if (isStart)
            {
                open.Push((line.Offset + indent, trimmed.ToString()));
            }
            else if (open.Count > 0)
            {
                var (offset, name) = open.Pop();
                regions.Add(new Region(offset, line.Offset + line.Length, name));
            }
        }
        regions.Sort((a, b) => a.StartOffset.CompareTo(b.StartOffset));
        return regions;
    }

    /// <summary>The line is the marker, alone or followed by whitespace and a name (<c>#regional</c> does not count).</summary>
    private static bool IsMarker(ReadOnlySpan<char> line, string marker) =>
        line.StartsWith(marker, StringComparison.OrdinalIgnoreCase) && (line.Length == marker.Length || char.IsWhiteSpace(line[marker.Length]));
}
