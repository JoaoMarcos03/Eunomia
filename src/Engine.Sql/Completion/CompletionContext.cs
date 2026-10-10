using Eunomia.Engine.Sql.Lexing;
using Eunomia.Engine.Sql.Splitting;

namespace Eunomia.Engine.Sql.Completion;

/// <summary>O que o autocomplete deve listar no ponto do cursor.</summary>
public enum CompletionScope
{
    /// <summary>Dentro de uma string ou comentário: nada.</summary>
    Suppressed,

    /// <summary><c>@@SESSION.</c> ou <c>@@GLOBAL.</c>: variáveis do servidor.</summary>
    Variables,

    /// <summary><c>x.</c> ou <c>db.tbl.</c>: colunas ou objetos do qualificador.</summary>
    Qualified,

    /// <summary>Sem qualificador: colunas da tabela selecionada, bases, objetos, funções e keywords.</summary>
    General,
}

/// <summary>Tabela referida na instrução atual (<c>FROM</c>, <c>JOIN</c>, <c>INTO</c>, <c>UPDATE</c>), com o alias.</summary>
public sealed record TableReference(string? Database, string Table, string? Alias);

/// <summary>
/// Contexto do autocomplete A (secção 16.1; anexo C §4.2): a palavra a substituir, os qualificadores antes dela e as
/// tabelas da instrução atual, para resolver aliases.
/// </summary>
/// <param name="Scope">O que listar.</param>
/// <param name="ReplaceStart">Início do texto que a sugestão substitui (inclui a crase inicial, se houver).</param>
/// <param name="Typed">O que já foi escrito da palavra, sem a crase.</param>
/// <param name="Quoted">A palavra começa por uma crase: a sugestão entra sempre entre crases.</param>
/// <param name="Qualifiers">Nomes antes do ponto: <c>[alias]</c>, <c>[db]</c> ou <c>[db, tbl]</c>.</param>
/// <param name="GlobalVariables"><c>@@GLOBAL.</c> em vez de <c>@@SESSION.</c>.</param>
/// <param name="Tables">Tabelas da instrução atual.</param>
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
    /// Analisa o texto do editor no cursor: suprimido dentro de strings e comentários; <c>@@SESSION.</c> /
    /// <c>@@GLOBAL.</c>; qualificadores <c>a.</c> e <c>a.b.</c>; e as tabelas de <c>FROM|INTO|UPDATE … (WHERE|HAVING|
    /// ORDER|GROUP)</c> da instrução que contém o cursor, com os JOINs como vírgulas e os pares <c>tbl [AS] alias</c>.
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

        // Qualificadores: até dois nomes separados por pontos antes da palavra.
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

    /// <summary>Nome simples ou entre crases que acaba antes de <paramref name="end"/>.</summary>
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

    /// <summary>O cursor está dentro de uma string ou de um comentário (o lexer corre até ao cursor).</summary>
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
                // Comentário de linha até ao cursor (um bloco fechado antes do cursor não conta).
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

    /// <summary>Fecham a lista de tabelas (uma vírgula depois deles já não separa tabelas).</summary>
    private static readonly HashSet<string> ClauseEnd = new(StringComparer.OrdinalIgnoreCase)
    {
        "WHERE", "HAVING", "ORDER", "GROUP", "LIMIT", "SET", "VALUES", "VALUE", "SELECT", "UNION", "WINDOW", "FOR",
        "PROCEDURE", "LOCK", "PARTITION",
    };

    /// <summary>Não são nome de tabela nem alias: o fim da lista, as condições dos JOINs e os tipos de JOIN.</summary>
    private static readonly HashSet<string> TableListEnd = new(ClauseEnd.Concat(
        ["ON", "USING", "LEFT", "RIGHT", "INNER", "OUTER", "CROSS", "NATURAL", "FULL"]), StringComparer.OrdinalIgnoreCase);

    /// <summary>Tabelas e aliases de <c>FROM|JOIN|INTO|UPDATE</c> na instrução (sem olhar para subqueries à parte).</summary>
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
                // Vírgulas continuam a lista; parênteses são ignorados.
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
            // nome [. nome] [[AS] alias]
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
                // "db." ainda a ser escrito: não é uma tabela.
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

    /// <summary>A vírgula está numa lista de tabelas: a última palavra estrutural antes dela abriu uma.</summary>
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

    /// <summary>Tokens sem espaços nem comentários, com o texto.</summary>
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
