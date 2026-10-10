using Avalonia.Media;
using Avalonia.Media.Immutable;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Eunomia.Engine.Sql;
using Eunomia.Engine.Sql.Lexing;
using Eunomia.UI.Controls.Common.Theming;

namespace Eunomia.UI.Controls.SqlEditor;

/// <summary>Cores do editor SQL (secção 5.6), lidas dos tokens do tema; no claro são as do HeidiSQL por omissão.</summary>
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
    // Claro: as cores do HeidiSQL (comment cinzento, datatype maroon, function navy, key azul, number roxo, string verde,
    // table name fúcsia); as duas variantes vêm dos tokens do tema.
    public static SqlEditorPalette Light { get; } = From(ThemeTokens.Light);

    public static SqlEditorPalette Dark { get; } = From(ThemeTokens.Dark);

    private static SqlEditorPalette From(ThemeFile t) => new(
        B(t.Argb("surface.content")), B(t.Argb("text.primary")), B(t.Argb("sql.keyword")), B(t.Argb("sql.datatype")), B(t.Argb("sql.function")),
        B(t.Argb("sql.number")), B(t.Argb("sql.string")), B(t.Argb("sql.comment")), B(t.Argb("sql.identifier")), B(t.Argb("sql.variable")),
        B(t.Argb("sql.gutter")));

    private static ImmutableSolidColorBrush B(uint argb) => new(argb);
}

/// <summary>
/// Highlighting do editor alimentado pelo lexer da linguagem (<see cref="ISqlLanguage"/>, secção 16.1): só as linhas
/// visíveis são coloridas; o estado no fim de cada linha (string ou comentário aberto) fica em cache e é invalidado
/// a partir da linha alterada.
/// </summary>
public sealed class SqlColorizer(ISqlLanguage language) : DocumentColorizingTransformer
{
    private readonly List<LexerState> _endStates = [];
    private readonly List<SqlToken> _tokens = [];
    private int _validLines;

    public SqlEditorPalette Palette { get; set; } = SqlEditorPalette.Light;

    /// <summary>Invalida o estado a partir da linha (1-based) que mudou.</summary>
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
    /// Token que acaba exatamente em <paramref name="end"/> (offset no documento), com o estado do lexer da linha: o
    /// auto uppercase só mexe em keywords e funções fora de strings e comentários.
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

    /// <summary>Estado do lexer no início da linha (1-based), com a mesma cache do realce (code folding).</summary>
    internal LexerState StateAt(TextDocument document, int lineNumber) => StateBefore(document, lineNumber);

    /// <summary>Estado do lexer no início da linha (1-based), calculando as linhas anteriores que faltarem.</summary>
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
