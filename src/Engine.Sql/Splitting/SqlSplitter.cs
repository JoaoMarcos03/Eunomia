namespace Eunomia.Engine.Sql.Splitting;

/// <summary>Uma instrução separada, com a posição no texto original (para erros e "Run current query").</summary>
public sealed record SqlStatement(string Text, int Start, int End, int Line)
{
    /// <summary>Delimitador em vigor quando a instrução acabou (para o SQL log).</summary>
    public string Delimiter { get; init; } = ";";
}

/// <summary>
/// Separa um script em instruções com as regras do HeidiSQL (FAM-Q02; anexo C §4.4): o delimitador (por omissão
/// <c>;</c>) só conta fora de strings (<c>'</c>, <c>"</c> e <c>`</c> com escapes por barra invertida ou aspas
/// duplicadas) e de comentários (<c>-- </c>, <c>#</c>, <c>/* */</c>); <c>DELIMITER xyz</c> no início de uma linha
/// muda o delimitador e não é enviado. Instruções só com espaços e comentários não são enviadas (os comentários
/// condicionais <c>/*! */</c> são executáveis e ficam).
/// </summary>
public static class SqlSplitter
{
    /// <summary>Delimitador válido: não vazio e sem comentários nem aspas (como o "Set delimiter" do HeidiSQL).</summary>
    public static bool IsValidDelimiter(string? delimiter) =>
        !string.IsNullOrWhiteSpace(delimiter)
        && !delimiter.Contains("/*", StringComparison.Ordinal)
        && !delimiter.Contains("--", StringComparison.Ordinal)
        && !delimiter.Contains('#', StringComparison.Ordinal)
        && delimiter.IndexOfAny(['\'', '"', '`']) < 0
        && !delimiter.Any(char.IsWhiteSpace);

    public static IReadOnlyList<SqlStatement> Split(string text, string delimiter = ";")
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!IsValidDelimiter(delimiter))
        {
            throw new ArgumentException($"Invalid delimiter: \"{delimiter}\".", nameof(delimiter));
        }
        var result = new List<SqlStatement>();
        var start = 0;
        var line = 1;
        var startLine = 1;
        var i = 0;
        var atLineStart = true;
        while (i < text.Length)
        {
            var c = text[i];
            if (atLineStart && TryReadDelimiterCommand(text, i, out var newDelimiter, out var commandEnd))
            {
                Add(result, text, start, i, startLine, delimiter);
                delimiter = newDelimiter;
                // A linha do DELIMITER não é enviada.
                i = commandEnd;
                start = i;
                startLine = line;
                continue;
            }
            if (c == '\n')
            {
                line++;
                atLineStart = true;
                i++;
                continue;
            }
            if (atLineStart && (c == ' ' || c == '\t' || c == '\r'))
            {
                i++;
                continue;
            }
            atLineStart = false;
            if (c is '\'' or '"' or '`')
            {
                i = SkipQuoted(text, i, ref line);
                continue;
            }
            if (c == '#' || (c == '-' && i + 1 < text.Length && text[i + 1] == '-' && (i + 2 >= text.Length || char.IsWhiteSpace(text[i + 2]))))
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }
                continue;
            }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var end = close < 0 ? text.Length : close + 2;
                line += CountLines(text, i, end);
                i = end;
                continue;
            }
            if (string.CompareOrdinal(text, i, delimiter, 0, delimiter.Length) == 0)
            {
                Add(result, text, start, i, startLine, delimiter);
                i += delimiter.Length;
                start = i;
                startLine = line;
                continue;
            }
            i++;
        }
        Add(result, text, start, text.Length, startLine, delimiter);
        return result;
    }

    /// <summary>
    /// "Run current query" (Ctrl+Shift+F9): a instrução que contém o caret; se o caret está entre instruções (ou numa
    /// vazia), a anterior.
    /// </summary>
    public static SqlStatement? StatementAt(IReadOnlyList<SqlStatement> statements, int caret)
    {
        ArgumentNullException.ThrowIfNull(statements);
        SqlStatement? previous = null;
        foreach (var s in statements)
        {
            if (caret >= s.Start && caret <= s.End)
            {
                return s;
            }
            if (s.End < caret)
            {
                previous = s;
            }
        }
        return previous;
    }

    private static void Add(List<SqlStatement> result, string text, int start, int end, int line, string delimiter)
    {
        // Avança espaços e quebras de linha iniciais para a posição e a linha apontarem para o texto.
        var s = start;
        while (s < end && char.IsWhiteSpace(text[s]))
        {
            if (text[s] == '\n')
            {
                line++;
            }
            s++;
        }
        var e = end;
        while (e > s && char.IsWhiteSpace(text[e - 1]))
        {
            e--;
        }
        if (e <= s || IsOnlyComments(text, s, e))
        {
            return;
        }
        result.Add(new SqlStatement(text[s..e], s, e, line) { Delimiter = delimiter });
    }

    private static bool IsOnlyComments(string text, int start, int end) => IsOnlyComments(text.AsSpan(start, end - start));

    /// <summary>Só espaços e comentários (os condicionais <c>/*! */</c> e <c>/*M! */</c> são executáveis).</summary>
    internal static bool IsOnlyComments(ReadOnlySpan<char> text)
    {
        var start = 0;
        var end = text.Length;
        var i = start;
        while (i < end)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '#' || (c == '-' && i + 1 < end && text[i + 1] == '-'))
            {
                while (i < end && text[i] != '\n')
                {
                    i++;
                }
            }
            else if (c == '/' && i + 1 < end && text[i + 1] == '*')
            {
                // Comentários condicionais do MySQL/MariaDB são executáveis.
                if (i + 2 < end && (text[i + 2] == '!' || (text[i + 2] == 'M' && i + 3 < end && text[i + 3] == '!')))
                {
                    return false;
                }
                var close = text[(i + 2)..].IndexOf("*/", StringComparison.Ordinal);
                i = close < 0 ? end : i + 2 + close + 2;
            }
            else
            {
                return false;
            }
        }
        return true;
    }

    private static int SkipQuoted(string text, int i, ref int line)
    {
        var quote = text[i];
        i++;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\n')
            {
                line++;
            }
            if (c == '\\' && quote != '`' && i + 1 < text.Length)
            {
                if (text[i + 1] == '\n')
                {
                    line++;
                }
                i += 2;
                continue;
            }
            if (c == quote)
            {
                if (i + 1 < text.Length && text[i + 1] == quote)
                {
                    i += 2;
                    continue;
                }
                return i + 1;
            }
            i++;
        }
        return text.Length;
    }

    /// <summary><c>DELIMITER xyz</c> no início da linha (sem distinguir maiúsculas); o fim é o fim da linha.</summary>
    private static bool TryReadDelimiterCommand(string text, int i, out string delimiter, out int end)
    {
        delimiter = string.Empty;
        end = i;
        const string keyword = "DELIMITER";
        var p = i;
        while (p < text.Length && (text[p] == ' ' || text[p] == '\t'))
        {
            p++;
        }
        if (p + keyword.Length >= text.Length
            || string.Compare(text, p, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0
            || !(text[p + keyword.Length] == ' ' || text[p + keyword.Length] == '\t'))
        {
            return false;
        }
        p += keyword.Length;
        while (p < text.Length && (text[p] == ' ' || text[p] == '\t'))
        {
            p++;
        }
        var valueStart = p;
        while (p < text.Length && !char.IsWhiteSpace(text[p]))
        {
            p++;
        }
        var value = text[valueStart..p];
        if (!IsValidDelimiter(value))
        {
            return false;
        }
        while (p < text.Length && text[p] != '\n')
        {
            p++;
        }
        delimiter = value;
        end = p;
        return true;
    }

    private static int CountLines(string text, int start, int end)
    {
        var count = 0;
        for (var i = start; i < end; i++)
        {
            if (text[i] == '\n')
            {
                count++;
            }
        }
        return count;
    }
}
