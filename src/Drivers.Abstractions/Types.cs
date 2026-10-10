using Eunomia.Engine.Results;

namespace Eunomia.Drivers.Abstractions;

/// <summary>Tipos lógicos da secção 14.4.</summary>
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
/// Tipo lógico com os parâmetros que importam para edição sem perdas e para as cores do grid (14.4).
/// Os campos que não se aplicam a um tipo ficam a <c>null</c>.
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
    /// Categoria de cor por omissão (coluna "Categoria de cor" da 14.4). Boolean e Json têm duas opções na
    /// tabela; fica a primeira (Integer e Text) até haver preferência.
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

/// <summary>Tipo nativo ↔ tipo lógico (secção 14.4). Alimenta editores, cores e o armazenamento dos blocos.</summary>
public interface ITypeMapper
{
    /// <summary>Interpreta o tipo tal como o servidor o descreve, por exemplo <c>decimal(12,2) unsigned</c>.</summary>
    LogicalType Map(string nativeType);

    /// <summary>Como os valores deste tipo ficam guardados num <see cref="RowBlock"/>.</summary>
    StorageKind StorageFor(LogicalType type);

    /// <summary>Texto nativo do tipo (para DDL), o inverso de <see cref="Map"/>.</summary>
    string ToNative(LogicalType type);
}
