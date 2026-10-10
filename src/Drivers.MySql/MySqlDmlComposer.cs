using System.Text;
using System.Text.RegularExpressions;
using Eunomia.Drivers.Abstractions;

namespace Eunomia.Drivers.MySql;

/// <summary>
/// MySQL/MariaDB DML for grid editing (appendix D B15): numbers unquoted when the text is a number, BIT as
/// <c>b'0101'</c>, binary as <c>0x…</c>, dates and text quoted, functions as they are; WHERE with
/// <c>CastAsText</c> and <c>IS NULL</c>; UPDATE and DELETE always with <c>LIMIT 1</c>. On tables without a key, text
/// is compared with the binary collation of the column's charset (with the column's collation, <c>'a'</c> and <c>'A'</c>
/// were the same row and <c>LIMIT 1</c> could change the other one); with a key, with the column's, to use the index.
/// </summary>
public sealed partial class MySqlDmlComposer(MySqlDialect dialect) : IDmlComposer
{
    [GeneratedRegex(@"^[+-]?(\d+(\.\d*)?|\.\d+)([eE][+-]?\d+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex Number();

    [GeneratedRegex(@"^0x[0-9a-fA-F]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Hex();

    [GeneratedRegex(@"^[01]+$", RegexOptions.CultureInvariant)]
    private static partial Regex Bits();

    public string? LastInsertId => "SELECT LAST_INSERT_ID()";

    public string Literal(ColumnDefinition column, CellLiteral value)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (value.IsFunction)
        {
            return value.Text ?? "NULL";
        }
        if (value.Text is not { } text)
        {
            return "NULL";
        }
        return column.Type.Kind switch
        {
            LogicalTypeKind.Integer or LogicalTypeKind.Decimal or LogicalTypeKind.Float or LogicalTypeKind.Boolean
                when Number().IsMatch(text.Trim()) => text.Trim(),
            LogicalTypeKind.Bit when Bits().IsMatch(text) => "b'" + text + "'",
            LogicalTypeKind.Bit when Number().IsMatch(text.Trim()) => text.Trim(),
            LogicalTypeKind.Binary or LogicalTypeKind.Spatial or LogicalTypeKind.Bit when Hex().IsMatch(text) =>
                text.Length == 2 ? "''" : text,
            _ => dialect.StringLiteral(text),
        };
    }

    public string Insert(DbObjectRef table, IReadOnlyList<ColumnValue> values)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            return $"INSERT INTO {dialect.QualifiedName(table)} () VALUES ()";
        }
        return $"INSERT INTO {dialect.QualifiedName(table)} ({string.Join(", ", values.Select(v => dialect.QuoteIdentifier(v.Column.Name)))}) " +
               $"VALUES ({string.Join(", ", values.Select(v => Literal(v.Column, v.Value)))})";
    }

    public string Update(DbObjectRef table, IReadOnlyList<ColumnValue> set, IReadOnlyList<ColumnValue> where, bool withoutKey)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(set);
        return new StringBuilder("UPDATE ").Append(dialect.QualifiedName(table)).Append(" SET ")
            .AppendJoin(", ", set.Select(v => dialect.QuoteIdentifier(v.Column.Name) + "=" + Literal(v.Column, v.Value)))
            .Append(Where(where, exact: withoutKey)).Append(" LIMIT 1").ToString();
    }

    public string Delete(DbObjectRef table, IReadOnlyList<ColumnValue> where, bool withoutKey)
    {
        ArgumentNullException.ThrowIfNull(table);
        return "DELETE FROM " + dialect.QualifiedName(table) + Where(where, exact: withoutKey) + " LIMIT 1";
    }

    public string SelectRow(DbObjectRef table, IReadOnlyList<ColumnDefinition> columns, IReadOnlyList<ColumnValue> where)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(columns);
        return "SELECT " + string.Join(", ", columns.Select(c => dialect.QuoteIdentifier(c.Name))) + " FROM " + dialect.QualifiedName(table) + Where(where) + " LIMIT 1";
    }

    public string Count(DbObjectRef table, IReadOnlyList<ColumnValue> where)
    {
        ArgumentNullException.ThrowIfNull(table);
        return "SELECT COUNT(*) FROM " + dialect.QualifiedName(table) + Where(where);
    }

    /// <summary>
    /// <c> WHERE</c> with every column: <c>IS NULL</c> or <c>CastAsText(col)=literal</c> (appendix D B15). With
    /// <paramref name="exact"/> (a table without a key), text is compared with <c>CONVERT(literal USING cs) COLLATE cs_bin</c>.
    /// </summary>
    private string Where(IReadOnlyList<ColumnValue> where, bool exact = false)
    {
        ArgumentNullException.ThrowIfNull(where);
        if (where.Count == 0)
        {
            throw new ArgumentException("A row needs at least one column to identify it.", nameof(where));
        }
        return " WHERE " + string.Join(" AND ", where.Select(v =>
            v.Value.IsNull
                ? dialect.CastAsText(v.Column) + " IS NULL"
                : dialect.CastAsText(v.Column) + "=" + (exact ? ExactLiteral(v.Column, v.Value) : Literal(v.Column, v.Value))));
    }

    /// <summary>
    /// Literal to compare without the column's collation: text, ENUM and SET with a known charset are converted to that
    /// charset with the <c>_bin</c> collation (case, accents and equivalences count); the rest as <see cref="Literal"/>.
    /// </summary>
    private string ExactLiteral(ColumnDefinition column, CellLiteral value)
    {
        var literal = Literal(column, value);
        if (value.IsFunction || column.Type.Kind is not (LogicalTypeKind.Text or LogicalTypeKind.Enum or LogicalTypeKind.Set)
            || column.Type.Charset is not { Length: > 0 } charset || string.Equals(charset, "binary", StringComparison.OrdinalIgnoreCase)
            || !SafeName().IsMatch(charset))
        {
            return literal;
        }
        return $"CONVERT({literal} USING {charset}) COLLATE {charset}_bin";
    }

    [GeneratedRegex(@"^[A-Za-z0-9_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeName();
}
