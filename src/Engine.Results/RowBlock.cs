using System.Text;

namespace Eunomia.Engine.Results;

/// <summary>
/// Immutable block of rows stored by column (section 11.1): typed arrays, a NULL bitmap,
/// a "truncated" bitmap and, for text, a UTF-8 arena with offsets. No per-cell objects.
/// Built with <see cref="RowBlockBuilder"/>; once built it can be shared between threads.
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

    /// <summary>Global index of the block's first row.</summary>
    public long FirstRow { get; }

    public int RowCount { get; }

    public ResultSchema Schema { get; }

    /// <summary>Bytes taken by the block's arrays; used for the memory budget.</summary>
    public long ByteSize { get; }

    public bool IsNull(int row, int column) => _columns[column].IsNull(row);

    /// <summary>The value was cut (long text, like <c>LEFT(..., 256)</c> in the Data tab).</summary>
    public bool IsTruncated(int row, int column) => _columns[column].IsTruncated(row);

    public long GetInt64(int row, int column) => _columns[column].Longs![row];

    /// <summary>Unscaled value of a DECIMAL; divide by 10^Scale.</summary>
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
    /// The same block at another position and with another schema (same types): the next Data tab pages continue the
    /// numbering and <c>LEFT(…)</c> columns keep their source name. Shares the data (it is immutable).
    /// </summary>
    public RowBlock WithPosition(int blockIndex, long firstRow, ResultSchema? schema = null)
    {
        if (schema is not null && schema.Count != Schema.Count)
        {
            throw new ArgumentException("O schema tem de ter as mesmas colunas.", nameof(schema));
        }
        return new RowBlock(blockIndex, firstRow, RowCount, schema ?? Schema, _columns);
    }

    /// <summary>View of the block with only some columns, in the given order (Data tab hidden columns). Shares the data.</summary>
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

    /// <summary>Columns of the block (spill).</summary>
    internal ColumnVector[] Columns => _columns;

    /// <summary>Bytes of a <see cref="StorageKind.Binary"/> column.</summary>
    public ReadOnlySpan<byte> GetBytes(int row, int column) => GetUtf8(row, column);
}

/// <summary>Data of a column within a block. Only one of the value arrays is filled.</summary>
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
