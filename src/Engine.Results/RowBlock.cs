using System.Text;

namespace Eunomia.Engine.Results;

/// <summary>
/// Bloco imutável de linhas guardado por coluna (secção 11.1): arrays tipados, bitmap de NULL,
/// bitmap de "truncado" e, para texto, uma arena UTF-8 com offsets. Sem objetos por célula.
/// Construído com <see cref="RowBlockBuilder"/>; depois de construído pode ser partilhado entre threads.
/// </summary>
public sealed class RowBlock
{
    private readonly ColumnVector[] _columns;

    internal RowBlock(int blockIndex, long firstRow, int rowCount, ResultSchema schema, ColumnVector[] columns)
    {
        BlockIndex = blockIndex;
        FirstRow = firstRow;
        RowCount = rowCount;
        Schema = schema;
        _columns = columns;

        long bytes = 0;
        foreach (var c in columns)
        {
            bytes += c.ByteSize;
        }
        ByteSize = bytes;
    }

    public int BlockIndex { get; }

    /// <summary>Índice global da primeira linha do bloco.</summary>
    public long FirstRow { get; }

    public int RowCount { get; }

    public ResultSchema Schema { get; }

    /// <summary>Bytes ocupados pelos arrays do bloco; usado para o orçamento de memória.</summary>
    public long ByteSize { get; }

    public bool IsNull(int row, int column) => _columns[column].IsNull(row);

    /// <summary>O valor foi cortado (texto longo, como <c>LEFT(..., 256)</c> no Data tab).</summary>
    public bool IsTruncated(int row, int column) => _columns[column].IsTruncated(row);

    public long GetInt64(int row, int column) => _columns[column].Longs![row];

    /// <summary>Valor não escalado de um DECIMAL; dividir por 10^Scale.</summary>
    public long GetDecimalUnscaled(int row, int column) => _columns[column].Longs![row];

    public double GetDouble(int row, int column) => _columns[column].Doubles![row];

    public DateTime GetDateTime(int row, int column) => new(_columns[column].Longs![row], DateTimeKind.Unspecified);

    public ReadOnlySpan<byte> GetUtf8(int row, int column)
    {
        var c = _columns[column];
        var start = c.Offsets![row];
        return c.Utf8.AsSpan(start, c.Offsets[row + 1] - start);
    }

    public string GetString(int row, int column) => Encoding.UTF8.GetString(GetUtf8(row, column));

    /// <summary>
    /// O mesmo bloco noutra posição e com outro schema (mesmos tipos): as páginas seguintes do Data tab continuam a
    /// numeração e as colunas <c>LEFT(…)</c> ficam com o nome de origem. Partilha os dados (são imutáveis).
    /// </summary>
    public RowBlock WithPosition(int blockIndex, long firstRow, ResultSchema? schema = null)
    {
        if (schema is not null && schema.Count != Schema.Count)
        {
            throw new ArgumentException("O schema tem de ter as mesmas colunas.", nameof(schema));
        }
        return new RowBlock(blockIndex, firstRow, RowCount, schema ?? Schema, _columns);
    }

    /// <summary>Vista do bloco só com algumas colunas, pela ordem dada (colunas ocultas do Data tab). Partilha os dados.</summary>
    public RowBlock Project(IReadOnlyList<int> columns, ResultSchema schema)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(schema);
        if (schema.Count != columns.Count)
        {
            throw new ArgumentException("O schema tem de ter as colunas projetadas.", nameof(schema));
        }
        return new RowBlock(BlockIndex, FirstRow, RowCount, schema, columns.Select(c => _columns[c]).ToArray());
    }

    /// <summary>Colunas do bloco (spill).</summary>
    internal ColumnVector[] Columns => _columns;

    /// <summary>Bytes de uma coluna <see cref="StorageKind.Binary"/>.</summary>
    public ReadOnlySpan<byte> GetBytes(int row, int column) => GetUtf8(row, column);
}

/// <summary>Dados de uma coluna dentro de um bloco. Só um dos arrays de valores está preenchido.</summary>
internal sealed class ColumnVector
{
    public long[]? Longs;
    public double[]? Doubles;
    public byte[] Utf8 = [];
    public int[]? Offsets;
    public ulong[] Nulls = [];
    public ulong[] Truncated = [];

    public bool IsNull(int row) => (Nulls[row >> 6] & (1UL << (row & 63))) != 0;

    public bool IsTruncated(int row) => (Truncated[row >> 6] & (1UL << (row & 63))) != 0;

    public long ByteSize =>
        (Longs?.LongLength ?? 0) * sizeof(long)
        + (Doubles?.LongLength ?? 0) * sizeof(double)
        + Utf8.LongLength
        + (Offsets?.LongLength ?? 0) * sizeof(int)
        + (Nulls.LongLength + Truncated.LongLength) * sizeof(ulong);
}
