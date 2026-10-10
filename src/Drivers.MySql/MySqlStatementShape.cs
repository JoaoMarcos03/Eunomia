namespace Eunomia.Drivers.MySql;

/// <summary>
/// Shape of a statement, without a parser: the first keyword (after whitespace, comments and parentheses) and whether the
/// text ends outside strings and comments. Used to append <c>SHOW WARNINGS</c> to the statement itself (PR-10: zero
/// extra round trips) only where it is safe.
/// </summary>
internal static class MySqlStatementShape
{
    /// <summary>Statements that may return rows (and whose warnings MySqlConnector does not report).</summary>
    private static readonly HashSet<string> RowKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "WITH", "SHOW", "DESCRIBE", "DESC", "EXPLAIN", "CALL", "VALUES", "TABLE", "HANDLER",
        "ANALYZE", "CHECK", "CHECKSUM", "OPTIMIZE", "REPAIR",
    };

    public static bool MayReturnRows(string sql) => RowKeywords.Contains(LeadingKeyword(sql));

    /// <summary>First word outside comments and parentheses; empty in a <c>/*! */</c> conditional comment.</summary>
    public static string LeadingKeyword(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (char.IsWhiteSpace(c) || c == '(')
            {
                i++;
            }
            else if (c == '#' || (c == '-' && At(sql, i + 1) == '-' && (i + 2 >= sql.Length || char.IsWhiteSpace(sql[i + 2]))))
            {
                var end = sql.IndexOf('\n', i);
                i = end < 0 ? sql.Length : end + 1;
            }
            else if (c == '/' && At(sql, i + 1) == '*' && At(sql, i + 2) != '!')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + 2;
            }
            else
            {
                break;
            }
        }
        var start = i;
        while (i < sql.Length && (char.IsAsciiLetter(sql[i]) || sql[i] == '_'))
        {
            i++;
        }
        return sql[start..i];
    }

    /// <summary>The text ends outside strings (<c>' " `</c>, with <c>\</c> and doubled quotes) and block comments.</summary>
    public static bool EndsInCode(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c is '\'' or '"' or '`')
            {
                i++;
                var closed = false;
                while (i < sql.Length)
                {
                    if (sql[i] == '\\' && c != '`')
                    {
                        i += 2;
                        continue;
                    }
                    if (sql[i] == c)
                    {
                        if (At(sql, i + 1) == c)
                        {
                            i += 2;
                            continue;
                        }
                        closed = true;
                        i++;
                        break;
                    }
                    i++;
                }
                if (!closed)
                {
                    return false;
                }
            }
            else if (c == '#' || (c == '-' && At(sql, i + 1) == '-' && (i + 2 >= sql.Length || char.IsWhiteSpace(sql[i + 2]))))
            {
                var end = sql.IndexOf('\n', i);
                i = end < 0 ? sql.Length : end + 1;
            }
            else if (c == '/' && At(sql, i + 1) == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    return false;
                }
                i = end + 2;
            }
            else
            {
                i++;
            }
        }
        return true;
    }

    private static char At(string s, int i) => i < s.Length ? s[i] : '\0';
}
