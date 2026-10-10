using Avalonia.Media;
using Avalonia.Media.Immutable;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Eunomia.Engine.Sql;
using Eunomia.Engine.Sql.Lexing;
using Eunomia.UI.Controls.Common.Theming;

namespace Eunomia.UI.Controls.SqlEditor;

/// <summary>SQL editor colours (section 5.6), read from the theme tokens; in light they are HeidiSQL's defaults.</summary>
public sealed record SqlEditorPalette(
    IImmutableBrush Background,
    IImmutableBrush Foreground,
    IImmutableBrush Keyword,
    IImmutableBrush DataType,
    IImmutableBrush Function,
    IImmutableBrush Number,
    IImmutableBrush String,
    IImmutableBrush Comment,
    IImmutableBrush Identifier,
    IImmutableBrush Variable,
    IImmutableBrush Gutter)
{
    // Light: HeidiSQL's colours (comment grey, datatype maroon, function navy, key blue, number purple, string green,
    // table name fuchsia); both variants come from the theme tokens.
    public static SqlEditorPalette Light { get; } = From(ThemeTokens.Light);

    public static SqlEditorPalette Dark { get; } = From(ThemeTokens.Dark);

    private static SqlEditorPalette From(ThemeFile t) => new(
        B(t.Argb("surface.content")), B(t.Argb("text.primary")), B(t.Argb("sql.keyword")), B(t.Argb("sql.datatype")), B(t.Argb("sql.function")),
        B(t.Argb("sql.number")), B(t.Argb("sql.string")), B(t.Argb("sql.comment")), B(t.Argb("sql.identifier")), B(t.Argb("sql.variable")),
        B(t.Argb("sql.gutter")));

    private static ImmutableSolidColorBrush B(uint argb) => new(argb);
}

/// <summary>
/// Editor highlighting fed by the language's lexer (<see cref="ISqlLanguage"/>, section 16.1): only the visible lines
/// are coloured; the state at the end of each line (open string or comment) is cached and invalidated
/// from the changed line on.
/// </summary>
public sealed class SqlColorizer(ISqlLanguage language) : DocumentColorizingTransformer
{
    private readonly List<LexerState> _endStates = [];
    private readonly List<SqlToken> _tokens = [];
    private int _validLines;

    public SqlEditorPalette Palette { get; set; } = SqlEditorPalette.Light;

    /// <summary>Invalidates the state from the (1-based) line that changed.</summary>
    public void InvalidateFrom(int lineNumber) => _validLines = Math.Min(_validLines, Math.Max(0, lineNumber - 1));

    protected override void ColorizeLine(DocumentLine line)
    {
        var document = CurrentContext.Document;
        var start = StateBefore(document, line.LineNumber);
        var text = document.GetTextAsMemory(line.Offset, line.Length).Span;
        _tokens.Clear();
        language.TokenizeLine(text, start, _tokens);
        foreach (var token in _tokens)
        {
            var brush = BrushFor(token.Kind);
            if (brush is null || token.Length == 0)
            {
                continue;
            }
            ChangeLinePart(line.Offset + token.Start, line.Offset + token.Start + token.Length, e => e.TextRunProperties.SetForegroundBrush(brush));
        }
    }

    private IBrush? BrushFor(SqlTokenKind kind) => kind switch
    {
        SqlTokenKind.Keyword => Palette.Keyword,
        SqlTokenKind.DataType => Palette.DataType,
        SqlTokenKind.Function => Palette.Function,
        SqlTokenKind.Number => Palette.Number,
        SqlTokenKind.String => Palette.String,
        SqlTokenKind.Comment => Palette.Comment,
        SqlTokenKind.QuotedIdentifier => Palette.Identifier,
        SqlTokenKind.Variable => Palette.Variable,
        _ => null,
    };

    /// <summary>
    /// Token that ends exactly at <paramref name="end"/> (document offset), with the line's lexer state: automatic
    /// upper case only touches keywords and functions outside strings and comments.
    /// </summary>
    public (SqlTokenKind Kind, int Start, int Length)? TokenEndingAt(TextDocument document, int end)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (end <= 0 || end > document.TextLength)
        {
            return null;
        }
        var line = document.GetLineByOffset(end);
        if (end == line.Offset)
        {
            return null;
        }
        var state = StateBefore(document, line.LineNumber);
        var tokens = new List<SqlToken>();
        language.TokenizeLine(document.GetTextAsMemory(line.Offset, end - line.Offset).Span, state, tokens);
        return tokens.Count > 0 && tokens[^1] is var last && last.Start + last.Length == end - line.Offset
            ? (last.Kind, line.Offset + last.Start, last.Length)
            : null;
    }

    /// <summary>Lexer state at the start of the (1-based) line, with the same cache as the highlighting (code folding).</summary>
    internal LexerState StateAt(TextDocument document, int lineNumber) => StateBefore(document, lineNumber);

    /// <summary>Lexer state at the start of the (1-based) line, computing any missing earlier lines.</summary>
    private LexerState StateBefore(TextDocument document, int lineNumber)
    {
        if (lineNumber <= 1)
        {
            return LexerState.Normal;
        }
        while (_endStates.Count < document.LineCount)
        {
            _endStates.Add(LexerState.Normal);
        }
        var state = _validLines == 0 ? LexerState.Normal : _endStates[_validLines - 1];
        for (var n = _validLines + 1; n < lineNumber; n++)
        {
            var l = document.GetLineByNumber(n);
            _tokens.Clear();
            state = language.TokenizeLine(document.GetTextAsMemory(l.Offset, l.Length).Span, state, _tokens);
            _endStates[n - 1] = state;
            _validLines = n;
        }
        return _endStates[lineNumber - 2];
    }
}
