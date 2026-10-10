using System.Diagnostics.CodeAnalysis;

namespace Eunomia.Engine.Results.Synthetic;

/// <summary>
/// Synthetic, deterministic source for the spike and the benchmarks (section 26.2): by default
/// 1,000,000 rows × 60 columns of mixed types (INT, BIGINT, DECIMAL, DOUBLE, DATETIME, DATE,
/// short and long VARCHAR, TEXT) with NULLs. Each cell's value depends only on (seed, row, column),
/// so an evicted block can be rebuilt identically (playing the part of the keyset refetch).
/// </summary>
public sealed class SyntheticRowSource : IRowSource, IFullValueSource
{
    public const int DefaultRows = 1_000_000;
    public const int DefaultColumns = 60;
    public const int DefaultBlockSize = 1024;

    /// <summary>Data tab limit for long text (<c>GRIDMAXDATA</c>, section 4.4).</summary>
    public const int TextTruncateLength = 256;

    /// <summary>Default per-result budget (section 11.3).</summary>
    public const long DefaultBudgetBytes = 256L * 1024 * 1024;

    private static readonly SyntheticKind[] KindCycle =
    [
        SyntheticKind.Int, SyntheticKind.VarcharShort, SyntheticKind.Decimal, SyntheticKind.DateTime,
        SyntheticKind.VarcharLong, SyntheticKind.Text, SyntheticKind.BigInt, SyntheticKind.Double,
        SyntheticKind.Date,
    ];

    private readonly SyntheticKind[] _kinds;
    private readonly BlockStore _store;
    private readonly TimeSpan _latency;

    public SyntheticRowSource(
        long rows = DefaultRows,
        int columns = DefaultColumns,
        ulong seed = 20261001,
        int blockSize = DefaultBlockSize,
        long budgetBytes = DefaultBudgetBytes,
        TimeSpan latency = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rows);
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(blockSize, 1);
        Seed = seed;
        BlockSize = blockSize;
        RowCount = new RowCountState(RowCountKind.Exact, rows);
        _latency = latency;
        (Schema, _kinds) = BuildSchema(columns);
        _store = new BlockStore(LoadBlock, budgetBytes);
        _store.BlockLoaded += b => BlocksAvailable?.Invoke(this, new BlocksAvailableEventArgs(b.BlockIndex, b.BlockIndex));
    }

    public ulong Seed { get; }

    public ResultSchema Schema { get; }

    public RowCountState RowCount { get; }

    public int BlockSize { get; }

    /// <summary>Block cache (for inspection in tests and benchmarks).</summary>
    public BlockStore Store => _store;

    public event EventHandler<BlocksAvailableEventArgs>? BlocksAvailable;

    public bool TryGetBlock(int blockIndex, [NotNullWhen(true)] out RowBlock? block) => _store.TryGet(blockIndex, out block);

    public ValueTask<RowBlock> GetBlockAsync(int blockIndex, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(blockIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(blockIndex, this.BlockCount());
        return new ValueTask<RowBlock>(_store.GetAsync(blockIndex, cancellationToken));
    }

    public Task RequestRangeAsync(long firstRow, int count, CancellationToken cancellationToken = default)
    {
        var total = RowCount.Count;
        if (total == 0 || count <= 0)
        {
            return Task.CompletedTask;
        }
        firstRow = Math.Clamp(firstRow, 0, total - 1);
        var lastRow = Math.Min(total - 1, firstRow + count - 1);
        var first = (int)(firstRow / BlockSize);
        var last = (int)(lastRow / BlockSize);
        _store.Pin(first, last);

        var pending = new List<Task>(last - first + 1);
        for (var b = first; b <= last; b++)
        {
            if (!_store.TryGet(b, out _))
            {
                pending.Add(_store.GetAsync(b, cancellationToken));
            }
        }
        return pending.Count == 0 ? Task.CompletedTask : Task.WhenAll(pending);
    }

    /// <summary>Produces the block deterministically. Public for benchmarks of the generation cost.</summary>
    public RowBlock BuildBlock(int blockIndex)
    {
        var firstRow = (long)blockIndex * BlockSize;
        var rows = (int)Math.Min(BlockSize, RowCount.Count - firstRow);
        var builder = new RowBlockBuilder(Schema, blockIndex, firstRow, rows);
        Span<char> text = stackalloc char[SyntheticValues.MaxTextLength];

        for (var c = 0; c < _kinds.Length; c++)
        {
            var kind = _kinds[c];
            var nullable = Schema[c].IsNullable;
            for (var r = 0; r < rows; r++)
            {
                var row = firstRow + r;
                var h = SyntheticValues.Hash(Seed, row, c);
                if (nullable && SyntheticValues.IsNull(h))
                {
                    builder.SetNull(r, c);
                    continue;
                }
                switch (kind)
                {
                    case SyntheticKind.Id:
                        builder.SetInt64(r, c, row + 1);
                        break;
                    case SyntheticKind.Int:
                        builder.SetInt64(r, c, SyntheticValues.Int(h));
                        break;
                    case SyntheticKind.BigInt:
                        builder.SetInt64(r, c, SyntheticValues.BigInt(h));
                        break;
                    case SyntheticKind.Decimal:
                        builder.SetDecimalUnscaled(r, c, SyntheticValues.DecimalUnscaled(h));
                        break;
                    case SyntheticKind.Double:
                        builder.SetDouble(r, c, SyntheticValues.Double(h));
                        break;
                    case SyntheticKind.DateTime:
                        builder.SetDateTime(r, c, SyntheticValues.DateTime(h));
                        break;
                    case SyntheticKind.Date:
                        builder.SetDateTime(r, c, SyntheticValues.DateTime(h).Date);
                        break;
                    default:
                        var length = SyntheticValues.Text(h, kind, text, out var full);
                        var truncated = kind == SyntheticKind.Text && full > TextTruncateLength;
                        builder.SetString(r, c, text[..Math.Min(length, TextTruncateLength)], truncated);
                        break;
                }
            }
        }
        return builder.Build();
    }

    public ValueTask<string?> GetFullTextAsync(long row, int column, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, RowCount.Count);
        var kind = _kinds[column];
        var h = SyntheticValues.Hash(Seed, row, column);
        if (Schema[column].IsNullable && SyntheticValues.IsNull(h))
        {
            return ValueTask.FromResult<string?>(null);
        }
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string text;
        switch (kind)
        {
            case SyntheticKind.Id:
                text = (row + 1).ToString(inv);
                break;
            case SyntheticKind.Int:
                text = SyntheticValues.Int(h).ToString(inv);
                break;
            case SyntheticKind.BigInt:
                text = SyntheticValues.BigInt(h).ToString(inv);
                break;
            case SyntheticKind.Decimal:
                text = (SyntheticValues.DecimalUnscaled(h) / 100m).ToString("0.00", inv);
                break;
            case SyntheticKind.Double:
                text = SyntheticValues.Double(h).ToString("R", inv);
                break;
            case SyntheticKind.DateTime:
                text = SyntheticValues.DateTime(h).ToString("yyyy-MM-dd HH:mm:ss", inv);
                break;
            case SyntheticKind.Date:
                text = SyntheticValues.DateTime(h).ToString("yyyy-MM-dd", inv);
                break;
            default:
                var buffer = new char[SyntheticValues.MaxTextLength];
                var length = SyntheticValues.Text(h, kind, buffer, out _);
                text = new string(buffer, 0, length);
                break;
        }
        return ValueTask.FromResult<string?>(text);
    }

    private RowBlock LoadBlock(int blockIndex, CancellationToken cancellationToken)
    {
        if (_latency > TimeSpan.Zero)
        {
            Thread.Sleep(_latency);
        }
        return BuildBlock(blockIndex);
    }

    private static (ResultSchema, SyntheticKind[]) BuildSchema(int columns)
    {
        var kinds = new SyntheticKind[columns];
        var infos = new ColumnInfo[columns];
        kinds[0] = SyntheticKind.Id;
        infos[0] = new ColumnInfo("id", "INT UNSIGNED", TypeCategory.Integer, StorageKind.Int64, IsPrimaryKey: true, IsNullable: false);
        for (var c = 1; c < columns; c++)
        {
            var kind = KindCycle[(c - 1) % KindCycle.Length];
            kinds[c] = kind;
            // One column in five is NOT NULL.
            var nullable = c % 5 != 0;
            var name = $"{SyntheticValues.Prefix(kind)}_{c:D2}";
            infos[c] = kind switch
            {
                SyntheticKind.Int => new ColumnInfo(name, "INT", TypeCategory.Integer, StorageKind.Int64, false, nullable),
                SyntheticKind.BigInt => new ColumnInfo(name, "BIGINT", TypeCategory.Integer, StorageKind.Int64, false, nullable),
                SyntheticKind.Decimal => new ColumnInfo(name, "DECIMAL(12,2)", TypeCategory.Real, StorageKind.Decimal, false, nullable, Scale: 2),
                SyntheticKind.Double => new ColumnInfo(name, "DOUBLE", TypeCategory.Real, StorageKind.Double, false, nullable),
                SyntheticKind.DateTime => new ColumnInfo(name, "DATETIME", TypeCategory.Temporal, StorageKind.DateTime, false, nullable),
                SyntheticKind.Date => new ColumnInfo(name, "DATE", TypeCategory.Temporal, StorageKind.DateTime, false, nullable),
                SyntheticKind.VarcharShort => new ColumnInfo(name, "VARCHAR(30)", TypeCategory.Text, StorageKind.String, false, nullable),
                SyntheticKind.VarcharLong => new ColumnInfo(name, "VARCHAR(255)", TypeCategory.Text, StorageKind.String, false, nullable),
                _ => new ColumnInfo(name, "TEXT", TypeCategory.Text, StorageKind.String, false, nullable),
            };
        }
        return (new ResultSchema(infos), kinds);
    }
}

internal enum SyntheticKind
{
    Id,
    Int,
    BigInt,
    Decimal,
    Double,
    DateTime,
    Date,
    VarcharShort,
    VarcharLong,
    Text,
}
