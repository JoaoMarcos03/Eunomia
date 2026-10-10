namespace Eunomia.Drivers.Abstractions;

/// <summary>
/// Value of a cell to save or compare: text (<c>null</c> = NULL) or, in function mode (Ctrl+F2), an SQL expression
/// that goes as is.
/// </summary>
public readonly record struct CellLiteral(string? Text, bool IsFunction = false)
{
    public static CellLiteral Null => new(null);

    public bool IsNull => Text is null && !IsFunction;
}

/// <summary>A column and its value in an INSERT, a SET or the WHERE.</summary>
public sealed record ColumnValue(ColumnDefinition Column, CellLiteral Value);

/// <summary>
/// Grid editing DML (section 14.1; appendix D B15), typed per engine: literals by type, INSERT, UPDATE and DELETE of one
/// row, with the WHERE by the row's key and a one-row limit (always, so that a stale key in the cache never
/// changes several rows).
/// </summary>
public interface IDmlComposer
{
    /// <summary>Literal of the value for the column: NULL, expression (function), number, BIT, binary as hex, date or text.</summary>
    string Literal(ColumnDefinition column, CellLiteral value);

    string Insert(DbObjectRef table, IReadOnlyList<ColumnValue> values);

    /// <param name="withoutKey">
    /// The WHERE has every column (a table without a primary key or UNIQUE NOT NULL): text is compared exactly
    /// (without the column collation's equivalences), so no other row that only differs in case or accents is changed.
    /// </param>
    string Update(DbObjectRef table, IReadOnlyList<ColumnValue> set, IReadOnlyList<ColumnValue> where, bool withoutKey);

    /// <param name="withoutKey">As in <see cref="Update"/>.</param>
    string Delete(DbObjectRef table, IReadOnlyList<ColumnValue> where, bool withoutKey);

    /// <summary>The row by the WHERE, with full columns (read back after saving, <c>EnsureFullRow</c>).</summary>
    string SelectRow(DbObjectRef table, IReadOnlyList<ColumnDefinition> columns, IReadOnlyList<ColumnValue> where);

    /// <summary>How many rows match the WHERE (confirms an UPDATE that changed no values).</summary>
    string Count(DbObjectRef table, IReadOnlyList<ColumnValue> where);

    /// <summary>The connection's last auto-increment value, or <c>null</c> if the engine does not have one.</summary>
    string? LastInsertId { get; }
}
