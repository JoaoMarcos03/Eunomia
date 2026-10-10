namespace Eunomia.Drivers.MySql;

/// <summary>
/// Forma de uma instrução, sem parser: a primeira keyword (depois de espaços, comentários e parênteses) e se o texto
/// acaba fora de strings e comentários. Serve para juntar o <c>SHOW WARNINGS</c> à própria instrução (PR-10: zero
/// round trips extra) só onde é seguro.
/// </summary>
internal static class MySqlStatementShape
{
    /// <summary>Instruções que podem devolver linhas (e cujos warnings o MySqlConnector não reporta).</summary>
    private static readonly HashSet<string> RowKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "WITH", "SHOW", "DESCRIBE", "DESC", "EXPLAIN", "CALL", "VALUES", "TABLE", "HANDLER",
        "ANALYZE", "CHECK", "CHECKSUM", "OPTIMIZE", "REPAIR",
    };

    public static bool MayReturnRows(string sql) => RowKeywords.Contains(LeadingKeyword(sql));

    /// <summary>Primeira palavra fora de comentários e parênteses; vazia num comentário condicional <c>/*! */</c>.</summary>
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

    /// <summary>O texto acaba fora de strings (<c>' " `</c>, com <c>\</c> e aspas duplicadas) e de comentários de bloco.</summary>
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
