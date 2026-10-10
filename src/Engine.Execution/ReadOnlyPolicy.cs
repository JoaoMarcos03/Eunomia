namespace Eunomia.Engine.Execution;

/// <summary>
/// O que uma sessão read-only (secção 23.2; Fase 14) deixa enviar ao servidor: uma lista do que só lê (SELECT sem
/// <c>INTO OUTFILE</c>, SHOW, DESCRIBE, EXPLAIN, USE, HELP, CTEs de leitura, SET de variáveis da sessão, controlo de
/// transações). Tudo o resto (DML, DDL, CALL, DO, PREPARE/EXECUTE, LOCK, KILL, GRANT, SET GLOBAL, LOAD DATA…) é recusado
/// antes de sair do cliente. É uma lista de permitidos de propósito: um falso "escreve" só obriga a desligar o modo, um
/// falso "lê" podia mudar dados de produção. O servidor também fica em <c>SET SESSION TRANSACTION READ ONLY</c>.
/// </summary>
public static class ReadOnlyPolicy
{
    private static readonly HashSet<string> ReadWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "SHOW", "DESCRIBE", "DESC", "EXPLAIN", "USE", "HELP", "TABLE", "VALUES", "CHECKSUM",
        "BEGIN", "START", "COMMIT", "ROLLBACK", "SAVEPOINT", "RELEASE",
    };

    /// <summary>A instrução só lê (ou é inofensiva) e pode correr numa sessão read-only.</summary>
    public static bool IsAllowed(string statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        // Sem comentários e com as strings apagadas, cada ";" separa instruções: todas têm de ser permitidas.
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
                // BEGIN NOT ATOMIC (MariaDB) é um bloco composto que pode escrever; só BEGIN [WORK] é uma transação.
                var after = FirstWord(code[5..].TrimStart());
                return after.Length == 0 || after.Equals("WORK", StringComparison.OrdinalIgnoreCase);
            case "EXPLAIN" or "DESCRIBE" or "DESC":
                // EXPLAIN ANALYZE corre a instrução (no MySQL 8 também UPDATE e DELETE).
                return !ContainsWord(upper, "ANALYZE");
            case "START":
                return FirstWord(code[5..].TrimStart()).Equals("TRANSACTION", StringComparison.OrdinalIgnoreCase)
                       && !ContainsWord(upper, "WRITE");
            case "SET":
                // Variáveis da sessão, sim; do servidor, passwords, papéis por omissão e "READ WRITE", não.
                return !ContainsWord(upper, "GLOBAL") && !ContainsWord(upper, "PERSIST") && !ContainsWord(upper, "PERSIST_ONLY")
                       && !upper.Contains("@@GLOBAL.", StringComparison.Ordinal) && !upper.Contains("@@PERSIST", StringComparison.Ordinal)
                       && !ContainsWord(upper, "PASSWORD") && !upper.Contains("DEFAULT ROLE", StringComparison.Ordinal)
                       && !upper.Contains("READ WRITE", StringComparison.Ordinal);
            default:
                return ReadWords.Contains(first);
        }
    }

    /// <summary>Mensagem do erro de uma instrução recusada.</summary>
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

    /// <summary>Tira comentários (os executáveis <c>/*! … */</c> ficam, sem as marcas) e o conteúdo de strings e identificadores citados.</summary>
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
                    // Comentário executável: o conteúdo conta (salta a versão).
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
                // Fim de um comentário executável.
                i++;
                sb.Append(' ');
            }
            else if (c is '\'' or '"' or '`')
            {
                // O conteúdo de strings e identificadores não conta como palavras ("SELECT 'DELETE'").
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
