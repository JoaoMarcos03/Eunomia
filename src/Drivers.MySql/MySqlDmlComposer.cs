using System.Text;
using System.Text.RegularExpressions;
using Eunomia.Drivers.Abstractions;

namespace Eunomia.Drivers.MySql;

/// <summary>
/// DML do MySQL/MariaDB para a edição no grid (anexo D B15): números sem aspas quando o texto é um número, BIT como
/// <c>b'0101'</c>, binário como <c>0x…</c>, datas e texto entre aspas, funções tal como estão; WHERE com
/// <c>CastAsText</c> e <c>IS NULL</c>; UPDATE e DELETE sempre com <c>LIMIT 1</c>. Nas tabelas sem chave, o texto
/// compara-se pela collation binária do charset da coluna (com a collation da coluna, <c>'a'</c> e <c>'A'</c> eram a
/// mesma linha e o <c>LIMIT 1</c> podia mudar a outra); com chave, pela da coluna, para usar o índice.
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
    /// <c> WHERE</c> com cada coluna: <c>IS NULL</c> ou <c>CastAsText(col)=literal</c> (anexo D B15). Com
    /// <paramref name="exact"/> (tabela sem chave), o texto compara-se com <c>CONVERT(literal USING cs) COLLATE cs_bin</c>.
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
    /// Literal para comparar sem a collation da coluna: texto, ENUM e SET com charset conhecido passam para esse charset
    /// com a collation <c>_bin</c> (maiúsculas, acentos e equivalências contam); o resto como <see cref="Literal"/>.
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
