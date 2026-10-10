using System.Globalization;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Results;

namespace Eunomia.App.Search;

/// <summary>Como o texto se compara com cada coluna (Find text on server › Match type).</summary>
public enum ServerTextMatch
{
    /// <summary>"Left and right wildcard": <c>LIKE '%texto%'</c>.</summary>
    Contains,

    /// <summary>"Exact match": <c>LIKE 'texto'</c>; nas colunas numéricas, <c>= número</c>.</summary>
    Exact,

    /// <summary>"Left wildcard": <c>LIKE '%texto'</c>.</summary>
    EndsWith,

    /// <summary>"Right wildcard": <c>LIKE 'texto%'</c>.</summary>
    StartsWith,

    /// <summary>"Regular expression": <c>REGEXP 'texto'</c>.</summary>
    Regex,
}

/// <summary>Opções do Find text on server (anexo B §2.4).</summary>
/// <param name="Text">Texto simples, ou o predicado SQL quando <paramref name="SqlPredicate"/>.</param>
/// <param name="SqlPredicate">Tab "SQL": o texto é um predicado aplicado a cada coluna (<c>col &gt; NOW()</c>).</param>
/// <param name="Category">"Search in column types": só as colunas desta categoria; <c>null</c> = todas.</param>
public sealed record ServerTextSearchOptions(string Text, bool SqlPredicate, TypeCategory? Category, ServerTextMatch Match, bool CaseSensitive);

/// <summary>Resultado de uma tabela: linhas encontradas e relevância (percentagem das linhas da tabela).</summary>
public sealed record ServerTextSearchHit(DbObjectRef Table, long FoundRows, long? TotalRows)
{
    public double? Relevance => TotalRows is > 0 ? 100.0 * FoundRows / TotalRows.Value : null;
}

/// <summary>
/// Find text on server (Ctrl+Shift+F; anexo B §2.4): uma consulta por tabela com a condição em todas as colunas da
/// categoria pedida, ligadas por <c>OR</c>. Sem distinção de maiúsculas, <c>LOWER(CONVERT(col USING utf8mb4)) LIKE</c>;
/// com, <c>LIKE BINARY</c>; regex com <c>REGEXP</c> (<c>REGEXP BINARY</c>). "Exact match" numa coluna numérica com um
/// texto numérico compara como número.
/// </summary>
public static class ServerTextSearch
{
    /// <summary>Condição para a tabela, ou <c>null</c> se nenhuma coluna entrar na pesquisa.</summary>
    public static string? Where(IDialect dialect, TableDefinition table, ServerTextSearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrEmpty(options.Text))
        {
            return null;
        }
        var conditions = table.Columns
            .Where(c => options.Category is null || c.Type.Category == options.Category)
            .Select(c => Condition(dialect, c, options))
            .Where(c => c is not null)
            .ToList();
        return conditions.Count == 0 ? null : string.Join(" OR ", conditions);
    }

    /// <summary><c>SELECT COUNT(*)</c> da tabela com a condição, ou <c>null</c> sem colunas.</summary>
    public static string? CountQuery(IDialect dialect, TableDefinition table, ServerTextSearchOptions options) =>
        Where(dialect, table, options) is { } where ? $"SELECT COUNT(*) FROM {dialect.QualifiedName(table.Ref)} WHERE {where}" : null;

    /// <summary>"See results": <c>SELECT *</c> da tabela com a condição.</summary>
    public static string? SelectQuery(IDialect dialect, TableDefinition table, ServerTextSearchOptions options) =>
        Where(dialect, table, options) is { } where ? $"SELECT * FROM {dialect.QualifiedName(table.Ref)} WHERE {where};" : null;

    private static string? Condition(IDialect dialect, ColumnDefinition column, ServerTextSearchOptions options)
    {
        var col = dialect.QuoteIdentifier(column.Name);
        if (options.SqlPredicate)
        {
            return $"{col} {options.Text}";
        }
        if (options.Match == ServerTextMatch.Regex)
        {
            return $"{col} REGEXP {(options.CaseSensitive ? "BINARY " : string.Empty)}{dialect.StringLiteral(options.Text)}";
        }
        var numeric = column.Type.Category is TypeCategory.Integer or TypeCategory.Real;
        if (options.Match == ServerTextMatch.Exact && numeric)
        {
            return decimal.TryParse(options.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                ? $"{col} = {number.ToString(CultureInfo.InvariantCulture)}"
                : null;
        }
        // O texto procura-se letra a letra: % e _ do utilizador não são curingas.
        var before = options.Match is ServerTextMatch.Contains or ServerTextMatch.EndsWith;
        var after = options.Match is ServerTextMatch.Contains or ServerTextMatch.StartsWith;
        return options.CaseSensitive
            ? $"{col} LIKE BINARY {Data.DataFilters.LikePattern(dialect, options.Text, before, after)}"
            : $"LOWER(CONVERT({col} USING utf8mb4)) LIKE {Data.DataFilters.LikePattern(dialect, options.Text.ToLowerInvariant(), before, after)}";
    }
}
