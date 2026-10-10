namespace Eunomia.Drivers.Abstractions;

/// <summary>
/// Valor de uma célula para gravar ou comparar: texto (<c>null</c> = NULL) ou, no modo função (Ctrl+F2), uma expressão
/// SQL que vai tal como está.
/// </summary>
public readonly record struct CellLiteral(string? Text, bool IsFunction = false)
{
    public static CellLiteral Null => new(null);

    public bool IsNull => Text is null && !IsFunction;
}

/// <summary>Uma coluna e o valor dela num INSERT, num SET ou no WHERE.</summary>
public sealed record ColumnValue(ColumnDefinition Column, CellLiteral Value);

/// <summary>
/// DML da edição no grid (secção 14.1; anexo D B15), tipado por motor: literais por tipo, INSERT, UPDATE e DELETE de uma
/// linha, com o WHERE pela chave da linha e o limite de uma linha (sempre, para que uma chave desatualizada na cache nunca
/// mude várias linhas).
/// </summary>
public interface IDmlComposer
{
    /// <summary>Literal do valor para a coluna: NULL, expressão (função), número, BIT, binário em hex, data ou texto.</summary>
    string Literal(ColumnDefinition column, CellLiteral value);

    string Insert(DbObjectRef table, IReadOnlyList<ColumnValue> values);

    /// <param name="withoutKey">
    /// O WHERE tem todas as colunas (tabela sem chave primária nem UNIQUE NOT NULL): o texto compara-se de forma exata
    /// (sem as equivalências da collation da coluna), para não mudar outra linha que só difira em maiúsculas ou acentos.
    /// </param>
    string Update(DbObjectRef table, IReadOnlyList<ColumnValue> set, IReadOnlyList<ColumnValue> where, bool withoutKey);

    /// <param name="withoutKey">Como em <see cref="Update"/>.</param>
    string Delete(DbObjectRef table, IReadOnlyList<ColumnValue> where, bool withoutKey);

    /// <summary>A linha pelo WHERE, com as colunas completas (releitura depois de gravar, <c>EnsureFullRow</c>).</summary>
    string SelectRow(DbObjectRef table, IReadOnlyList<ColumnDefinition> columns, IReadOnlyList<ColumnValue> where);

    /// <summary>Quantas linhas correspondem ao WHERE (confirma um UPDATE que não mudou valores).</summary>
    string Count(DbObjectRef table, IReadOnlyList<ColumnValue> where);

    /// <summary>Último valor de auto incremento da ligação, ou <c>null</c> se o motor não tiver.</summary>
    string? LastInsertId { get; }
}
