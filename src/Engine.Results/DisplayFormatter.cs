using System.Globalization;

namespace Eunomia.Engine.Results;

/// <summary>
/// Texto de display das células (secção 15.2): separador de milhares, casas decimais de DECIMAL, FLOAT e DOUBLE com
/// todos os dígitos significativos, BIT em dígitos binários, datas, <c>(NULL)</c> e sufixo <c>" [...]"</c> em texto
/// cortado (anexo D). Sem estado mutável. O texto para gravar, copiar e exportar é o de <see cref="FormatRaw"/>.
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

    /// <param name="culture">Separadores numéricos; o HeidiSQL usa os do sistema. Por omissão, a cultura atual.</param>
    /// <param name="thousandsSeparator">Mostrar separador de milhares (ligado por omissão).</param>
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

    /// <summary>Bytes mostrados no grid: hex com <c>0x</c>, no máximo <see cref="MaxDisplayBytes"/> bytes.</summary>
    public const int MaxDisplayBytes = 256;

    private static readonly string[] DateTimeFormats =
    [
        "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.f", "yyyy-MM-dd HH:mm:ss.ff", "yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-dd HH:mm:ss.ffff",
        "yyyy-MM-dd HH:mm:ss.fffff", "yyyy-MM-dd HH:mm:ss.ffffff", "yyyy-MM-dd HH:mm:ss.fffffff",
    ];

    private static readonly string[] RawDecimalFormats = [.. Enumerable.Range(0, 13).Select(s => s == 0 ? "0" : "0." + new string('0', s))];

    /// <summary>
    /// Texto bruto e sem perdas de uma célula, para editar, copiar, duplicar, exportar e pôr num WHERE: números sem
    /// separadores e com todos os dígitos (FLOAT e DOUBLE com o valor mais curto que volta ao mesmo número), BIT em dígitos
    /// binários (a forma que o DML lê como <c>b'…'</c>), datas como no display, binário completo em <c>0x…</c>, texto sem
    /// o sufixo de cortado; <c>null</c> se for NULL. Uma célula cortada no SELECT (<c>LEFT(…, 256)</c>) continua parcial:
    /// quem grava tem de ir buscar o valor completo (<see cref="RowBlock.IsTruncated"/>).
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

    /// <summary>Coluna BIT (guardada como inteiro).</summary>
    public static bool IsBit(ColumnInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return info.Storage == StorageKind.Int64 && info.NativeType.StartsWith("BIT", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Dígitos binários de um BIT, com zeros à esquerda até à largura declarada (<c>BIT(4)</c> → <c>0101</c>).</summary>
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

    /// <summary>Valor mais curto que volta ao mesmo número; o FLOAT (lido como double) volta a float primeiro.</summary>
    private static string RoundTrip(double value, ColumnInfo info) =>
        info.NativeType.StartsWith("FLOAT", StringComparison.OrdinalIgnoreCase)
            ? ((float)value).ToString("R", CultureInfo.InvariantCulture)
            : value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Número em texto invariante com o separador decimal e, se ligado, o de milhares da cultura.</summary>
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

    /// <summary>Formata uma coluna inteira de um bloco.</summary>
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
