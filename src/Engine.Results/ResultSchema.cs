namespace Eunomia.Engine.Results;

/// <summary>Categorias de tipo usadas para as cores do grid (secção 5.6, <c>preferences.pas</c>).</summary>
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

/// <summary>Como os valores de uma coluna ficam guardados num <see cref="RowBlock"/> (secção 11.1).</summary>
public enum StorageKind
{
    /// <summary>Inteiros com sinal em <c>long[]</c>.</summary>
    Int64,

    /// <summary>DECIMAL sem perdas: valor não escalado em <c>long[]</c> mais <see cref="ColumnInfo.Scale"/>.</summary>
    Decimal,

    /// <summary>FLOAT/DOUBLE em <c>double[]</c>.</summary>
    Double,

    /// <summary>Datas e horas como ticks em <c>long[]</c>.</summary>
    DateTime,

    /// <summary>Texto numa arena UTF-8 com offsets.</summary>
    String,

    /// <summary>Bytes (BLOB, BINARY, BIT, geometria) numa arena com offsets; mostrados em hex.</summary>
    Binary,
}

/// <summary>Metadata de uma coluna do resultado.</summary>
/// <param name="Name">Nome da coluna.</param>
/// <param name="NativeType">Tipo tal como o servidor o descreve, por exemplo <c>VARCHAR(255)</c>.</param>
/// <param name="Category">Categoria para cores.</param>
/// <param name="Storage">Representação em memória.</param>
/// <param name="IsPrimaryKey">Faz parte da chave primária (PK a bold no grid).</param>
/// <param name="IsNullable">Aceita NULL.</param>
/// <param name="Scale">Casas decimais de DECIMAL; casas das frações de segundo em DATETIME/TIMESTAMP; 0 nos outros tipos.</param>
public sealed record ColumnInfo(
    string Name,
    string NativeType,
    TypeCategory Category,
    StorageKind Storage,
    bool IsPrimaryKey,
    bool IsNullable,
    int Scale = 0)
{
    /// <summary>Números alinham à direita no grid.</summary>
    public bool IsNumeric => Category is TypeCategory.Integer or TypeCategory.Real;

    /// <summary>Base de origem da coluna (<c>org_db</c>), se o servidor a der; vazia em expressões.</summary>
    public string? SourceDatabase { get; init; }

    /// <summary>Tabela de origem (<c>org_table</c>): caption do result tab e resultados editáveis.</summary>
    public string? SourceTable { get; init; }

    /// <summary>Nome da coluna na tabela (<c>org_name</c>), que pode diferir do alias.</summary>
    public string? SourceColumn { get; init; }
}

/// <summary>Colunas de um resultado, imutável.</summary>
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

/// <summary>Estado da contagem de linhas (secção 11.1): reproduz "(exact)" / "(approximately)" do HeidiSQL.</summary>
public enum RowCountKind
{
    Unknown,
    Estimated,
    Exact,
    Final,
}

/// <summary>Contagem de linhas e o grau de certeza.</summary>
public readonly record struct RowCountState(RowCountKind Kind, long Count)
{
    public static RowCountState Unknown => new(RowCountKind.Unknown, 0);
}
