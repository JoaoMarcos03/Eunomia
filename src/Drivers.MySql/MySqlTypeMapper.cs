using System.Globalization;
using System.Text;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Results;

namespace Eunomia.Drivers.MySql;

/// <summary>
/// MySQL/MariaDB native types ↔ logical types (section 14.4). Reads the type as the server describes it in
/// <c>information_schema.COLUMNS.COLUMN_TYPE</c> (<c>decimal(12,2) unsigned</c>, <c>enum('a','b')</c>…).
/// </summary>
public sealed class MySqlTypeMapper : ITypeMapper
{
    public LogicalType Map(string nativeType)
    {
        ArgumentNullException.ThrowIfNull(nativeType);
        var (name, args, rest) = Split(nativeType.Trim());
        var unsigned = rest.Contains("unsigned", StringComparison.OrdinalIgnoreCase);
        int? Arg(int i) => i < args.Count && int.TryParse(args[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
        return name switch
        {
            "tinyint" => new LogicalType(LogicalTypeKind.Integer, Bits: 8, Signed: !unsigned),
            "smallint" => new LogicalType(LogicalTypeKind.Integer, Bits: 16, Signed: !unsigned),
            "mediumint" => new LogicalType(LogicalTypeKind.Integer, Bits: 24, Signed: !unsigned),
            "int" or "integer" => new LogicalType(LogicalTypeKind.Integer, Bits: 32, Signed: !unsigned),
            "bigint" => new LogicalType(LogicalTypeKind.Integer, Bits: 64, Signed: !unsigned),
            "decimal" or "numeric" or "dec" or "fixed" => new LogicalType(LogicalTypeKind.Decimal, Precision: Arg(0) ?? 10, Scale: Arg(1) ?? 0, Signed: !unsigned),
            "float" => new LogicalType(LogicalTypeKind.Float, Bits: 32, Signed: !unsigned),
            "double" or "real" or "double precision" => new LogicalType(LogicalTypeKind.Float, Bits: 64, Signed: !unsigned),
            "bit" => new LogicalType(LogicalTypeKind.Bit, Bits: Arg(0) ?? 1),
            "bool" or "boolean" => new LogicalType(LogicalTypeKind.Boolean),
            "char" or "varchar" or "tinytext" or "text" or "mediumtext" or "longtext" or "nchar" or "nvarchar" =>
                new LogicalType(LogicalTypeKind.Text, Length: TextLength(name, Arg(0))),
            "binary" or "varbinary" or "tinyblob" or "blob" or "mediumblob" or "longblob" =>
                new LogicalType(LogicalTypeKind.Binary, Length: TextLength(name, Arg(0))),
            "date" => new LogicalType(LogicalTypeKind.Date),
            "time" => new LogicalType(LogicalTypeKind.Time, FractionDigits: Arg(0) ?? 0),
            "datetime" or "timestamp" => new LogicalType(LogicalTypeKind.DateTime, FractionDigits: Arg(0) ?? 0),
            "year" => new LogicalType(LogicalTypeKind.Integer, Bits: 16, Signed: false),
            "json" => new LogicalType(LogicalTypeKind.Json),
            "enum" => new LogicalType(LogicalTypeKind.Enum, Values: args),
            "set" => new LogicalType(LogicalTypeKind.Set, Values: args),
            "uuid" => new LogicalType(LogicalTypeKind.Uuid),
            "geometry" or "point" or "linestring" or "polygon" or "multipoint" or "multilinestring" or "multipolygon" or "geometrycollection" or "geomcollection" =>
                new LogicalType(LogicalTypeKind.Spatial),
            _ => new LogicalType(LogicalTypeKind.Unknown),
        };
    }

    public StorageKind StorageFor(LogicalType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.Kind switch
        {
            LogicalTypeKind.Integer when type.Bits == 64 && type.Signed == false => StorageKind.String,
            LogicalTypeKind.Integer or LogicalTypeKind.Boolean or LogicalTypeKind.Bit => StorageKind.Int64,
            LogicalTypeKind.Decimal when type.Precision <= 18 && type.Scale <= 12 => StorageKind.Decimal,
            LogicalTypeKind.Decimal => StorageKind.String,
            LogicalTypeKind.Float => StorageKind.Double,
            LogicalTypeKind.Binary or LogicalTypeKind.Spatial => StorageKind.Binary,
            // Dates as text: zero dates and fractions exactly as the server returns them.
            _ => StorageKind.String,
        };
    }

    public string ToNative(LogicalType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var unsigned = type.Signed == false ? " unsigned" : string.Empty;
        return type.Kind switch
        {
            LogicalTypeKind.Integer => (type.Bits switch { 8 => "tinyint", 16 => "smallint", 24 => "mediumint", 64 => "bigint", _ => "int" }) + unsigned,
            LogicalTypeKind.Decimal => string.Create(CultureInfo.InvariantCulture, $"decimal({type.Precision ?? 10},{type.Scale ?? 0})") + unsigned,
            LogicalTypeKind.Float => (type.Bits == 32 ? "float" : "double") + unsigned,
            LogicalTypeKind.Bit => string.Create(CultureInfo.InvariantCulture, $"bit({type.Bits ?? 1})"),
            LogicalTypeKind.Boolean => "tinyint(1)",
            LogicalTypeKind.Text => type.Length is { } n and <= 65535 ? string.Create(CultureInfo.InvariantCulture, $"varchar({n})") : "longtext",
            LogicalTypeKind.Binary => type.Length is { } b and <= 65535 ? string.Create(CultureInfo.InvariantCulture, $"varbinary({b})") : "longblob",
            LogicalTypeKind.Date => "date",
            LogicalTypeKind.Time => type.FractionDigits is > 0 ? string.Create(CultureInfo.InvariantCulture, $"time({type.FractionDigits})") : "time",
            LogicalTypeKind.DateTime => type.FractionDigits is > 0 ? string.Create(CultureInfo.InvariantCulture, $"datetime({type.FractionDigits})") : "datetime",
            LogicalTypeKind.Json => "json",
            LogicalTypeKind.Enum => "enum(" + string.Join(",", (type.Values ?? []).Select(Quote)) + ")",
            LogicalTypeKind.Set => "set(" + string.Join(",", (type.Values ?? []).Select(Quote)) + ")",
            LogicalTypeKind.Uuid => "uuid",
            LogicalTypeKind.Spatial => "geometry",
            _ => "text",
        };
    }

    /// <summary>Grid colour category for the type (section 5.6).</summary>
    public static TypeCategory CategoryOf(LogicalType type) => type.Kind switch
    {
        LogicalTypeKind.Time or LogicalTypeKind.Date or LogicalTypeKind.DateTime => TypeCategory.Temporal,
        LogicalTypeKind.Integer when type.Bits == 16 && type.Signed == false => TypeCategory.Integer,
        LogicalTypeKind.Enum or LogicalTypeKind.Set or LogicalTypeKind.Uuid => TypeCategory.Other,
        _ => type.Category,
    };

    private static long? TextLength(string name, int? declared) => name switch
    {
        "tinytext" or "tinyblob" => 255,
        "text" or "blob" => 65535,
        "mediumtext" or "mediumblob" => 16_777_215,
        "longtext" or "longblob" => 4_294_967_295,
        _ => declared,
    };

    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>Splits <c>name(args) rest</c>; enum/set arguments come unquoted and with <c>''</c> resolved.</summary>
    internal static (string Name, List<string> Args, string Tail) Split(string type)
    {
        var open = type.IndexOf('(', StringComparison.Ordinal);
        if (open < 0)
        {
            var space = type.IndexOf(' ', StringComparison.Ordinal);
            return space < 0
                ? (type.ToLowerInvariant(), [], string.Empty)
                : (type[..space].ToLowerInvariant(), [], type[space..]);
        }
        var name = type[..open].Trim().ToLowerInvariant();
        var args = new List<string>();
        var sb = new StringBuilder();
        var inQuote = false;
        var i = open + 1;
        for (; i < type.Length; i++)
        {
            var c = type[i];
            if (inQuote)
            {
                if (c == '\'' && i + 1 < type.Length && type[i + 1] == '\'')
                {
                    sb.Append('\'');
                    i++;
                }
                else if (c == '\'')
                {
                    inQuote = false;
                }
                else if (c == '\\' && i + 1 < type.Length)
                {
                    sb.Append(type[++i]);
                }
                else
                {
                    sb.Append(c);
                }
                continue;
            }
            if (c == '\'')
            {
                inQuote = true;
            }
            else if (c == ',')
            {
                args.Add(sb.ToString());
                sb.Clear();
            }
            else if (c == ')')
            {
                break;
            }
            else if (!char.IsWhiteSpace(c))
            {
                sb.Append(c);
            }
        }
        args.Add(sb.ToString());
        if (args is [""])
        {
            args.Clear();
        }
        return (name, args, i + 1 < type.Length ? type[(i + 1)..] : string.Empty);
    }
}
