using AvaloniaEdit.Document;
using Eunomia.Engine.Sql.Lexing;

namespace Eunomia.UI.Controls.SqlEditor;

/// <summary>
/// Regiões do code folding (Query ▸ Code folding; secção 6 da spec: só por marcadores <c>#region</c>/<c>#endregion</c>,
/// que no MySQL são comentários e não mudam o SQL): uma linha é marcador quando começa, depois de espaços, por
/// <c>#region</c> ou <c>#endregion</c> e não está dentro de uma string nem de um comentário de bloco. Os pares fazem-se
/// por uma pilha (as regiões podem estar umas dentro das outras); um marcador sem par fica de fora.
/// </summary>
internal static class RegionFolding
{
    public const string Start = "#region";
    public const string End = "#endregion";

    /// <summary>Uma região: do <c>#</c> do <c>#region</c> ao fim da linha do <c>#endregion</c>; o nome é a linha do <c>#region</c>.</summary>
    public readonly record struct Region(int StartOffset, int EndOffset, string Name);

    /// <param name="stateAt">Estado do lexer no início de uma linha (1-based).</param>
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

    /// <summary>A linha é o marcador, sozinho ou seguido de espaço e um nome (<c>#regional</c> não conta).</summary>
    private static bool IsMarker(ReadOnlySpan<char> line, string marker) =>
        line.StartsWith(marker, StringComparison.OrdinalIgnoreCase) && (line.Length == marker.Length || char.IsWhiteSpace(line[marker.Length]));
}
