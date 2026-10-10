using System.Globalization;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Results;

namespace Eunomia.App.Search;

/// <summary>How the text is compared with each column (Find text on server › Match type).</summary>
public enum ServerTextMatch
{
    /// <summary>"Left and right wildcard": <c>LIKE '%text%'</c>.</summary>
    Contains,

    /// <summary>"Exact match": <c>LIKE 'text'</c>; on numeric columns, <c>= number</c>.</summary>
    Exact,

    /// <summary>"Left wildcard": <c>LIKE '%text'</c>.</summary>
    EndsWith,

    /// <summary>"Right wildcard": <c>LIKE 'text%'</c>.</summary>
    StartsWith,

    /// <summary>"Regular expression": <c>REGEXP 'text'</c>.</summary>
    Regex,
}

/// <summary>Find text on server options (appendix B §2.4).</summary>
/// <param name="Text">Plain text, or the SQL predicate when <paramref name="SqlPredicate"/>.</param>
/// <param name="SqlPredicate">"SQL" tab: the text is a predicate applied to each column (<c>col &gt; NOW()</c>).</param>
/// <param name="Category">"Search in column types": only the columns of this category; <c>null</c> = all.</param>
public sealed record ServerTextSearchOptions(string Text, bool SqlPredicate, TypeCategory? Category, ServerTextMatch Match, bool CaseSensitive);

/// <summary>Result for one table: matching rows and relevance (percentage of the table's rows).</summary>
public sealed record ServerTextSearchHit(DbObjectRef Table, long FoundRows, long? TotalRows)
{
    public double? Relevance => TotalRows is > 0 ? 100.0 * FoundRows / TotalRows.Value : null;
}

/// <summary>
/// Find text on server (Ctrl+Shift+F; appendix B §2.4): one query per table with the condition on every column of the
/// requested category, joined by <c>OR</c>. Case-insensitive: <c>LOWER(CONVERT(col USING utf8mb4)) LIKE</c>;
/// case-sensitive: <c>LIKE BINARY</c>; regex with <c>REGEXP</c> (<c>REGEXP BINARY</c>). "Exact match" on a numeric column
/// with numeric text compares as a number.
/// </summary>
public static class ServerTextSearch
{
    /// <summary>Condition for the table, or <c>null</c> if no column takes part in the search.</summary>
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

    /// <summary><c>SELECT COUNT(*)</c> of the table with the condition, or <c>null</c> without columns.</summary>
    public static string? CountQuery(IDialect dialect, TableDefinition table, ServerTextSearchOptions options) =>
        Where(dialect, table, options) is { } where ? $"SELECT COUNT(*) FROM {dialect.QualifiedName(table.Ref)} WHERE {where}" : null;

    /// <summary>"See results": <c>SELECT *</c> of the table with the condition.</summary>
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
        // The text is matched character by character: the user's % and _ are not wildcards.
        var before = options.Match is ServerTextMatch.Contains or ServerTextMatch.EndsWith;
        var after = options.Match is ServerTextMatch.Contains or ServerTextMatch.StartsWith;
        return options.CaseSensitive
            ? $"{col} LIKE BINARY {Data.DataFilters.LikePattern(dialect, options.Text, before, after)}"
            : $"LOWER(CONVERT({col} USING utf8mb4)) LIKE {Data.DataFilters.LikePattern(dialect, options.Text.ToLowerInvariant(), before, after)}";
    }
}
