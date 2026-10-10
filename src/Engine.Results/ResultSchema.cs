namespace Eunomia.Engine.Results;

/// <summary>Type categories used for the grid colours (section 5.6, <c>preferences.pas</c>).</summary>
public enum TypeCategory
{
    Integer,
    Real,
    Text,
    Binary,
    Temporal,
    Spatial,
    Other,
}

/// <summary>How a column's values are stored in a <see cref="RowBlock"/> (section 11.1).</summary>
public enum StorageKind
{
    /// <summary>Signed integers in <c>long[]</c>.</summary>
    Int64,

    /// <summary>Lossless DECIMAL: unscaled value in <c>long[]</c> plus <see cref="ColumnInfo.Scale"/>.</summary>
    Decimal,

    /// <summary>FLOAT/DOUBLE in <c>double[]</c>.</summary>
    Double,

    /// <summary>Dates and times as ticks in <c>long[]</c>.</summary>
    DateTime,

    /// <summary>Text in a UTF-8 arena with offsets.</summary>
    String,

    /// <summary>Bytes (BLOB, BINARY, BIT, geometry) in an arena with offsets; shown as hex.</summary>
    Binary,
}

/// <summary>Metadata of a result column.</summary>
/// <param name="Name">Column name.</param>
/// <param name="NativeType">Type as the server describes it, for example <c>VARCHAR(255)</c>.</param>
/// <param name="Category">Category for colours.</param>
/// <param name="Storage">In-memory representation.</param>
/// <param name="IsPrimaryKey">Part of the primary key (PK in bold in the grid).</param>
/// <param name="IsNullable">Accepts NULL.</param>
/// <param name="Scale">DECIMAL decimal places; fractional-second digits for DATETIME/TIMESTAMP; 0 for other types.</param>
public sealed record ColumnInfo(
    string Name,
    string NativeType,
    TypeCategory Category,
    StorageKind Storage,
    bool IsPrimaryKey,
    bool IsNullable,
    int Scale = 0)
{
    /// <summary>Numbers align right in the grid.</summary>
    public bool IsNumeric => Category is TypeCategory.Integer or TypeCategory.Real;

    /// <summary>Source database of the column (<c>org_db</c>), if the server gives it; empty for expressions.</summary>
    public string? SourceDatabase { get; init; }

    /// <summary>Source table (<c>org_table</c>): result tab caption and editable results.</summary>
    public string? SourceTable { get; init; }

    /// <summary>Column name in the table (<c>org_name</c>), which may differ from the alias.</summary>
    public string? SourceColumn { get; init; }
}

/// <summary>Columns of a result, immutable.</summary>
public sealed class ResultSchema
{
    public ResultSchema(IReadOnlyList<ColumnInfo> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        Columns = columns;
    }

    public IReadOnlyList<ColumnInfo> Columns { get; }

    public int Count => Columns.Count;

    public ColumnInfo this[int index] => Columns[index];
}

/// <summary>State of the row count (section 11.1): reproduces HeidiSQL's "(exact)" / "(approximately)".</summary>
public enum RowCountKind
{
    Unknown,
    Estimated,
    Exact,
    Final,
}

/// <summary>Row count and its degree of certainty.</summary>
public readonly record struct RowCountState(RowCountKind Kind, long Count)
{
    public static RowCountState Unknown => new(RowCountKind.Unknown, 0);
}
