using System.Text;
using Eunomia.Engine.Sql.Lexing;

namespace Eunomia.Engine.Sql.Formatting;

/// <summary>
/// Reformat SQL (Ctrl+F8; section 4.7, internal formatter; appendix C §4): simple rules over the lexer's token stream,
/// HeidiSQL's plus a few more. Keywords, types and functions in upper case; SELECT, FROM, WHERE, GROUP BY, ORDER BY, HAVING,
/// LIMIT, the JOINs, CREATE, DROP, UPDATE, INSERT, REPLACE, TRUNCATE and DELETE start a line (as in HeidiSQL), and so do SET,
/// VALUES and UNION; AND and OR of the conditions on the next line, indented; subqueries indented; a blank line between
/// statements. Strings, identifiers and comments stay as they are; whitespace between tokens is only kept where it already
/// existed, never after "(" or before ")", "," or ";".
/// </summary>
public static class SqlFormatter
{
    private const string Indent = "\t";

    private static readonly HashSet<string> ClauseStarts = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "HAVING", "LIMIT", "UNION", "VALUES", "INSERT", "REPLACE", "UPDATE", "DELETE", "WINDOW",
        "CREATE", "DROP", "TRUNCATE",
    };

    private static readonly HashSet<string> JoinStarts = new(StringComparer.OrdinalIgnoreCase)
    {
        "LEFT", "RIGHT", "INNER", "CROSS", "NATURAL", "FULL", "STRAIGHT_JOIN",
    };

    public static string Format(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var tokens = Tokens(sql);
        var output = new StringBuilder(sql.Length + 32);
        var depth = 0;
        var subqueryDepths = new Stack<int>();
        var betweenPending = false;
        var previous = -1;
        var newlineDue = false;
        for (var i = 0; i < tokens.Count; i++)
        {
            var (start, end, kind) = tokens[i];
            var text = sql[start..end];
            if (kind == SqlTokenKind.Whitespace)
            {
                continue;
            }
            var word = kind is SqlTokenKind.Keyword or SqlTokenKind.DataType or SqlTokenKind.Function ? text.ToUpperInvariant() : text;
            var lineBreak = newlineDue;
            var extraIndent = false;
            // Inside parentheses that are not a subquery (arguments, lists) the line is not broken: EXTRACT(YEAR FROM d).
            var inPlainParens = subqueryDepths.Count > 0 && subqueryDepths.Peek() < 0;
            if (kind == SqlTokenKind.Keyword && !inPlainParens)
            {
                var next = NextWord(sql, tokens, i);
                var before = PreviousWord(sql, tokens, previous);
                if (ClauseStarts.Contains(word) && !(word == "REPLACE" && before is "OR") && !(word == "UPDATE" && before is "KEY" or "FOR" or "ON")
                    && !(word == "DELETE" && before is "ON")
                    && !(word == "VALUES" && before is "ON" or "=" or "," or "(" or "+" or "-" or "*" or "/"))
                {
                    lineBreak = true;
                }
                else if ((word is "GROUP" or "ORDER" or "PARTITION") && string.Equals(next, "BY", StringComparison.OrdinalIgnoreCase) && before is not "WITHIN")
                {
                    lineBreak = true;
                }
                else if (word == "ON" && string.Equals(next, "DUPLICATE", StringComparison.OrdinalIgnoreCase))
                {
                    lineBreak = true;
                }
                else if (word == "SET" && before is not ("CHARACTER" or "CHAR" or "UPDATE" or "DELETE"))
                {
                    lineBreak = true;
                }
                else if (JoinStarts.Contains(word) && (string.Equals(next, "JOIN", StringComparison.OrdinalIgnoreCase) || next is "OUTER" or "SEMI" or "ANTI"
                         || word == "STRAIGHT_JOIN" || (word == "NATURAL" && next is "LEFT" or "RIGHT" or "INNER")))
                {
                    lineBreak = !(before is not null && JoinStarts.Contains(before));
                }
                else if (word == "JOIN" && !(before is not null && (JoinStarts.Contains(before) || before is "OUTER")))
                {
                    lineBreak = true;
                }
                else if (word == "BETWEEN")
                {
                    betweenPending = true;
                }
                else if (word is "AND" or "OR")
                {
                    if (word == "AND" && betweenPending)
                    {
                        betweenPending = false;
                    }
                    else
                    {
                        lineBreak = true;
                        extraIndent = true;
                    }
                }
            }
            if (lineBreak && output.Length > 0)
            {
                TrimEndSpaces(output);
                output.Append(Environment.NewLine);
                for (var d = 0; d < depth + (extraIndent ? 1 : 0); d++)
                {
                    output.Append(Indent);
                }
            }
            else if (previous >= 0 && !newlineDue && tokens[previous].End < start && output.Length > 0 && output[^1] is not ('\n' or '\t')
                     && sql[tokens[previous].Start..tokens[previous].End] != "(" && text is not (")" or "," or ";"))
            {
                // There was whitespace (or lines) between the two tokens: one space stays (never after "(" or before ")", "," or ";").
                output.Append(' ');
            }
            newlineDue = false;
            output.Append(word);

            if (kind == SqlTokenKind.Operator && text == "(")
            {
                if (string.Equals(NextWord(sql, tokens, i), "SELECT", StringComparison.OrdinalIgnoreCase))
                {
                    subqueryDepths.Push(depth);
                    depth++;
                }
                else
                {
                    subqueryDepths.Push(-1);
                }
            }
            else if (kind == SqlTokenKind.Operator && text == ")" && subqueryDepths.Count > 0)
            {
                var opened = subqueryDepths.Pop();
                if (opened >= 0)
                {
                    depth = opened;
                }
            }
            else if (kind == SqlTokenKind.Delimiter)
            {
                depth = 0;
                subqueryDepths.Clear();
                betweenPending = false;
                TrimEndSpaces(output);
                output.Append(Environment.NewLine);
                newlineDue = true;
            }
            else if (kind == SqlTokenKind.Comment && (text.StartsWith("--", StringComparison.Ordinal) || text.StartsWith('#')))
            {
                // A line comment ends the line; without this the next token would be commented out.
                newlineDue = true;
            }
            previous = i;
        }
        while (output.Length > 0 && char.IsWhiteSpace(output[^1]))
        {
            output.Length--;
        }
        return output.ToString();
    }

    private static void TrimEndSpaces(StringBuilder output)
    {
        while (output.Length > 0 && output[^1] is ' ' or '\t')
        {
            output.Length--;
        }
    }

    private static string? NextWord(string sql, List<(int Start, int End, SqlTokenKind Kind)> tokens, int index)
    {
        for (var i = index + 1; i < tokens.Count; i++)
        {
            if (tokens[i].Kind is not (SqlTokenKind.Whitespace or SqlTokenKind.Comment))
            {
                return sql[tokens[i].Start..tokens[i].End].ToUpperInvariant();
            }
        }
        return null;
    }

    private static string? PreviousWord(string sql, List<(int Start, int End, SqlTokenKind Kind)> tokens, int index) =>
        index >= 0 ? sql[tokens[index].Start..tokens[index].End].ToUpperInvariant() : null;

    /// <summary>Tokens of the whole text with absolute positions; multi-line strings and comments stay in a single token.</summary>
    private static List<(int Start, int End, SqlTokenKind Kind)> Tokens(string sql)
    {
        var result = new List<(int, int, SqlTokenKind)>();
        var lineTokens = new List<SqlToken>();
        var state = LexerState.Normal;
        var offset = 0;
        while (offset <= sql.Length)
        {
            var newline = sql.IndexOf('\n', offset);
            var lineEnd = newline < 0 ? sql.Length : newline;
            var contentEnd = lineEnd > offset && sql[lineEnd - 1] == '\r' ? lineEnd - 1 : lineEnd;
            var startState = state;
            lineTokens.Clear();
            state = MySqlLexer.TokenizeLine(sql.AsSpan(offset, contentEnd - offset), state, lineTokens);
            for (var t = 0; t < lineTokens.Count; t++)
            {
                var token = lineTokens[t];
                var absolute = (offset + token.Start, offset + token.Start + token.Length, token.Kind);
                if (t == 0 && startState != LexerState.Normal && result.Count > 0)
                {
                    result[^1] = (result[^1].Item1, absolute.Item2, result[^1].Item3);
                }
                else
                {
                    result.Add(absolute);
                }
            }
            if (state != LexerState.Normal && result.Count > 0 && newline >= 0)
            {
                // The string or the comment continues: the line break is part of the token.
                result[^1] = (result[^1].Item1, newline + 1, result[^1].Item3);
            }
            if (newline < 0)
            {
                break;
            }
            offset = newline + 1;
        }
        return result;
    }
}
