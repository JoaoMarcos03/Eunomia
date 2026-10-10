using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Eunomia.Engine.Transfer;

/// <summary>Handling of duplicate rows in the CSV import (appendix B §3.1; default REPLACE).</summary>
public enum CsvDuplicates
{
    Insert,
    InsertIgnore,
    Replace,
}

/// <summary>Target column of a CSV field (positional mapping: field k goes to the k-th column).</summary>
public sealed record CsvTargetColumn(string Name, bool IsNumeric);

/// <summary>
/// Multi-row INSERT of the CSV import's client mode (appendix B §3.3): <c>INSERT [LOW_PRIORITY] [IGNORE] INTO `db`.`t`
/// (cols) VALUES (…), (…)</c> or <c>REPLACE</c>, closed at 0.5 MiB or 10,000 rows. Missing fields at the end are
/// NULL and extra ones are ignored. The type comes from each field's target column (HeidiSQL uses the one at the original
/// position and mixes up the types when the columns are reordered).
/// </summary>
public sealed partial class CsvInsertBatcher
{
    public const int DefaultMaxBytes = 512 * 1024;
    public const int DefaultMaxRows = 10_000;

    private readonly IReadOnlyList<CsvTargetColumn> _columns;
    private readonly string _prefix;
    private readonly NumberFormatInfo? _localNumbers;
    private readonly int _maxBytes;
    private readonly int _maxRows;
    private readonly StringBuilder _sql = new();

    /// <param name="localNumbers">Format of local numbers (e.g. <c>1.234,56</c>); <c>null</c> without the option.</param>
    public CsvInsertBatcher(
        string database,
        string table,
        IReadOnlyList<CsvTargetColumn> columns,
        CsvDuplicates duplicates = CsvDuplicates.Replace,
        bool lowPriority = false,
        NumberFormatInfo? localNumbers = null,
        int maxBytes = DefaultMaxBytes,
        int maxRows = DefaultMaxRows)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count == 0)
        {
            throw new ArgumentException("At least one column is needed.", nameof(columns));
        }
        _columns = columns;
        _localNumbers = localNumbers;
        _maxBytes = maxBytes;
        _maxRows = Math.Max(1, maxRows);
        var keyword = duplicates == CsvDuplicates.Replace ? "REPLACE" : "INSERT";
        _prefix = keyword + (lowPriority ? " LOW_PRIORITY" : string.Empty) + (duplicates == CsvDuplicates.InsertIgnore ? " IGNORE" : string.Empty)
            + " INTO " + SqlDumpWriter.Quote(database) + "." + SqlDumpWriter.Quote(table)
            + " (" + string.Join(", ", columns.Select(c => SqlDumpWriter.Quote(c.Name))) + ") VALUES ";
    }

    /// <summary>Rows in the INSERT not yet sent.</summary>
    public int PendingRows { get; private set; }

    /// <summary>Adds a record; returns the previous INSERT when this one no longer fits in it.</summary>
    public string? Add(IReadOnlyList<string?> record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var row = new StringBuilder("(");
        for (var c = 0; c < _columns.Count; c++)
        {
            if (c > 0)
            {
                row.Append(", ");
            }
            AppendValue(row, c < record.Count ? record[c] : null, _columns[c].IsNumeric);
        }
        row.Append(')');

        string? done = null;
        if (PendingRows > 0 && (_sql.Length + row.Length + 2 > _maxBytes || PendingRows >= _maxRows))
        {
            done = Flush();
        }
        if (PendingRows == 0)
        {
            _sql.Append(_prefix);
        }
        else
        {
            _sql.Append(", ");
        }
        _sql.Append(row);
        PendingRows++;
        return done;
    }

    /// <summary>The INSERT not yet sent (end of file), or <c>null</c> if there are no rows.</summary>
    public string? Flush()
    {
        if (PendingRows == 0)
        {
            return null;
        }
        var sql = _sql.ToString();
        _sql.Clear();
        PendingRows = 0;
        return sql;
    }

    private void AppendValue(StringBuilder sb, string? value, bool numeric)
    {
        if (value is null)
        {
            sb.Append("NULL");
            return;
        }
        if (numeric)
        {
            var number = _localNumbers is null ? value.Trim() : UnformatNumber(value, _localNumbers);
            if (NumericLiteral().IsMatch(number))
            {
                sb.Append(number);
                return;
            }
        }
        AppendString(sb, value);
    }

    /// <summary>Local number in SQL format: without a thousands separator and with a decimal point.</summary>
    public static string UnformatNumber(string value, NumberFormatInfo format)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(format);
        var text = value.Trim();
        if (format.NumberGroupSeparator.Length > 0)
        {
            text = text.Replace(format.NumberGroupSeparator, string.Empty, StringComparison.Ordinal);
            // Many cultures use a non-breaking space as the thousands separator; the file may bring another kind of space.
            if (string.IsNullOrWhiteSpace(format.NumberGroupSeparator))
            {
                text = string.Concat(text.Where(c => c is not (' ' or ' ' or ' ')));
            }
        }
        return format.NumberDecimalSeparator == "." ? text : text.Replace(format.NumberDecimalSeparator, ".", StringComparison.Ordinal);
    }

    /// <summary>Single-quoted text with MySQL's escapes.</summary>
    internal static void AppendString(StringBuilder sb, string value)
    {
        sb.Append('\'');
        foreach (var c in value)
        {
            switch (c)
            {
                case '\'':
                    sb.Append("\\'");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\0':
                    sb.Append("\\0");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        sb.Append('\'');
    }

    [GeneratedRegex(@"^[+-]?([0-9]+\.?[0-9]*|\.[0-9]+)([eE][+-]?[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex NumericLiteral();
}
