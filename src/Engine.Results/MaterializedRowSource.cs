namespace Eunomia.Engine.Results;

/// <summary>
/// Budget of a result (section 11.3, PR-17): bytes in memory per result, spill folder and disk quota.
/// Without a spill folder, everything stays in memory.
/// </summary>
public sealed record ResultStorage(long BudgetBytes = ResultStorage.DefaultBudgetBytes, string? SpillDirectory = null, long SpillQuotaBytes = ResultStorage.DefaultSpillQuotaBytes)
{
    /// <summary>Global budget shared with the other results (PR-17), or <c>null</c>.</summary>
    public ResultMemoryPool? Pool { get; init; }

    public const long DefaultBudgetBytes = 256L * 1024 * 1024;

    public const long DefaultSpillQuotaBytes = 10L * 1024 * 1024 * 1024;

    public static ResultStorage InMemory { get; } = new();
}

/// <summary>The memory and disk allowed for a result ran out (the result is partial but usable).</summary>
public sealed class ResultStorageFullException(string message) : Exception(message);

/// <summary>
/// Result with blocks in arrival order (query results; Data tab pages). Blocks arrive through
/// <see cref="Append"/> (from the streaming executor); the count becomes final in <see cref="Complete"/>. Above the
/// budget, the least used blocks outside the range requested by the grid go to the spill file and come back when
/// requested. Thread-safe for one writer and several readers.
/// </summary>
public sealed class MaterializedRowSource : IRowSource, IDisposable
{
    private readonly List<RowBlock?> _blocks = [];
    private readonly List<long> _lastUse = [];
    private readonly Lock _gate = new();
    private readonly ResultStorage _storage;
    private SpillFile? _spill;
    private long _rows;
    private long _resident;
    private long _tick;
    private int _pinnedFirst = -1;
    private int _pinnedLast = -1;
    private bool _completed;
    private bool _disposed;

    // Eviction may come from the result itself and from the global budget at the same time: one at a time (re-entrant).
    private readonly object _trimLock = new();

    public MaterializedRowSource(ResultSchema schema, int blockSize = 1024, ResultStorage? storage = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentOutOfRangeException.ThrowIfLessThan(blockSize, 1);
        Schema = schema;
        BlockSize = blockSize;
        _storage = storage ?? ResultStorage.InMemory;
        _storage.Pool?.Register(this);
    }

    public ResultSchema Schema { get; }

    public int BlockSize { get; }

    public RowCountState RowCount
    {
        get
        {
            lock (_gate)
            {
                return new RowCountState(_completed ? RowCountKind.Final : RowCountKind.Exact, _rows);
            }
        }
    }

    public bool IsComplete
    {
        get
        {
            lock (_gate)
            {
                return _completed;
            }
        }
    }

    /// <summary>Bytes in memory (for the section 11.3 budget).</summary>
    public long ByteSize
    {
        get
        {
            lock (_gate)
            {
                return _resident;
            }
        }
    }

    /// <summary>Bytes in the spill file.</summary>
    public long SpilledBytes => _spill?.Length ?? 0;

    public event EventHandler<BlocksAvailableEventArgs>? BlocksAvailable;

    /// <summary>
    /// Appends a block (it must be the next one: right index and first row). If the budget and the disk quota
    /// run out, throws <see cref="ResultStorageFullException"/> and the block is not added.
    /// </summary>
    public void Append(RowBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        int index;
        lock (_gate)
        {
            if (block.BlockIndex != _blocks.Count || block.FirstRow != _rows)
            {
                throw new InvalidOperationException($"Bloco fora de ordem: {block.BlockIndex} (esperado {_blocks.Count}).");
            }
            if (_spill is not null && _spill.Length + block.ByteSize > _storage.SpillQuotaBytes && _resident + block.ByteSize > _storage.BudgetBytes)
            {
                throw new ResultStorageFullException("Memory and disk limit reached for this result.");
            }
            _blocks.Add(block);
            _lastUse.Add(++_tick);
            _rows += block.RowCount;
            _resident += block.ByteSize;
            index = _blocks.Count - 1;
        }
        _storage.Pool?.Changed(block.ByteSize);
        EnforceBudget();
        BlocksAvailable?.Invoke(this, new BlocksAvailableEventArgs(index, index));
    }

    public void Complete()
    {
        int last;
        lock (_gate)
        {
            _completed = true;
            last = Math.Max(0, _blocks.Count - 1);
        }
        BlocksAvailable?.Invoke(this, new BlocksAvailableEventArgs(0, last));
    }

    public bool TryGetBlock(int blockIndex, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out RowBlock? block)
    {
        lock (_gate)
        {
            block = blockIndex >= 0 && blockIndex < _blocks.Count ? _blocks[blockIndex] : null;
            if (block is not null)
            {
                _lastUse[blockIndex] = ++_tick;
            }
            return block is not null;
        }
    }

    public async ValueTask<RowBlock> GetBlockAsync(int blockIndex, CancellationToken cancellationToken = default)
    {
        if (TryGetBlock(blockIndex, out var block))
        {
            return block;
        }
        SpillFile spill;
        lock (_gate)
        {
            if (blockIndex < 0 || blockIndex >= _blocks.Count || _spill is null)
            {
                throw new ArgumentOutOfRangeException(nameof(blockIndex));
            }
            spill = _spill;
        }
        var loaded = await spill.ReadAsync(blockIndex, Schema, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (_blocks[blockIndex] is { } other)
            {
                return other;
            }
            _blocks[blockIndex] = loaded;
            _lastUse[blockIndex] = ++_tick;
            _resident += loaded.ByteSize;
        }
        _storage.Pool?.Changed(loaded.ByteSize);
        EnforceBudget();
        BlocksAvailable?.Invoke(this, new BlocksAvailableEventArgs(blockIndex, blockIndex));
        return loaded;
    }

    /// <summary>Protects from spilling the blocks of the range requested by the grid and brings back those that are there.</summary>
    public async Task RequestRangeAsync(long firstRow, int count, CancellationToken cancellationToken = default)
    {
        var first = (int)(Math.Max(0, firstRow) / BlockSize);
        var last = (int)(Math.Max(0, firstRow + Math.Max(1, count) - 1) / BlockSize);
        int available;
        lock (_gate)
        {
            _pinnedFirst = first;
            _pinnedLast = last;
            available = _blocks.Count;
        }
        for (var b = first; b <= last && b < available; b++)
        {
            await GetBlockAsync(b, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Above the budget, writes to the spill and drops the least used blocks outside the protected range.</summary>
    private void EnforceBudget() => TrimTo(_storage.BudgetBytes);

    /// <summary>
    /// Evicts blocks (the least used, outside the protected range) until at most <paramref name="bytes"/> are in
    /// memory or there are no more candidates; without a spill folder, does nothing. The global budget uses it.
    /// </summary>
    internal void TrimTo(long bytes)
    {
        if (_storage.SpillDirectory is null)
        {
            return;
        }
        lock (_trimLock)
        {
            TrimLocked(bytes, _storage.SpillDirectory);
        }
    }

    private void TrimLocked(long bytes, string spillDirectory)
    {
        while (true)
        {
            RowBlock victim;
            lock (_gate)
            {
                if (_resident <= bytes || _disposed)
                {
                    return;
                }
                var candidate = -1;
                for (var i = 0; i < _blocks.Count; i++)
                {
                    if (_blocks[i] is not null && (i < _pinnedFirst || i > _pinnedLast) && (candidate < 0 || _lastUse[i] < _lastUse[candidate]))
                    {
                        candidate = i;
                    }
                }
                if (candidate < 0)
                {
                    return;
                }
                victim = _blocks[candidate]!;
                _spill ??= new SpillFile(spillDirectory);
            }
            _spill.Write(victim);
            var freed = false;
            lock (_gate)
            {
                if (ReferenceEquals(_blocks[victim.BlockIndex], victim))
                {
                    _blocks[victim.BlockIndex] = null;
                    _resident -= victim.ByteSize;
                    freed = true;
                }
            }
            if (freed)
            {
                _storage.Pool?.Changed(-victim.ByteSize);
            }
        }
    }

    public void Dispose()
    {
        long resident;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _spill?.Dispose();
            _spill = null;
            resident = _resident;
        }
        _storage.Pool?.Unregister(this, resident);
    }
}
