using System.Globalization;
using System.Text;
using Eunomia.Drivers.Abstractions;

namespace Eunomia.App.Data;

/// <summary>Operator of a Quick Filter item (appendix D B10).</summary>
public enum QuickFilterOperator
{
    Equal,
    NotEqual,
    StartsWith,
    EndsWith,
    Contains,
    Greater,
    Less,

    /// <summary>Clipboard: <c>col IN (&lt;text as is&gt;)</c>, without escaping.</summary>
    InVerbatim,
    IsNull,
    IsNotNull,
}

/// <summary>
/// SQL of the Data tab filters (appendix D B9, B10): multi-column filter (<c>LIKE '%x%'</c> on every column, OR) and
/// Quick Filter (values of the selected rows, a typed value, clipboard), with HeidiSQL's escaping: numbers unquoted,
/// binary as hex, BIT as <c>b'…'</c>, text quoted.
/// </summary>
public static partial class DataFilters
{
    /// <summary>Limit of the values collected from the selected rows (1 KB).</summary>
    public const int MaxValuesLength = 1024;

    /// <summary>
    /// "Create multi column filter": <c>CastAsText(col) LIKE '%text%'</c> for every column (hidden ones included),
    /// joined by OR with line breaks; <c>ILIKE</c> where the engine has it. Empty text gives an empty filter.
    /// </summary>
    public static string MultiColumn(IDialect dialect, IEnumerable<ColumnDefinition> columns, string text)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(columns);
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }
        var like = dialect.CaseInsensitiveLike == LikeOperator.ILike ? " ILIKE " : " LIKE ";
        var literal = dialect.StringLiteral("%" + text + "%");
        var result = new StringBuilder();
        var lineLength = 0;
        foreach (var column in columns)
        {
            var condition = dialect.CastAsText(column) + like + literal;
            if (result.Length > 0)
            {
                if (lineLength + condition.Length > 100)
                {
                    result.Append(" OR\n");
                    lineLength = 0;
                }
                else
                {
                    result.Append(" OR ");
                    lineLength += 4;
                }
            }
            result.Append(condition);
            lineLength += condition.Length;
        }
        return result.ToString();
    }

    /// <summary>
    /// List for the Quick Filter's "IN (clipboard)": the clipboard text split on commas, tabs and line breaks (quotes
    /// around an item are removed), each item as a literal of the column. It used to go into the SQL as is: pasted text
    /// with <c>)</c> and <c>;</c> closed the IN and ran another statement.
    /// </summary>
    public static string InList(IDialect dialect, ColumnDefinition column, string clipboard)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(clipboard);
        var items = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        void Flush()
        {
            var item = current.ToString().Trim();
            current.Clear();
            if (item.Length >= 2 && item[0] is '\'' or '"' && item[^1] == item[0])
            {
                var q = item[0];
                item = item[1..^1].Replace(new string(q, 2), q.ToString(), StringComparison.Ordinal);
            }
            if (item.Length > 0)
            {
                items.Add(Escape(dialect, column, item));
            }
        }
        foreach (var c in clipboard)
        {
            if (quote is { } open)
            {
                current.Append(c);
                if (c == open)
                {
                    quote = null;
                }
            }
            else if (c is '\'' or '"')
            {
                quote = c;
                current.Append(c);
            }
            else if (c is ',' or '\t' or '\n' or '\r')
            {
                Flush();
            }
            else
            {
                current.Append(c);
            }
        }
        Flush();
        return items.Count == 0 ? "NULL" : string.Join(", ", items);
    }

    /// <summary>
    /// LIKE pattern in which <paramref name="text"/> matches character by character (<c>%</c>, <c>_</c> and <c>!</c> escaped
    /// with <c>ESCAPE '!'</c>, which works under any <c>sql_mode</c>): <c>'%text%' ESCAPE '!'</c>. Without it, a value with
    /// <c>_</c> or <c>%</c> matched other rows and <c>C:\temp</c> was never found.
    /// </summary>
    public static string LikePattern(IDialect dialect, string text, bool anyBefore, bool anyAfter)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(text);
        var escaped = text.Replace("!", "!!", StringComparison.Ordinal).Replace("%", "!%", StringComparison.Ordinal).Replace("_", "!_", StringComparison.Ordinal);
        return dialect.StringLiteral((anyBefore ? "%" : string.Empty) + escaped + (anyAfter ? "%" : string.Empty)) + " ESCAPE '!'";
    }

    /// <summary>Value as an SQL literal according to the column type (<c>EscapeString(v, Datatype)</c>).</summary>
    public static string Escape(IDialect dialect, ColumnDefinition column, string value)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(value);
        switch (column.Type.Kind)
        {
            case LogicalTypeKind.Integer or LogicalTypeKind.Decimal or LogicalTypeKind.Float or LogicalTypeKind.Boolean
                when Number().IsMatch(value):
                return value;
            case LogicalTypeKind.Bit when value.All(c => c is '0' or '1') && value.Length > 0:
                return "b'" + value + "'";
            case LogicalTypeKind.Binary when value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && value.Length > 2 && value.Skip(2).All(char.IsAsciiHexDigit):
                return value;
            default:
                return dialect.StringLiteral(value);
        }
    }

    /// <summary>
    /// Quick Filter block 1: values of the focused column in every selected row (no duplicates, up to 1 KB).
    /// <c>=</c> gives <c>col IN (…)</c> (+ <c>OR col IS NULL</c>), <c>!=</c> gives <c>col NOT IN (…)</c> (+ <c>AND col IS NOT NULL</c>);
    /// the others join one condition per value with OR. <c>null</c> if there are no values for the operator.
    /// </summary>
    public static string? FromValues(IDialect dialect, ColumnDefinition column, IEnumerable<string?> values, QuickFilterOperator op)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(values);
        var distinct = new List<string>();
        var hasNull = false;
        var length = 0;
        foreach (var value in values)
        {
            if (value is null)
            {
                hasNull = true;
                continue;
            }
            if (distinct.Contains(value, StringComparer.Ordinal))
            {
                continue;
            }
            if (length + value.Length > MaxValuesLength && distinct.Count > 0)
            {
                break;
            }
            distinct.Add(value);
            length += value.Length;
        }
        var col = dialect.QuoteIdentifier(column.Name);
        switch (op)
        {
            case QuickFilterOperator.Equal:
                return distinct.Count == 0
                    ? hasNull ? col + " IS NULL" : null
                    : $"{col} IN ({string.Join(", ", distinct.Select(v => Escape(dialect, column, v)))})" + (hasNull ? $" OR {col} IS NULL" : string.Empty);
            case QuickFilterOperator.NotEqual:
                return distinct.Count == 0
                    ? hasNull ? col + " IS NOT NULL" : null
                    : $"{col} NOT IN ({string.Join(", ", distinct.Select(v => Escape(dialect, column, v)))})" + (hasNull ? $" AND {col} IS NOT NULL" : string.Empty);
            default:
                return distinct.Count == 0 ? null : string.Join(" OR ", distinct.Select(v => Single(dialect, column, op, v)));
        }
    }

    /// <summary>Quick Filter blocks 2 and 3: a typed value or one from the clipboard.</summary>
    public static string Single(IDialect dialect, ColumnDefinition column, QuickFilterOperator op, string value)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(value);
        var col = dialect.QuoteIdentifier(column.Name);
        return op switch
        {
            QuickFilterOperator.Equal => $"{col} = {Escape(dialect, column, value)}",
            QuickFilterOperator.NotEqual => $"{col} != {Escape(dialect, column, value)}",
            QuickFilterOperator.StartsWith => $"{col} LIKE {LikePattern(dialect, value, anyBefore: false, anyAfter: true)}",
            QuickFilterOperator.EndsWith => $"{col} LIKE {LikePattern(dialect, value, anyBefore: true, anyAfter: false)}",
            QuickFilterOperator.Contains => $"{col} LIKE {LikePattern(dialect, value, anyBefore: true, anyAfter: true)}",
            QuickFilterOperator.Greater => $"{col} > {Escape(dialect, column, value)}",
            QuickFilterOperator.Less => $"{col} < {Escape(dialect, column, value)}",
            QuickFilterOperator.InVerbatim => $"{col} IN ({InList(dialect, column, value)})",
            QuickFilterOperator.IsNull => col + " IS NULL",
            _ => col + " IS NOT NULL",
        };
    }

    /// <summary>
    /// Shift when choosing a Quick Filter: joins it to the current filter with AND, unless one already contains the other.
    /// Each part goes in parentheses when it has an OR, so the AND does not change its meaning.
    /// </summary>
    public static string Combine(string? existing, string added)
    {
        ArgumentNullException.ThrowIfNull(added);
        var current = DataTabQuery.NormalizeFilter(existing);
        if (current is null || current.Contains(added, StringComparison.Ordinal))
        {
            return current ?? added;
        }
        if (added.Contains(current, StringComparison.Ordinal))
        {
            return added;
        }
        return Wrap(current) + "\nAND " + Wrap(added);
    }

    private static string Wrap(string condition) =>
        condition.Contains(" OR ", StringComparison.OrdinalIgnoreCase) || condition.Contains("\nOR", StringComparison.OrdinalIgnoreCase) || condition.Contains("OR\n", StringComparison.OrdinalIgnoreCase)
            ? "(" + condition + ")"
            : condition;

    /// <summary>Caption of a menu item: the SQL, cut at 100 characters (<c>StrEllipsis(Hint, 100)</c>).</summary>
    public static string Caption(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var single = sql.Replace("\n", " ", StringComparison.Ordinal);
        return single.Length <= 100 ? single : single[..99] + "…";
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^[+-]?(\d+(\.\d*)?|\.\d+)([eE][+-]?\d+)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex Number();
}
