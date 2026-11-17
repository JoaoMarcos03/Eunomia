namespace Eunomia.Engine.Sql;

public enum SessionStatementKind
{
    None,

    /// <summary><c>USE db</c>: the default database of the connection.</summary>
    Use,

    /// <summary>A <c>SET</c> that only changes the session (variables, <c>NAMES</c>, <c>SQL_MODE</c>, <c>FOREIGN_KEY_CHECKS</c>…).</summary>
    Set,
}

/// <summary>
/// Statements of a script that change the state of the connection and not the data (<c>USE</c> and session <c>SET</c>s,
/// also inside <c>/*!NNNNN … */</c>). A job that resumes a script on a new connection replays them first, otherwise the
/// rest of the file runs in another database and without the dump's settings (<c>FOREIGN_KEY_CHECKS=0</c>, time zone…).
/// A <c>SET</c> with an effect outside the session (<c>GLOBAL</c>, <c>PERSIST</c>, <c>PASSWORD</c>, <c>DEFAULT ROLE</c>,
/// MariaDB's <c>SET STATEMENT … FOR</c>) is not one of them.
/// </summary>
public static class SessionStatements
{
    public static SessionStatementKind Classify(string statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        var words = Words(statement, 2, stopAtSymbol: true);
        if (words.Count == 0)
        {
            return SessionStatementKind.None;
        }
        if (Is(words[0], "USE"))
        {
            return words.Count > 1 || HasQuotedName(statement) ? SessionStatementKind.Use : SessionStatementKind.None;
        }
        if (!Is(words[0], "SET"))
        {
            return SessionStatementKind.None;
        }
        if (words.Count > 1 && (Is(words[1], "PASSWORD") || Is(words[1], "DEFAULT") || Is(words[1], "STATEMENT")))
        {
            return SessionStatementKind.None;
        }
        // Any assignment with a global scope (also after a comma) excludes the whole statement.
        foreach (var word in Words(statement, int.MaxValue, stopAtSymbol: false))
        {
            if (Is(word, "GLOBAL") || Is(word, "PERSIST") || Is(word, "PERSIST_ONLY")
                || word.StartsWith("@@GLOBAL.", StringComparison.OrdinalIgnoreCase)
                || word.StartsWith("@@PERSIST", StringComparison.OrdinalIgnoreCase))
            {
                return SessionStatementKind.None;
            }
        }
        return SessionStatementKind.Set;
    }

    /// <summary>
    /// <c>USE</c> or <c>CREATE</c>/<c>DROP</c>/<c>ALTER DATABASE</c> (or <c>SCHEMA</c>), also inside <c>/*!NNNNN … */</c>:
    /// the statements a script that is run in another database than its own skips (a dump's
    /// <c>CREATE DATABASE x; USE x;</c>, mysqldump's <c>DROP DATABASE</c> with <c>--add-drop-database</c>).
    /// </summary>
    public static bool IsDatabaseStatement(string statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        if (Classify(statement) == SessionStatementKind.Use)
        {
            return true;
        }
        var words = Words(statement, 2, stopAtSymbol: true);
        return words.Count == 2
            && (Is(words[0], "CREATE") || Is(words[0], "DROP") || Is(words[0], "ALTER"))
            && (Is(words[1], "DATABASE") || Is(words[1], "SCHEMA"));
    }

    /// <summary>Database a <c>USE</c> selects (without the quotes), or <c>null</c> if the statement is not a <c>USE</c>.</summary>
    public static string? UseTarget(string statement)
    {
        if (Classify(statement) != SessionStatementKind.Use)
        {
            return null;
        }
        var i = SkipTrivia(statement, 0) + "USE".Length;
        i = SkipTrivia(statement, i);
        if (i >= statement.Length)
        {
            return null;
        }
        if (statement[i] == '`')
        {
            var end = SkipQuoted(statement, i);
            return statement[(i + 1)..Math.Max(i + 1, end - 1)].Replace("``", "`", StringComparison.Ordinal);
        }
        var start = i;
        while (i < statement.Length && !char.IsWhiteSpace(statement[i]) && statement[i] is not (';' or '*' or '/'))
        {
            i++;
        }
        return i > start ? statement[start..i] : null;
    }

    private static bool Is(string word, string keyword) => word.Equals(keyword, StringComparison.OrdinalIgnoreCase);

    /// <summary><c>USE `my db`</c>: the name is quoted, so it is not a word.</summary>
    private static bool HasQuotedName(string statement) => statement.Contains('`', StringComparison.Ordinal);

    /// <summary>
    /// Words outside strings, quoted names and comments (<c>/*! … */</c> counts as code). With
    /// <paramref name="stopAtSymbol"/>, stops at the first symbol (only the first words matter).
    /// </summary>
    private static List<string> Words(string text, int max, bool stopAtSymbol)
    {
        var words = new List<string>();
        var i = SkipTrivia(text, 0);
        while (i < text.Length && words.Count < max)
        {
            var c = text[i];
            if (char.IsLetter(c) || c == '_' || c == '@')
            {
                var start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '@' or '.' or '$'))
                {
                    i++;
                }
                words.Add(text[start..i]);
            }
            else if (stopAtSymbol)
            {
                break;
            }
            else if (c is '\'' or '"' or '`')
            {
                i = SkipQuoted(text, i);
            }
            else
            {
                i++;
            }
            i = SkipTrivia(text, i);
        }
        return words;
    }

    /// <summary>Skips whitespace, semicolons and comments from <paramref name="i"/>; an executable comment's marks are skipped, its content is not.</summary>
    private static int SkipTrivia(string text, int i)
    {
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c) || c == ';')
            {
                i++;
            }
            else if (c == '#' || (c == '-' && i + 2 < text.Length && text[i + 1] == '-' && char.IsWhiteSpace(text[i + 2])))
            {
                var end = text.IndexOf('\n', i);
                i = end < 0 ? text.Length : end + 1;
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                if (i + 2 < text.Length && text[i + 2] == '!')
                {
                    // Executable comment: the version is skipped and what is inside is read.
                    i += 3;
                    while (i < text.Length && char.IsAsciiDigit(text[i]))
                    {
                        i++;
                    }
                }
                else
                {
                    var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? text.Length : end + 2;
                }
            }
            else if (c == '*' && i + 1 < text.Length && text[i + 1] == '/')
            {
                // End of an executable comment.
                i += 2;
            }
            else
            {
                return i;
            }
        }
        return i;
    }

    /// <summary>Position after the quoted text that starts at <paramref name="start"/> (doubled quotes and backslashes inside).</summary>
    private static int SkipQuoted(string text, int start)
    {
        var quote = text[start];
        var i = start + 1;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\\' && quote != '`')
            {
                i += 2;
            }
            else if (c == quote)
            {
                if (i + 1 < text.Length && text[i + 1] == quote)
                {
                    i += 2;
                }
                else
                {
                    return i + 1;
                }
            }
            else
            {
                i++;
            }
        }
        return text.Length;
    }
}

/// <summary>
/// Session state of a script as it runs (the <see cref="SessionStatements"/> that succeeded), to replay on a new connection
/// when the job resumes. Only the last <c>USE</c> is kept; a repeated <c>SET</c> (mysqldump repeats the same ones for each
/// table) moves to the end instead of piling up, so replaying the list in order gives the same session as the script. The
/// size is bounded: a very long <c>SET</c> (a big value in a variable) is not kept and the oldest ones go first.
/// </summary>
public sealed class SessionStateLog
{
    public const int MaxStatements = 200;
    public const int MaxStatementLength = 8 * 1024;

    private readonly List<string> _statements;

    public SessionStateLog(IEnumerable<string>? statements = null) => _statements = statements is null ? [] : [.. statements];

    public IReadOnlyList<string> Statements => _statements;

    /// <summary>Records <paramref name="statement"/> if it changes the session; <c>true</c> if the list changed.</summary>
    public bool Record(string statement)
    {
        var kind = SessionStatements.Classify(statement);
        if (kind == SessionStatementKind.None)
        {
            return false;
        }
        var text = statement.Trim();
        if (text.Length > MaxStatementLength)
        {
            return false;
        }
        if (_statements.Count > 0 && _statements[^1] == text)
        {
            return false;
        }
        if (kind == SessionStatementKind.Use)
        {
            _statements.RemoveAll(s => SessionStatements.Classify(s) == SessionStatementKind.Use);
        }
        else
        {
            _statements.Remove(text);
        }
        _statements.Add(text);
        while (_statements.Count > MaxStatements)
        {
            var oldest = _statements.FindIndex(s => SessionStatements.Classify(s) != SessionStatementKind.Use);
            _statements.RemoveAt(oldest < 0 ? 0 : oldest);
        }
        return true;
    }
}
