using System.Text;

namespace Eunomia.Engine.Results;

/// <summary>
/// Builds a <see cref="RowBlock"/>. Text values must be written in increasing row order
/// within each column (the order in which a driver reader delivers them); skipped rows stay empty.
/// </summary>
public sealed class RowBlockBuilder
{
    private readonly ResultSchema _schema;
    private readonly int _blockIndex;
    private readonly long _firstRow;
    private readonly int _rowCount;
    private readonly ColumnVector[] _columns;
    private readonly byte[][] _buffers;
    private readonly int[] _lengths;
    private readonly int[] _nextRow;
    private bool _built;

    public RowBlockBuilder(ResultSchema schema, int blockIndex, long firstRow, int rowCount)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentOutOfRangeException.ThrowIfNegative(rowCount);
        _schema = schema;
        _blockIndex = blockIndex;
        _firstRow = firstRow;
        _rowCount = rowCount;
        _columns = new ColumnVector[schema.Count];
        _buffers = new byte[schema.Count][];
        _lengths = new int[schema.Count];
        _nextRow = new int[schema.Count];

        var words = (rowCount + 63) >> 6;
        for (var c = 0; c < schema.Count; c++)
        {
            var v = new ColumnVector { Nulls = new ulong[words], Truncated = new ulong[words] };
            switch (schema[c].Storage)
            {
                case StorageKind.Double:
                    v.Doubles = new double[rowCount];
                    break;
                case StorageKind.String or StorageKind.Binary:
                    v.Offsets = new int[rowCount + 1];
                    _buffers[c] = new byte[Math.Max(64, rowCount * 16)];
                    break;
                default:
                    v.Longs = new long[rowCount];
                    break;
            }
            _columns[c] = v;
        }
    }

    public int RowCount => _rowCount;

    public void SetNull(int row, int column)
    {
        _columns[column].Nulls[row >> 6] |= 1UL << (row & 63);
        if (IsArena(_schema[column].Storage))
        {
            AdvanceStringRow(column, row);
            _nextRow[column] = row + 1;
            _columns[column].Offsets![row + 1] = _lengths[column];
        }
    }

    public void SetInt64(int row, int column, long value) => _columns[column].Longs![row] = value;

    public void SetDecimalUnscaled(int row, int column, long unscaled) => _columns[column].Longs![row] = unscaled;

    public void SetDouble(int row, int column, double value) => _columns[column].Doubles![row] = value;

    public void SetDateTime(int row, int column, DateTime value) => _columns[column].Longs![row] = value.Ticks;

    public void SetString(int row, int column, ReadOnlySpan<char> value, bool truncated = false)
    {
        AdvanceStringRow(column, row);
        var needed = Encoding.UTF8.GetMaxByteCount(value.Length);
        EnsureCapacity(column, needed);
        var written = Encoding.UTF8.GetBytes(value, _buffers[column].AsSpan(_lengths[column]));
        _lengths[column] += written;
        _columns[column].Offsets![row + 1] = _lengths[column];
        _nextRow[column] = row + 1;
        if (truncated)
        {
            _columns[column].Truncated[row >> 6] |= 1UL << (row & 63);
        }
    }

    /// <summary>Bytes of a <see cref="StorageKind.Binary"/> column (in row order, like text).</summary>
    public void SetBytes(int row, int column, ReadOnlySpan<byte> value, bool truncated = false)
    {
        AdvanceStringRow(column, row);
        EnsureCapacity(column, value.Length);
        value.CopyTo(_buffers[column].AsSpan(_lengths[column]));
        _lengths[column] += value.Length;
        _columns[column].Offsets![row + 1] = _lengths[column];
        _nextRow[column] = row + 1;
        if (truncated)
        {
            _columns[column].Truncated[row >> 6] |= 1UL << (row & 63);
        }
    }

    private static bool IsArena(StorageKind storage) => storage is StorageKind.String or StorageKind.Binary;

    /// <summary>Copies a row from another block with the same schema (filtered views). Rows go in order.</summary>
    public void CopyRow(RowBlock source, int sourceRow, int row)
    {
        ArgumentNullException.ThrowIfNull(source);
        var columns = source.Columns;
        for (var c = 0; c < _columns.Length; c++)
        {
            if (source.IsNull(sourceRow, c))
            {
                SetNull(row, c);
                continue;
            }
            var from = columns[c];
            switch (_schema[c].Storage)
            {
                case StorageKind.Double:
                    SetDouble(row, c, from.Doubles![sourceRow]);
                    break;
                case StorageKind.String or StorageKind.Binary:
                    SetBytes(row, c, source.GetUtf8(sourceRow, c), source.IsTruncated(sourceRow, c));
                    break;
                default:
                    _columns[c].Longs![row] = from.Longs![sourceRow];
                    break;
            }
        }
    }

    public RowBlock Build() => Build(_rowCount);

    /// <summary>Builds with only the first <paramref name="rowCount"/> rows (last block of a streaming result).</summary>
    public RowBlock Build(int rowCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rowCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rowCount, _rowCount);
        if (_built)
        {
            throw new InvalidOperationException("O bloco já foi construído.");
        }
        _built = true;

        for (var c = 0; c < _columns.Length; c++)
        {
            if (!IsArena(_schema[c].Storage))
            {
                continue;
            }
            AdvanceStringRow(c, _rowCount);
            _columns[c].Utf8 = _buffers[c].AsSpan(0, _lengths[c]).ToArray();
            _buffers[c] = [];
        }
        return new RowBlock(_blockIndex, _firstRow, rowCount, _schema, _columns);
    }

    /// <summary>Fills in the offsets of skipped rows (empty text) up to <paramref name="row"/>.</summary>
    private void AdvanceStringRow(int column, int row)
    {
        if (row < _nextRow[column])
        {
            throw new InvalidOperationException($"Texto da coluna {column} escrito fora de ordem (linha {row}).");
        }
        var offsets = _columns[column].Offsets!;
        for (var r = _nextRow[column]; r < row; r++)
        {
            offsets[r + 1] = _lengths[column];
        }
        _nextRow[column] = row;
    }

    private void EnsureCapacity(int column, int extra)
    {
        var buffer = _buffers[column];
        if (_lengths[column] + extra <= buffer.Length)
        {
            return;
        }
        var size = Math.Max(buffer.Length * 2, _lengths[column] + extra);
        Array.Resize(ref _buffers[column], size);
    }
}
