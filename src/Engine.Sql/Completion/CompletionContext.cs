using Eunomia.Engine.Sql.Lexing;
using Eunomia.Engine.Sql.Splitting;

namespace Eunomia.Engine.Sql.Completion;

/// <summary>What the autocomplete should list at the caret.</summary>
public enum CompletionScope
{
    /// <summary>Inside a string or a comment: nothing.</summary>
    Suppressed,

    /// <summary><c>@@SESSION.</c> or <c>@@GLOBAL.</c>: server variables.</summary>
    Variables,

    /// <summary><c>x.</c> or <c>db.tbl.</c>: columns or objects of the qualifier.</summary>
    Qualified,

    /// <summary>No qualifier: columns of the selected table, databases, objects, functions and keywords.</summary>
    General,
}

/// <summary>Table referenced in the current statement (<c>FROM</c>, <c>JOIN</c>, <c>INTO</c>, <c>UPDATE</c>), with its alias.</summary>
public sealed record TableReference(string? Database, string Table, string? Alias);

/// <summary>
/// Context of autocomplete A (section 16.1; appendix C §4.2): the word to replace, the qualifiers before it and the
/// tables of the current statement, to resolve aliases.
/// </summary>
/// <param name="Scope">What to list.</param>
/// <param name="ReplaceStart">Start of the text the suggestion replaces (includes the opening backtick, if any).</param>
/// <param name="Typed">What has already been typed of the word, without the backtick.</param>
/// <param name="Quoted">The word starts with a backtick: the suggestion always goes in backticks.</param>
/// <param name="Qualifiers">Names before the dot: <c>[alias]</c>, <c>[db]</c> or <c>[db, tbl]</c>.</param>
/// <param name="GlobalVariables"><c>@@GLOBAL.</c> instead of <c>@@SESSION.</c>.</param>
/// <param name="Tables">Tables of the current statement.</param>
public sealed record CompletionContext(
    CompletionScope Scope,
    int ReplaceStart,
    string Typed,
    bool Quoted,
    IReadOnlyList<string> Qualifiers,
    bool GlobalVariables,
    IReadOnlyList<TableReference> Tables)
{
    /// <summary>
    /// Analyses the editor text at the caret: suppressed inside strings and comments; <c>@@SESSION.</c> /
    /// <c>@@GLOBAL.</c>; <c>a.</c> and <c>a.b.</c> qualifiers; and the tables of <c>FROM|INTO|UPDATE … (WHERE|HAVING|
    /// ORDER|GROUP)</c> of the statement containing the caret, with JOINs treated as commas and <c>tbl [AS] alias</c> pairs.
    /// </summary>
    public static CompletionContext Analyze(string text, int caret, string delimiter = ";")
    {
        ArgumentNullException.ThrowIfNull(text);
        caret = Math.Clamp(caret, 0, text.Length);
        if (InsideStringOrComment(text, caret))
        {
            return new CompletionContext(CompletionScope.Suppressed, caret, string.Empty, false, [], false, []);
        }

        var start = caret;
        while (start > 0 && IsWordChar(text[start - 1]))
        {
            start--;
        }
        var typed = text[start..caret];
        var quoted = start > 0 && text[start - 1] == '`';
        var replaceStart = quoted ? start - 1 : start;

        // Qualifiers: up to two names separated by dots before the word.
        var qualifiers = new List<string>();
        var at = replaceStart;
        while (qualifiers.Count < 2 && at > 0 && text[at - 1] == '.' && ReadNameBackwards(text, at - 1) is { } name)
        {
            qualifiers.Insert(0, name.Name);
            at = name.Start;
        }
        if (qualifiers.Count == 1 && at >= 2 && text[at - 1] == '@' && text[at - 2] == '@'
            && (qualifiers[0].Equals("SESSION", StringComparison.OrdinalIgnoreCase) || qualifiers[0].Equals("GLOBAL", StringComparison.OrdinalIgnoreCase)))
        {
            return new CompletionContext(CompletionScope.Variables, replaceStart, typed, false, qualifiers,
                qualifiers[0].Equals("GLOBAL", StringComparison.OrdinalIgnoreCase), []);
        }

        var statements = SqlSplitter.Split(text, string.IsNullOrEmpty(delimiter) ? ";" : delimiter);
        var statement = SqlSplitter.StatementAt(statements, caret);
        var tables = statement is null ? [] : TablesOf(statement.Text);
        return new CompletionContext(qualifiers.Count > 0 ? CompletionScope.Qualified : CompletionScope.General,
            replaceStart, typed, quoted, qualifiers, false, tables);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';

    /// <summary>Plain or backticked name ending before <paramref name="end"/>.</summary>
    private static (string Name, int Start)? ReadNameBackwards(string text, int end)
    {
        if (end > 0 && text[end - 1] == '`')
        {
            var open = text.LastIndexOf('`', end - 2 < 0 ? 0 : end - 2);
            return open >= 0 && open < end - 1 ? (text[(open + 1)..(end - 1)].Replace("``", "`", StringComparison.Ordinal), open) : null;
        }
        var start = end;
        while (start > 0 && IsWordChar(text[start - 1]))
        {
            start--;
        }
        return start < end ? (text[start..end], start) : null;
    }

    /// <summary>The caret is inside a string or a comment (the lexer runs up to the caret).</summary>
    private static bool InsideStringOrComment(string text, int caret)
    {
        var state = LexerState.Normal;
        var tokens = new List<SqlToken>();
        var lineStart = 0;
        while (true)
        {
            var newline = text.IndexOf('\n', lineStart);
            var lineEnd = newline < 0 || newline >= caret ? caret : newline;
            tokens.Clear();
            state = MySqlLexer.TokenizeLine(text.AsSpan(lineStart, lineEnd - lineStart), state, tokens);
            if (lineEnd == caret)
            {
                if (state is LexerState.BlockComment or LexerState.SingleQuoted or LexerState.DoubleQuoted)
                {
                    return true;
                }
                // Line comment up to the caret (a block closed before the caret does not count).
                return tokens.Count > 0 && tokens[^1] is { Kind: SqlTokenKind.Comment } last
                    && last.Start + last.Length == lineEnd - lineStart
                    && text[lineStart + last.Start] is '#' or '-';
            }
            lineStart = newline + 1;
        }
    }

    private static readonly HashSet<string> TableListStart = new(StringComparer.OrdinalIgnoreCase)
    {
        "FROM", "JOIN", "STRAIGHT_JOIN", "INTO", "UPDATE",
    };

    /// <summary>They close the table list (a comma after them no longer separates tables).</summary>
    private static readonly HashSet<string> ClauseEnd = new(StringComparer.OrdinalIgnoreCase)
    {
        "WHERE", "HAVING", "ORDER", "GROUP", "LIMIT", "SET", "VALUES", "VALUE", "SELECT", "UNION", "WINDOW", "FOR",
        "PROCEDURE", "LOCK", "PARTITION",
    };

    /// <summary>Neither a table name nor an alias: the end of the list, JOIN conditions and JOIN kinds.</summary>
    private static readonly HashSet<string> TableListEnd = new(ClauseEnd.Concat(
        ["ON", "USING", "LEFT", "RIGHT", "INNER", "OUTER", "CROSS", "NATURAL", "FULL"]), StringComparer.OrdinalIgnoreCase);

    /// <summary>Tables and aliases of <c>FROM|JOIN|INTO|UPDATE</c> in the statement (without looking at subqueries separately).</summary>
    public static IReadOnlyList<TableReference> TablesOf(string statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        var words = Words(statement);
        var result = new List<TableReference>();
        var expectTable = false;
        for (var i = 0; i < words.Count; i++)
        {
            var (text, kind) = words[i];
            if (kind is SqlTokenKind.Operator or SqlTokenKind.Delimiter)
            {
                // Commas continue the list; parentheses are ignored.
                if (text == "," && result.Count > 0 && expectTable is false && InTableList(words, i))
                {
                    expectTable = true;
                }
                continue;
            }
            if (kind != SqlTokenKind.QuotedIdentifier && TableListStart.Contains(text))
            {
                expectTable = true;
                continue;
            }
            if (!expectTable || !IsName(kind) || (kind != SqlTokenKind.QuotedIdentifier && TableListEnd.Contains(text)))
            {
                expectTable = false;
                continue;
            }
            // name [. name] [[AS] alias]
            var first = Unquote(text, kind);
            string? database = null;
            var table = first;
            if (i + 2 < words.Count && words[i + 1].Text == "." && IsName(words[i + 2].Kind))
            {
                database = first;
                table = Unquote(words[i + 2].Text, words[i + 2].Kind);
                i += 2;
            }
            else if (i + 1 < words.Count && words[i + 1].Text == ".")
            {
                // "db." still being typed: not a table.
                expectTable = false;
                continue;
            }
            string? alias = null;
            if (i + 2 < words.Count && words[i + 1].Text.Equals("AS", StringComparison.OrdinalIgnoreCase) && IsName(words[i + 2].Kind))
            {
                alias = Unquote(words[i + 2].Text, words[i + 2].Kind);
                i += 2;
            }
            else if (i + 1 < words.Count && words[i + 1].Kind is SqlTokenKind.Identifier or SqlTokenKind.QuotedIdentifier)
            {
                alias = Unquote(words[i + 1].Text, words[i + 1].Kind);
                i++;
            }
            result.Add(new TableReference(database, table, alias));
            expectTable = false;
        }
        return result;
    }

    /// <summary>The comma is in a table list: the last structural word before it opened one.</summary>
    private static bool InTableList(List<(string Text, SqlTokenKind Kind)> words, int comma)
    {
        for (var i = comma - 1; i >= 0; i--)
        {
            var (text, kind) = words[i];
            if (kind == SqlTokenKind.QuotedIdentifier)
            {
                continue;
            }
            if (TableListStart.Contains(text))
            {
                return true;
            }
            if (ClauseEnd.Contains(text))
            {
                return false;
            }
        }
        return false;
    }

    private static bool IsName(SqlTokenKind kind) =>
        kind is SqlTokenKind.Identifier or SqlTokenKind.QuotedIdentifier or SqlTokenKind.Keyword or SqlTokenKind.DataType or SqlTokenKind.Function;

    private static string Unquote(string text, SqlTokenKind kind) =>
        kind == SqlTokenKind.QuotedIdentifier && text.Length >= 2 && text[0] == '`'
            ? text[1..(text[^1] == '`' ? ^1 : ^0)].Replace("``", "`", StringComparison.Ordinal)
            : text;

    /// <summary>Tokens without whitespace or comments, with their text.</summary>
    private static List<(string Text, SqlTokenKind Kind)> Words(string statement)
    {
        var result = new List<(string, SqlTokenKind)>();
        var tokens = new List<SqlToken>();
        var state = LexerState.Normal;
        var lineStart = 0;
        while (lineStart <= statement.Length)
        {
            var newline = statement.IndexOf('\n', lineStart);
            var lineEnd = newline < 0 ? statement.Length : newline;
            tokens.Clear();
            state = MySqlLexer.TokenizeLine(statement.AsSpan(lineStart, lineEnd - lineStart), state, tokens);
            foreach (var t in tokens)
            {
                if (t.Kind is not (SqlTokenKind.Whitespace or SqlTokenKind.Comment))
                {
                    result.Add((statement.Substring(lineStart + t.Start, t.Length), t.Kind));
                }
            }
            if (newline < 0)
            {
                break;
            }
            lineStart = newline + 1;
        }
        return result;
    }
}
