using System.Buffers;
using System.Globalization;
using Eunomia.Engine.Results;

namespace Eunomia.Engine.Transfer;

/// <summary>Como um valor de uma coluna é escrito num INSERT (anexo B §2.3.4).</summary>
internal enum SqlLiteralKind
{
    Integer,
    Bit,
    Decimal,
    Double,
    Float,
    Date,
    DateTime,
    Binary,
    NumericText,
    Text,
}

/// <summary>
/// Literais SQL do MySQL/MariaDB escritos diretamente em UTF-8, sem uma string por valor (anexo B §2.3.4): NULL,
/// números sem aspas, BIT como <c>b'…'</c>, binário e geometria como <c>_binary 0x…</c> (vazio como <c>''</c>), texto
/// entre plicas com <c>\\</c>, <c>\'</c>, <c>\r</c>, <c>\n</c> e <c>\0</c>. FLOAT e DOUBLE com o valor mais curto que
/// volta ao mesmo número.
/// </summary>
internal static class SqlLiteralWriter
{
    private static readonly SearchValues<byte> Special = SearchValues.Create("\\'\r\n\0"u8);
    private static readonly byte[] HexDigits = "0123456789ABCDEF"u8.ToArray();

    public static SqlLiteralKind KindOf(ColumnInfo column)
    {
        ArgumentNullException.ThrowIfNull(column);
        var type = column.NativeType;
        return column.Storage switch
        {
            StorageKind.Int64 when type.StartsWith("BIT", StringComparison.OrdinalIgnoreCase) => SqlLiteralKind.Bit,
            StorageKind.Int64 => SqlLiteralKind.Integer,
            StorageKind.Decimal => SqlLiteralKind.Decimal,
            StorageKind.Double when type.StartsWith("FLOAT", StringComparison.OrdinalIgnoreCase) => SqlLiteralKind.Float,
            StorageKind.Double => SqlLiteralKind.Double,
            StorageKind.DateTime when type.StartsWith("DATE", StringComparison.OrdinalIgnoreCase)
                && !type.StartsWith("DATETIME", StringComparison.OrdinalIgnoreCase) => SqlLiteralKind.Date,
            StorageKind.DateTime => SqlLiteralKind.DateTime,
            StorageKind.Binary => SqlLiteralKind.Binary,
            _ when column.IsNumeric => SqlLiteralKind.NumericText,
            _ => SqlLiteralKind.Text,
        };
    }

    public static void Write(ByteBuilder output, RowBlock block, int row, int column, SqlLiteralKind kind, int scale)
    {
        if (block.IsNull(row, column))
        {
            output.Append("NULL"u8);
            return;
        }
        switch (kind)
        {
            case SqlLiteralKind.Integer:
                Format(output, block.GetInt64(row, column));
                break;
            case SqlLiteralKind.Bit:
                output.Append("b'"u8);
                var bits = (ulong)block.GetInt64(row, column);
                var digits = bits == 0 ? 1 : 64 - System.Numerics.BitOperations.LeadingZeroCount(bits);
                var span = output.GetSpan(digits);
                for (var i = 0; i < digits; i++)
                {
                    span[i] = (byte)('0' + (int)((bits >> (digits - 1 - i)) & 1));
                }
                output.Advance(digits);
                output.Append((byte)'\'');
                break;
            case SqlLiteralKind.Decimal:
                WriteDecimal(output, block.GetDecimalUnscaled(row, column), scale);
                break;
            case SqlLiteralKind.Double:
                Format(output, block.GetDouble(row, column));
                break;
            case SqlLiteralKind.Float:
                Format(output, (float)block.GetDouble(row, column));
                break;
            case SqlLiteralKind.Date:
                output.Append((byte)'\'');
                Format(output, block.GetDateTime(row, column), "yyyy-MM-dd");
                output.Append((byte)'\'');
                break;
            case SqlLiteralKind.DateTime:
                output.Append((byte)'\'');
                Format(output, block.GetDateTime(row, column), scale is > 0 and <= 7 ? "yyyy-MM-dd HH:mm:ss." + new string('f', scale) : "yyyy-MM-dd HH:mm:ss");
                output.Append((byte)'\'');
                break;
            case SqlLiteralKind.Binary:
                WriteBinary(output, block.GetBytes(row, column));
                break;
            case SqlLiteralKind.NumericText:
                var number = block.GetUtf8(row, column);
                if (number.IsEmpty)
                {
                    output.Append("''"u8);
                }
                else
                {
                    output.Append(number);
                }
                break;
            default:
                WriteText(output, block.GetUtf8(row, column));
                break;
        }
    }

    /// <summary>Texto entre plicas com os escapes do MySQL.</summary>
    public static void WriteText(ByteBuilder output, ReadOnlySpan<byte> utf8)
    {
        output.Append((byte)'\'');
        while (!utf8.IsEmpty)
        {
            var hit = utf8.IndexOfAny(Special);
            if (hit < 0)
            {
                output.Append(utf8);
                break;
            }
            output.Append(utf8[..hit]);
            output.Append((byte)'\\');
            output.Append(utf8[hit] switch
            {
                (byte)'\r' => (byte)'r',
                (byte)'\n' => (byte)'n',
                0 => (byte)'0',
                var b => b,
            });
            utf8 = utf8[(hit + 1)..];
        }
        output.Append((byte)'\'');
    }

    public static void WriteBinary(ByteBuilder output, ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            output.Append("''"u8);
            return;
        }
        output.Append("_binary 0x"u8);
        var span = output.GetSpan(bytes.Length * 2);
        for (var i = 0; i < bytes.Length; i++)
        {
            span[2 * i] = HexDigits[bytes[i] >> 4];
            span[(2 * i) + 1] = HexDigits[bytes[i] & 0xF];
        }
        output.Advance(bytes.Length * 2);
    }

    /// <summary>DECIMAL a partir do valor não escalado, com todas as casas da coluna (<c>-0.50</c>).</summary>
    public static void WriteDecimal(ByteBuilder output, long unscaled, int scale)
    {
        Span<byte> digits = stackalloc byte[24];
        var magnitude = unscaled < 0 ? (ulong)(-(unscaled + 1)) + 1 : (ulong)unscaled;
        magnitude.TryFormat(digits, out var count, default, CultureInfo.InvariantCulture);
        if (unscaled < 0)
        {
            output.Append((byte)'-');
        }
        if (scale <= 0)
        {
            output.Append(digits[..count]);
            return;
        }
        if (count <= scale)
        {
            output.Append((byte)'0');
            output.Append((byte)'.');
            for (var i = count; i < scale; i++)
            {
                output.Append((byte)'0');
            }
            output.Append(digits[..count]);
            return;
        }
        output.Append(digits[..(count - scale)]);
        output.Append((byte)'.');
        output.Append(digits[(count - scale)..count]);
    }

    private static void Format<T>(ByteBuilder output, T value, string? format = null)
        where T : IUtf8SpanFormattable
    {
        var span = output.GetSpan(64);
        if (!value.TryFormat(span, out var written, format, CultureInfo.InvariantCulture))
        {
            throw new InvalidOperationException("Value too long.");
        }
        output.Advance(written);
    }
}
