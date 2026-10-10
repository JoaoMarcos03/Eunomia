namespace Eunomia.Engine.Sql.Lexing;

public enum SqlTokenKind
{
    Whitespace,
    Keyword,
    DataType,
    Function,
    Identifier,
    QuotedIdentifier,
    String,
    Number,
    Comment,
    Variable,
    Operator,
    Delimiter,
}

/// <summary>Token dentro de uma linha: posição e comprimento em caracteres.</summary>
public readonly record struct SqlToken(int Start, int Length, SqlTokenKind Kind);

/// <summary>Estado no fim de uma linha, para o lexing incremental por linha (secção 16.1).</summary>
public enum LexerState : byte
{
    Normal,
    BlockComment,
    SingleQuoted,
    DoubleQuoted,
    Backtick,
}

/// <summary>
/// Lexer MySQL simples e incremental por linha: keywords, tipos, funções, strings (<c>'</c> e <c>"</c>,
/// com escapes <c>\</c> e aspas duplicadas), identificadores com backticks, números (decimais, expoente,
/// <c>0x</c>), comentários <c>-- </c> (com espaço ou fim de linha), <c>#</c> e <c>/* */</c>, e variáveis
/// <c>@x</c> / <c>@@x</c>. Não aloca além da lista de tokens do chamador.
/// </summary>
public static class MySqlLexer
{
    /// <summary>Tokeniza uma linha a partir do estado deixado pela linha anterior e devolve o estado final.</summary>
    public static LexerState TokenizeLine(ReadOnlySpan<char> line, LexerState state, List<SqlToken> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        var i = 0;
        if (state != LexerState.Normal)
        {
            var entering = state;
            var end = ContinueMultiLine(line, 0, entering, out state);
            if (end > 0)
            {
                tokens.Add(new SqlToken(0, end, KindOf(entering)));
            }
            i = end;
        }

        while (i < line.Length)
        {
            var c = line[i];
            var start = i;

            if (char.IsWhiteSpace(c))
            {
                while (i < line.Length && char.IsWhiteSpace(line[i]))
                {
                    i++;
                }
                tokens.Add(new SqlToken(start, i - start, SqlTokenKind.Whitespace));
                continue;
            }

            if (c == '#' || (c == '-' && At(line, i + 1) == '-' && (i + 2 >= line.Length || char.IsWhiteSpace(line[i + 2]))))
            {
                tokens.Add(new SqlToken(start, line.Length - start, SqlTokenKind.Comment));
                return LexerState.Normal;
            }

            if (c == '/' && At(line, i + 1) == '*')
            {
                var end = ContinueMultiLine(line, i + 2, LexerState.BlockComment, out state);
                tokens.Add(new SqlToken(start, end - start, SqlTokenKind.Comment));
                i = end;
                continue;
            }

            if (c is '\'' or '"' or '`')
            {
                var quoteState = c switch
                {
                    '\'' => LexerState.SingleQuoted,
                    '"' => LexerState.DoubleQuoted,
                    _ => LexerState.Backtick,
                };
                var end = ContinueMultiLine(line, i + 1, quoteState, out state);
                tokens.Add(new SqlToken(start, end - start, c == '`' ? SqlTokenKind.QuotedIdentifier : SqlTokenKind.String));
                i = end;
                continue;
            }

            if (char.IsAsciiDigit(c) || (c == '.' && char.IsAsciiDigit(At(line, i + 1))))
            {
                var end = ScanNumber(line, i);
                if (end > i)
                {
                    tokens.Add(new SqlToken(start, end - start, SqlTokenKind.Number));
                    i = end;
                    continue;
                }
            }

            if (c == '@')
            {
                i++;
                if (At(line, i) == '@')
                {
                    i++;
                }
                while (i < line.Length && (IsIdentifierChar(line[i]) || line[i] == '.'))
                {
                    i++;
                }
                tokens.Add(new SqlToken(start, i - start, SqlTokenKind.Variable));
                continue;
            }

            if (IsIdentifierChar(c))
            {
                while (i < line.Length && IsIdentifierChar(line[i]))
                {
                    i++;
                }
                tokens.Add(new SqlToken(start, i - start, MySqlKeywords.Classify(line[start..i])));
                continue;
            }

            i++;
            tokens.Add(new SqlToken(start, 1, c == ';' ? SqlTokenKind.Delimiter : SqlTokenKind.Operator));
        }
        return state;
    }

    /// <summary>Tokeniza um texto completo (várias linhas), útil para testes.</summary>
    public static List<(int Line, SqlToken Token)> Tokenize(string text)
    {
        var result = new List<(int, SqlToken)>();
        var tokens = new List<SqlToken>();
        var state = LexerState.Normal;
        var lineNo = 0;
        foreach (var line in text.AsSpan().EnumerateLines())
        {
            tokens.Clear();
            state = TokenizeLine(line, state, tokens);
            foreach (var t in tokens)
            {
                result.Add((lineNo, t));
            }
            lineNo++;
        }
        return result;
    }

    private static SqlTokenKind KindOf(LexerState state) => state switch
    {
        LexerState.BlockComment => SqlTokenKind.Comment,
        LexerState.Backtick => SqlTokenKind.QuotedIdentifier,
        _ => SqlTokenKind.String,
    };

    /// <summary>
    /// Avança dentro de um comentário de bloco, string ou backtick a partir de <paramref name="i"/>.
    /// Devolve a posição a seguir ao fecho (ou o fim da linha) e o estado resultante.
    /// </summary>
    private static int ContinueMultiLine(ReadOnlySpan<char> line, int i, LexerState inside, out LexerState after)
    {
        switch (inside)
        {
            case LexerState.BlockComment:
            {
                var close = line[i..].IndexOf("*/");
                if (close < 0)
                {
                    after = LexerState.BlockComment;
                    return line.Length;
                }
                after = LexerState.Normal;
                return i + close + 2;
            }
            default:
            {
                var quote = inside switch
                {
                    LexerState.SingleQuoted => '\'',
                    LexerState.DoubleQuoted => '"',
                    _ => '`',
                };
                var backslash = inside != LexerState.Backtick;
                while (i < line.Length)
                {
                    var c = line[i];
                    if (backslash && c == '\\')
                    {
                        i += 2;
                        continue;
                    }
                    if (c == quote)
                    {
                        if (At(line, i + 1) == quote)
                        {
                            i += 2;
                            continue;
                        }
                        after = LexerState.Normal;
                        return i + 1;
                    }
                    i++;
                }
                after = inside;
                return line.Length;
            }
        }
    }

    private static int ScanNumber(ReadOnlySpan<char> line, int i)
    {
        var start = i;
        if (line[i] == '0' && (At(line, i + 1) is 'x' or 'X') && char.IsAsciiHexDigit(At(line, i + 2)))
        {
            i += 2;
            while (i < line.Length && char.IsAsciiHexDigit(line[i]))
            {
                i++;
            }
        }
        else
        {
            while (i < line.Length && char.IsAsciiDigit(line[i]))
            {
                i++;
            }
            if (At(line, i) == '.' && (i == start || char.IsAsciiDigit(At(line, i + 1)) || !IsIdentifierChar(At(line, i + 1))))
            {
                i++;
                while (i < line.Length && char.IsAsciiDigit(line[i]))
                {
                    i++;
                }
            }
            if ((At(line, i) is 'e' or 'E')
                && (char.IsAsciiDigit(At(line, i + 1)) || (At(line, i + 1) is '+' or '-' && char.IsAsciiDigit(At(line, i + 2)))))
            {
                i += 2;
                while (i < line.Length && char.IsAsciiDigit(line[i]))
                {
                    i++;
                }
            }
        }
        // Em MySQL, "1abc" é um identificador, não um número seguido de texto.
        return i < line.Length && IsIdentifierChar(line[i]) ? start : i;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';

    private static char At(ReadOnlySpan<char> line, int i) => (uint)i < (uint)line.Length ? line[i] : '\0';
}
