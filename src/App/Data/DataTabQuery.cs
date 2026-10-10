using System.Globalization;
using System.Text;
using Eunomia.Drivers.Abstractions;

namespace Eunomia.App.Data;

/// <summary>A column of the Data tab ORDER BY.</summary>
public sealed record DataSort(string Column, bool Descending);

/// <summary>SELECT of a Data tab page and the columns it returns (source names, in SELECT order).</summary>
public sealed record DataSelect(string Sql, IReadOnlyList<string> Columns, IReadOnlySet<string> TruncatedColumns, IReadOnlyList<string> KeyColumns);

/// <summary>
/// Data tab SELECT, like HeidiSQL's <c>DataGridBeforePaint</c> (appendix D B2, B3): hidden columns are left out unless
/// they are part of the key or the table has no key; long columns cut at 256 only with a key; <c>*</c> when nothing is
/// cut or hidden; the filter's <c>WHERE</c>, the accumulated <c>ORDER BY</c> and <c>LIMIT off, n</c>.
/// </summary>
public static class DataTabQuery
{
    /// <summary><c>GRIDMAXDATA</c>: fixed limit of the text loaded per cell.</summary>
    public const int MaxData = 256;

    /// <summary>Rows per step (<c>DatagridRowsPerStep</c>) and Show all maximum (<c>DatagridMaximumRows</c>).</summary>
    public const int DefaultRowsPerStep = 1000;

    public const int DefaultMaximumRows = 100_000;

    /// <summary>Key columns: the PK; otherwise the first UNIQUE whose columns are all NOT NULL; otherwise none.</summary>
    public static IReadOnlyList<string> KeyColumns(TableDefinition table)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (table.Indexes.FirstOrDefault(i => i.Kind == IndexKind.Primary) is { } primary)
        {
            return primary.Columns;
        }
        var nullable = table.Columns.Where(c => c.IsNullable).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return table.Indexes.FirstOrDefault(i => i.Kind == IndexKind.Unique && i.Columns.All(c => !nullable.Contains(c)))?.Columns ?? [];
    }

    /// <summary>Filter text for the <c>WHERE</c>: without a leading <c>WHERE</c>; <c>null</c> if it only has whitespace or comments.</summary>
    public static string? NormalizeFilter(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(StripComments(text)))
        {
            return null;
        }
        var trimmed = text.Trim();
        if (trimmed.StartsWith("WHERE", StringComparison.OrdinalIgnoreCase) && (trimmed.Length == 5 || char.IsWhiteSpace(trimmed[5])))
        {
            trimmed = trimmed[5..].TrimStart();
        }
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>
    /// Filter ready for the Data tab SQL: the one from <see cref="NormalizeFilter"/>, without the trailing <c>;</c>, and a
    /// single condition. The WHERE goes on the same connection that accepts several statements: a <c>;</c> in the middle
    /// (from pasted text, for example) would run the rest as another statement, so it is refused (<see cref="InvalidOperationException"/>).
    /// </summary>
    public static string? WhereClause(string? text)
    {
        if (NormalizeFilter(text) is not { } filter)
        {
            return null;
        }
        if (FilterError(filter) is { } error)
        {
            throw new InvalidOperationException(error);
        }
        return Eunomia.Engine.Sql.Splitting.SqlSplitter.Split(filter) is [{ } single] ? single.Text : filter;
    }

    /// <summary>Error for a filter with more than one statement (a <c>;</c> outside strings and comments), or <c>null</c>.</summary>
    public static string? FilterError(string? text) =>
        NormalizeFilter(text) is { } filter && Eunomia.Engine.Sql.Splitting.SqlSplitter.Split(filter).Count > 1
            ? "The filter must be a single WHERE condition: remove the \";\" that separates it into several statements."
            : null;

    private static string StripComments(string text)
    {
        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '#' || (text[i] == '-' && i + 1 < text.Length && text[i + 1] == '-'))
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }
            }
            else if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 1;
            }
            else
            {
                result.Append(text[i]);
            }
        }
        return result.ToString();
    }

    /// <summary>
    /// SELECT of <paramref name="count"/> rows starting at <paramref name="offset"/>. With <paramref name="fullRows"/>,
    /// nothing is cut (Export and Copy as run it again this way, appendix D B4).
    /// </summary>
    public static DataSelect Build(IDialect dialect, TableDefinition table, IReadOnlyCollection<string> hidden, string? filter,
        IReadOnlyList<DataSort> sort, long offset, int count, bool fullRows = false)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(hidden);
        ArgumentNullException.ThrowIfNull(sort);
        var keys = KeyColumns(table);
        var keySet = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hiddenSet = hidden.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var columns = new List<string>();
        var expressions = new List<string>();
        var truncated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var star = true;
        foreach (var column in table.Columns)
        {
            var isKey = keySet.Contains(column.Name);
            if (hiddenSet.Contains(column.Name) && !isKey && keys.Count > 0)
            {
                star = false;
                continue;
            }
            if (column.IsInvisible)
            {
                star = false;
            }
            var cut = !fullRows && keys.Count > 0 && !isKey ? dialect.LoadPartExpression(column, MaxData) : null;
            if (cut is not null)
            {
                truncated.Add(column.Name);
                star = false;
            }
            columns.Add(column.Name);
            expressions.Add(cut ?? dialect.QuoteIdentifier(column.Name));
        }

        var sql = new StringBuilder("SELECT ");
        sql.Append(star ? "*" : string.Join(", ", expressions));
        sql.Append(" FROM ").Append(dialect.QualifiedName(table.Ref));
        if (WhereClause(filter) is { } where)
        {
            sql.Append(" WHERE ").Append(where);
        }
        var known = table.Columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var order = sort.Where(s => known.Contains(s.Column)).ToList();
        if (order.Count > 0)
        {
            sql.Append(" ORDER BY ").Append(string.Join(", ", order.Select(s => dialect.QuoteIdentifier(s.Column) + (s.Descending ? " DESC" : " ASC"))));
        }
        sql.Append(' ').Append(dialect.Paging(offset, count));
        return new DataSelect(sql.ToString(), columns, truncated, keys);
    }

    /// <summary>Header click (appendix D B7): ASC → DESC → removed; a new column is appended (DESC with Shift).</summary>
    public static IReadOnlyList<DataSort> ToggleSort(IReadOnlyList<DataSort> sort, string column, bool shift)
    {
        ArgumentNullException.ThrowIfNull(sort);
        var list = sort.ToList();
        var index = list.FindIndex(s => string.Equals(s.Column, column, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            list.Add(new DataSort(column, shift));
        }
        else if (!list[index].Descending)
        {
            list[index] = list[index] with { Descending = true };
        }
        else
        {
            list.RemoveAt(index);
        }
        return list;
    }

    /// <summary><c>Sort</c> value saved per table: <c>&lt;order&gt;_&lt;column&gt;|…</c>, 0 = ASC.</summary>
    public static string FormatSort(IReadOnlyList<DataSort> sort)
    {
        ArgumentNullException.ThrowIfNull(sort);
        return string.Join("|", sort.Select(s => (s.Descending ? "1_" : "0_") + s.Column));
    }

    public static IReadOnlyList<DataSort> ParseSort(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return [];
        }
        var result = new List<DataSort>();
        foreach (var item in value.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var underscore = item.IndexOf('_', StringComparison.Ordinal);
            if (underscore > 0 && item.Length > underscore + 1)
            {
                result.Add(new DataSort(item[(underscore + 1)..], item[..underscore] != "0"));
            }
        }
        return result;
    }

    /// <summary><c>HiddenColumns</c> value saved per table, separated by <c>|</c>.</summary>
    public static string FormatHidden(IEnumerable<string> hidden) => string.Join("|", hidden);

    public static IReadOnlyList<string> ParseHidden(string? value) =>
        string.IsNullOrEmpty(value) ? [] : value.Split('|', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Data tab label (appendix D B6): "db.t: N rows total (exact)"; with a limit or a filter, the total comes from the
    /// approximate (or exact) count and gets ", limited to N" or ", N rows match to filter" appended. The (exact/approximately)
    /// suffix only on InnoDB. Views: "db.v: N rows".
    /// </summary>
    public static string CountLabel(string database, string table, DataCount count, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(count);
        culture ??= CultureInfo.CurrentCulture;
        string N(long n) => n.ToString("N0", culture);
        var head = $"{database}.{table}: ";
        if (count.IsView)
        {
            return head + N(count.Loaded) + " rows";
        }
        if (!count.Limited && !count.Filtered)
        {
            return head + N(count.Loaded) + " rows total" + (count.InnoDb ? " (exact)" : string.Empty);
        }
        var total = count.Total ?? count.Loaded;
        var text = head + N(total) + " rows total";
        if (count.InnoDb)
        {
            text += count.TotalExact ? " (exact)" : " (approximately)";
        }
        if (count.Filtered)
        {
            return text + (count.Loaded == total ? ", all rows match to filter" : $", {N(count.Loaded)} rows match to filter");
        }
        return text + ", limited to " + N(count.Loaded);
    }
}

/// <summary>Numbers for the Data tab label.</summary>
/// <param name="Loaded">Rows loaded in the grid.</param>
/// <param name="Limited">The SELECT may have left rows out (it loaded as many as it asked for).</param>
/// <param name="Filtered">There is a WHERE filter.</param>
/// <param name="Total">Table count (approximate from <c>SHOW TABLE STATUS</c>, or exact from <c>COUNT(*)</c>).</param>
/// <param name="TotalExact">The count is exact.</param>
/// <param name="InnoDb">InnoDB engine (the suffix only appears there).</param>
/// <param name="IsView">View: only the loaded rows.</param>
public sealed record DataCount(long Loaded, bool Limited, bool Filtered, long? Total, bool TotalExact, bool InnoDb, bool IsView = false);
