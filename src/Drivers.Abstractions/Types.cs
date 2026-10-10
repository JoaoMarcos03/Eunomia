using Eunomia.Engine.Results;

namespace Eunomia.Drivers.Abstractions;

/// <summary>Logical types of section 14.4.</summary>
public enum LogicalTypeKind
{
    Integer,
    Decimal,
    Float,
    Boolean,
    Bit,
    Text,
    Binary,
    Date,
    Time,
    DateTime,
    Interval,
    Json,
    Enum,
    Set,
    Uuid,
    Spatial,
    Array,
    Unknown,
}

/// <summary>
/// Logical type with the parameters that matter for lossless editing and for the grid colours (14.4).
/// Fields that do not apply to a type are left <c>null</c>.
/// </summary>
public sealed record LogicalType(
    LogicalTypeKind Kind,
    int? Bits = null,
    bool? Signed = null,
    int? Precision = null,
    int? Scale = null,
    long? Length = null,
    int? FractionDigits = null,
    bool? WithTimeZone = null,
    string? Charset = null,
    string? Collation = null,
    IReadOnlyList<string>? Values = null,
    LogicalType? Element = null)
{
    /// <summary>
    /// Default colour category (the "Colour category" column of 14.4). Boolean and Json have two options in the
    /// table; the first one is used (Integer and Text) until there is a preference.
    /// </summary>
    public TypeCategory Category => Kind switch
    {
        LogicalTypeKind.Integer or LogicalTypeKind.Boolean or LogicalTypeKind.Bit => TypeCategory.Integer,
        LogicalTypeKind.Decimal or LogicalTypeKind.Float => TypeCategory.Real,
        LogicalTypeKind.Text or LogicalTypeKind.Json => TypeCategory.Text,
        LogicalTypeKind.Binary => TypeCategory.Binary,
        LogicalTypeKind.Date or LogicalTypeKind.Time or LogicalTypeKind.DateTime or LogicalTypeKind.Interval => TypeCategory.Temporal,
        LogicalTypeKind.Spatial => TypeCategory.Spatial,
        _ => TypeCategory.Other,
    };
}

/// <summary>Native type ↔ logical type (section 14.4). Feeds editors, colours and block storage.</summary>
public interface ITypeMapper
{
    /// <summary>Parses the type as the server describes it, for example <c>decimal(12,2) unsigned</c>.</summary>
    LogicalType Map(string nativeType);

    /// <summary>How values of this type are stored in a <see cref="RowBlock"/>.</summary>
    StorageKind StorageFor(LogicalType type);

    /// <summary>Native text of the type (for DDL), the inverse of <see cref="Map"/>.</summary>
    string ToNative(LogicalType type);
}
