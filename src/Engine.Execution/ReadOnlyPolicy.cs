namespace Eunomia.Engine.Execution;

/// <summary>
/// What a read-only session (section 23.2; phase 14) lets through to the server: a list of what only reads (SELECT without
/// <c>INTO OUTFILE</c>, SHOW, DESCRIBE, EXPLAIN, USE, HELP, read-only CTEs, SET of session variables, transaction
/// control). Everything else (DML, DDL, CALL, DO, PREPARE/EXECUTE, LOCK, KILL, GRANT, SET GLOBAL, LOAD DATA…) is refused
/// before it leaves the client. It is an allow list on purpose: a false "writes" only forces turning the mode off, a
/// false "reads" could change production data. The server is also put in <c>SET SESSION TRANSACTION READ ONLY</c>.
/// </summary>
public static class ReadOnlyPolicy
{
    private static readonly HashSet<string> ReadWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "SHOW", "DESCRIBE", "DESC", "EXPLAIN", "USE", "HELP", "TABLE", "VALUES", "CHECKSUM",
        "BEGIN", "START", "COMMIT", "ROLLBACK", "SAVEPOINT", "RELEASE",
    };

    /// <summary>The statement only reads (or is harmless) and can run in a read-only session.</summary>
    public static bool IsAllowed(string statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        // Without comments and with strings blanked, each ";" separates statements: all of them must be allowed.
        return WithoutComments(statement).Split(';').All(IsAllowedSingle);
    }

    private static bool IsAllowedSingle(string piece)
    {
        var code = piece.Trim().TrimStart('(').TrimStart();
        if (code.Length == 0)
        {
            return true;
        }
        var first = FirstWord(code);
        var upper = code.ToUpperInvariant();
        switch (first.ToUpperInvariant())
        {
            case "SELECT" or "TABLE" or "VALUES":
                return !upper.Contains("INTO OUTFILE", StringComparison.Ordinal) && !upper.Contains("INTO DUMPFILE", StringComparison.Ordinal);
            case "WITH":
                return !ContainsWord(upper, "UPDATE") && !ContainsWord(upper, "DELETE") && !ContainsWord(upper, "INSERT")
                       && !upper.Contains("INTO OUTFILE", StringComparison.Ordinal) && !upper.Contains("INTO DUMPFILE", StringComparison.Ordinal);
            case "BEGIN":
                // BEGIN NOT ATOMIC (MariaDB) is a compound block that may write; only BEGIN [WORK] is a transaction.
                var after = FirstWord(code[5..].TrimStart());
                return after.Length == 0 || after.Equals("WORK", StringComparison.OrdinalIgnoreCase);
            case "EXPLAIN" or "DESCRIBE" or "DESC":
                // EXPLAIN ANALYZE runs the statement (on MySQL 8, UPDATE and DELETE too).
                return !ContainsWord(upper, "ANALYZE");
            case "START":
                return FirstWord(code[5..].TrimStart()).Equals("TRANSACTION", StringComparison.OrdinalIgnoreCase)
                       && !ContainsWord(upper, "WRITE");
            case "SET":
                // Session variables, yes; server variables, passwords, default roles and "READ WRITE", no.
                return !ContainsWord(upper, "GLOBAL") && !ContainsWord(upper, "PERSIST") && !ContainsWord(upper, "PERSIST_ONLY")
                       && !upper.Contains("@@GLOBAL.", StringComparison.Ordinal) && !upper.Contains("@@PERSIST", StringComparison.Ordinal)
                       && !ContainsWord(upper, "PASSWORD") && !upper.Contains("DEFAULT ROLE", StringComparison.Ordinal)
                       && !upper.Contains("READ WRITE", StringComparison.Ordinal);
            default:
                return ReadWords.Contains(first);
        }
    }

    /// <summary>Error message for a refused statement.</summary>
    public static string RefusalMessage(string session) =>
        $"Session \"{session}\" is read-only: the statement changes data or structure and was not sent to the server. " +
        "Turn off \"Read-only session\" in the Session Manager to change data.";

    private static string FirstWord(string text)
    {
        var end = 0;
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_'))
        {
            end++;
        }
        return text[..end];
    }

    private static bool ContainsWord(string upper, string word)
    {
        var at = 0;
        while ((at = upper.IndexOf(word, at, StringComparison.Ordinal)) >= 0)
        {
            var before = at == 0 || !(char.IsLetterOrDigit(upper[at - 1]) || upper[at - 1] is '_' or '@' or '.');
            var end = at + word.Length;
            var after = end >= upper.Length || !(char.IsLetterOrDigit(upper[end]) || upper[end] == '_');
            if (before && after)
            {
                return true;
            }
            at = end;
        }
        return false;
    }

    /// <summary>Removes comments (executable <c>/*! … */</c> ones stay, without the markers) and the content of strings and quoted identifiers.</summary>
    private static string WithoutComments(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '#' || (c == '-' && i + 2 < text.Length && text[i + 1] == '-' && char.IsWhiteSpace(text[i + 2])))
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }
                sb.Append(' ');
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                if (i + 2 < text.Length && text[i + 2] == '!')
                {
                    // Executable comment: its content counts (skips the version).
                    i += 3;
                    while (i < text.Length && char.IsAsciiDigit(text[i]))
                    {
                        i++;
                    }
                    i--;
                    sb.Append(' ');
                    continue;
                }
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 1;
                sb.Append(' ');
            }
            else if (c == '*' && i + 1 < text.Length && text[i + 1] == '/')
            {
                // End of an executable comment.
                i++;
                sb.Append(' ');
            }
            else if (c is '\'' or '"' or '`')
            {
                // The content of strings and identifiers does not count as words ("SELECT 'DELETE'").
                var quote = c;
                i++;
                while (i < text.Length && text[i] != quote)
                {
                    if (text[i] == '\\' && quote != '`')
                    {
                        i++;
                    }
                    i++;
                }
                sb.Append(" x ");
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
