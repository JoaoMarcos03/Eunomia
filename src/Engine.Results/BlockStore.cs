using System.Diagnostics.CodeAnalysis;

namespace Eunomia.Engine.Results;

/// <summary>
/// Block cache with a memory budget and LRU (section 11.3). Blocks of the pinned range
/// (visible + prefetch) are never evicted. Evicted blocks are requested from the loader again
/// (in the real source: refetch by keyset or a spill read). Thread-safe.
/// </summary>
public sealed class BlockStore
{
    private readonly Func<int, CancellationToken, RowBlock> _loader;
    private readonly Lock _lock = new();
    private readonly Dictionary<int, LinkedListNode<RowBlock>> _resident = [];
    private readonly LinkedList<RowBlock> _lru = new();
    private readonly Dictionary<int, Task<RowBlock>> _loading = [];
    private int _pinnedFirst = -1;
    private int _pinnedLast = -2;
    private long _residentBytes;

    /// <param name="loader">Produces a block. Runs on the thread pool.</param>
    /// <param name="budgetBytes">Budget; above it, the least used blocks that are not pinned are evicted.</param>
    public BlockStore(Func<int, CancellationToken, RowBlock> loader, long budgetBytes)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budgetBytes);
        _loader = loader;
        BudgetBytes = budgetBytes;
    }

    public long BudgetBytes { get; }

    public long ResidentBytes
    {
        get
        {
            lock (_lock)
            {
                return _residentBytes;
            }
        }
    }

    public int ResidentCount
    {
        get
        {
            lock (_lock)
            {
                return _resident.Count;
            }
        }
    }

    /// <summary>Number of blocks produced by the loader (includes refetches of evicted blocks).</summary>
    public int LoadCount => Volatile.Read(ref _loadCount);

    private int _loadCount;

    /// <summary>A block became resident. Raised on the thread pool.</summary>
    public event Action<RowBlock>? BlockLoaded;

    public bool TryGet(int blockIndex, [NotNullWhen(true)] out RowBlock? block)
    {
        lock (_lock)
        {
            if (_resident.TryGetValue(blockIndex, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                block = node.Value;
                return true;
            }
        }
        block = null;
        return false;
    }

    public Task<RowBlock> GetAsync(int blockIndex, CancellationToken cancellationToken = default)
    {
        Task<RowBlock> task;
        lock (_lock)
        {
            if (_resident.TryGetValue(blockIndex, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return Task.FromResult(node.Value);
            }
            if (!_loading.TryGetValue(blockIndex, out task!))
            {
                // The load is shared by every request; cancellation only affects whoever is waiting.
                task = Task.Run(() => Load(blockIndex), CancellationToken.None);
                _loading[blockIndex] = task;
            }
        }
        return cancellationToken.CanBeCanceled ? task.WaitAsync(cancellationToken) : task;
    }

    /// <summary>Pins blocks <c>[first, last]</c>: they are not evicted until the next <see cref="Pin"/>.</summary>
    public void Pin(int first, int last)
    {
        lock (_lock)
        {
            _pinnedFirst = first;
            _pinnedLast = last;
            EvictOverBudget();
        }
    }

    private RowBlock Load(int blockIndex)
    {
        RowBlock block;
        try
        {
            block = _loader(blockIndex, CancellationToken.None);
        }
        catch
        {
            lock (_lock)
            {
                _loading.Remove(blockIndex);
            }
            throw;
        }

        Interlocked.Increment(ref _loadCount);
        lock (_lock)
        {
            _loading.Remove(blockIndex);
            if (!_resident.ContainsKey(blockIndex))
            {
                _resident[blockIndex] = _lru.AddFirst(block);
                _residentBytes += block.ByteSize;
                EvictOverBudget();
            }
        }
        BlockLoaded?.Invoke(block);
        return block;
    }

    private void EvictOverBudget()
    {
        var node = _lru.Last;
        while (_residentBytes > BudgetBytes && node is not null)
        {
            var previous = node.Previous;
            var index = node.Value.BlockIndex;
            if (index < _pinnedFirst || index > _pinnedLast)
            {
                _lru.Remove(node);
                _resident.Remove(index);
                _residentBytes -= node.Value.ByteSize;
            }
            node = previous;
        }
    }
}
