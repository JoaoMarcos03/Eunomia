using System.Globalization;

namespace Eunomia.Engine.Results;

/// <summary>
/// Display text of cells (section 15.2): thousands separator, DECIMAL decimal places, FLOAT and DOUBLE with
/// every significant digit, BIT as binary digits, dates, <c>(NULL)</c> and the <c>" [...]"</c> suffix on cut
/// text (appendix D). No mutable state. The text to save, copy and export comes from <see cref="FormatRaw"/>.
/// </summary>
public sealed class DisplayFormatter
{
    public const string NullText = "(NULL)";
    public const string TruncatedSuffix = " [...]";

    private static readonly long[] Pow10 =
    [
        1, 10, 100, 1_000, 10_000, 100_000, 1_000_000, 10_000_000, 100_000_000, 1_000_000_000,
        10_000_000_000, 100_000_000_000, 1_000_000_000_000,
    ];

    private readonly CultureInfo _culture;
    private readonly string[] _decimalFormats;

    /// <param name="culture">Numeric separators; HeidiSQL uses the system's. By default, the current culture.</param>
    /// <param name="thousandsSeparator">Show the thousands separator (on by default).</param>
    public DisplayFormatter(CultureInfo? culture = null, bool thousandsSeparator = true)
    {
        _culture = culture ?? CultureInfo.CurrentCulture;
        ThousandsSeparator = thousandsSeparator;
        var group = thousandsSeparator ? "#,0" : "0";
        _decimalFormats = new string[Pow10.Length];
        for (var s = 0; s < Pow10.Length; s++)
        {
            _decimalFormats[s] = s == 0 ? group : group + "." + new string('0', s);
        }
        IntegerFormat = group;
    }

    public bool ThousandsSeparator { get; }

    private string IntegerFormat { get; }

    public string Format(RowBlock block, int row, int column)
    {
        if (block.IsNull(row, column))
        {
            return NullText;
        }
        var info = block.Schema[column];
        switch (info.Storage)
        {
            case StorageKind.Int64 when IsBit(info):
                return BitText(block.GetInt64(row, column), info);
            case StorageKind.Int64:
                return block.GetInt64(row, column).ToString(IntegerFormat, _culture);
            case StorageKind.Decimal:
                var scale = Math.Clamp(info.Scale, 0, Pow10.Length - 1);
                var value = (decimal)block.GetDecimalUnscaled(row, column) / Pow10[scale];
                return value.ToString(_decimalFormats[scale], _culture);
            case StorageKind.Double:
                return LocalizeNumber(RoundTrip(block.GetDouble(row, column), info));
            case StorageKind.DateTime:
                var date = block.GetDateTime(row, column);
                if (info.NativeType.StartsWith("DATE", StringComparison.OrdinalIgnoreCase) && !info.NativeType.StartsWith("DATETIME", StringComparison.OrdinalIgnoreCase))
                {
                    return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                }
                return info.Scale is > 0 and <= 7
                    ? date.ToString(DateTimeFormats[info.Scale], CultureInfo.InvariantCulture)
                    : date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            case StorageKind.Binary:
                return FormatBinary(block.GetBytes(row, column), block.IsTruncated(row, column));
            default:
                var text = block.GetString(row, column);
                return block.IsTruncated(row, column) ? text + TruncatedSuffix : text;
        }
    }

    /// <summary>Bytes shown in the grid: hex with <c>0x</c>, at most <see cref="MaxDisplayBytes"/> bytes.</summary>
    public const int MaxDisplayBytes = 256;

    private static readonly string[] DateTimeFormats =
    [
        "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.f", "yyyy-MM-dd HH:mm:ss.ff", "yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-dd HH:mm:ss.ffff",
        "yyyy-MM-dd HH:mm:ss.fffff", "yyyy-MM-dd HH:mm:ss.ffffff", "yyyy-MM-dd HH:mm:ss.fffffff",
    ];

    private static readonly string[] RawDecimalFormats = [.. Enumerable.Range(0, 13).Select(s => s == 0 ? "0" : "0." + new string('0', s))];

    /// <summary>
    /// Lossless raw text of a cell, to edit, copy, duplicate, export and put in a WHERE: numbers without separators and
    /// with every digit (FLOAT and DOUBLE with the shortest value that round-trips to the same number), BIT as binary digits
    /// (the form the DML reads as <c>b'…'</c>), dates as displayed, full binary as <c>0x…</c>, text without the cut
    /// suffix; <c>null</c> for NULL. A cell cut in the SELECT (<c>LEFT(…, 256)</c>) stays partial: whoever saves
    /// has to fetch the full value (<see cref="RowBlock.IsTruncated"/>).
    /// </summary>
    public static string? FormatRaw(RowBlock block, int row, int column)
    {
        ArgumentNullException.ThrowIfNull(block);
        if (block.IsNull(row, column))
        {
            return null;
        }
        var info = block.Schema[column];
        switch (info.Storage)
        {
            case StorageKind.Int64 when IsBit(info):
                return BitText(block.GetInt64(row, column), info);
            case StorageKind.Int64:
                return block.GetInt64(row, column).ToString(CultureInfo.InvariantCulture);
            case StorageKind.Decimal:
                var scale = Math.Clamp(info.Scale, 0, Pow10.Length - 1);
                return ((decimal)block.GetDecimalUnscaled(row, column) / Pow10[scale]).ToString(RawDecimalFormats[scale], CultureInfo.InvariantCulture);
            case StorageKind.Double:
                return RoundTrip(block.GetDouble(row, column), info);
            case StorageKind.Binary:
                return "0x" + Convert.ToHexString(block.GetBytes(row, column));
            case StorageKind.DateTime:
                return Invariant.Format(block, row, column);
            default:
                return block.GetString(row, column);
        }
    }

    private static readonly DisplayFormatter Invariant = new(CultureInfo.InvariantCulture, thousandsSeparator: false);

    /// <summary>BIT column (stored as an integer).</summary>
    public static bool IsBit(ColumnInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return info.Storage == StorageKind.Int64 && info.NativeType.StartsWith("BIT", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Binary digits of a BIT, zero-padded to the declared width (<c>BIT(4)</c> → <c>0101</c>).</summary>
    public static string BitText(long value, ColumnInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var digits = Convert.ToString(value, 2);
        var open = info.NativeType.IndexOf('(', StringComparison.Ordinal);
        var close = info.NativeType.IndexOf(')', StringComparison.Ordinal);
        if (open > 0 && close > open && int.TryParse(info.NativeType.AsSpan(open + 1, close - open - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
            && width is > 0 and <= 64 && digits.Length < width)
        {
            digits = digits.PadLeft(width, '0');
        }
        return digits;
    }

    /// <summary>Shortest value that round-trips to the same number; FLOAT (read as double) goes back to float first.</summary>
    private static string RoundTrip(double value, ColumnInfo info) =>
        info.NativeType.StartsWith("FLOAT", StringComparison.OrdinalIgnoreCase)
            ? ((float)value).ToString("R", CultureInfo.InvariantCulture)
            : value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Number as invariant text with the culture's decimal separator and, if on, its thousands separator.</summary>
    private string LocalizeNumber(string invariant)
    {
        var format = _culture.NumberFormat;
        if (invariant.Contains('E', StringComparison.Ordinal) || invariant.Contains("Infinity", StringComparison.Ordinal) || invariant == "NaN")
        {
            return invariant.Replace(".", format.NumberDecimalSeparator, StringComparison.Ordinal);
        }
        var negative = invariant.StartsWith('-');
        var digits = negative ? invariant[1..] : invariant;
        var dot = digits.IndexOf('.', StringComparison.Ordinal);
        var integer = dot < 0 ? digits : digits[..dot];
        var fraction = dot < 0 ? string.Empty : digits[(dot + 1)..];
        var sb = new System.Text.StringBuilder(invariant.Length + 8);
        if (negative)
        {
            sb.Append(format.NegativeSign);
        }
        for (var i = 0; i < integer.Length; i++)
        {
            if (ThousandsSeparator && i > 0 && (integer.Length - i) % 3 == 0)
            {
                sb.Append(format.NumberGroupSeparator);
            }
            sb.Append(integer[i]);
        }
        if (fraction.Length > 0)
        {
            sb.Append(format.NumberDecimalSeparator).Append(fraction);
        }
        return sb.ToString();
    }

    public static string FormatBinary(ReadOnlySpan<byte> bytes, bool truncated)
    {
        var shown = bytes.Length > MaxDisplayBytes ? bytes[..MaxDisplayBytes] : bytes;
        var text = "0x" + Convert.ToHexString(shown);
        return truncated || bytes.Length > MaxDisplayBytes ? text + TruncatedSuffix : text;
    }

    /// <summary>Formats a whole column of a block.</summary>
    public string[] FormatColumn(RowBlock block, int column)
    {
        var cells = new string[block.RowCount];
        for (var r = 0; r < cells.Length; r++)
        {
            cells[r] = Format(block, r, column);
        }
        return cells;
    }
}
