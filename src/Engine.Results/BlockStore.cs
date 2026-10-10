using System.Diagnostics.CodeAnalysis;

namespace Eunomia.Engine.Results;

/// <summary>
/// Cache de blocos com orçamento de memória e LRU (secção 11.3). Os blocos do range fixado
/// (visível + prefetch) nunca são despejados. Blocos despejados voltam a ser pedidos ao loader
/// (no source real: refetch por keyset ou leitura do spill). Thread-safe.
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

    /// <param name="loader">Produz um bloco. Corre no thread pool.</param>
    /// <param name="budgetBytes">Orçamento; acima dele despeja os blocos menos usados que não estão fixados.</param>
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

    /// <summary>Número de blocos produzidos pelo loader (inclui refetch de blocos despejados).</summary>
    public int LoadCount => Volatile.Read(ref _loadCount);

    private int _loadCount;

    /// <summary>Um bloco ficou residente. Disparado no thread pool.</summary>
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
                // O load é partilhado por todos os pedidos; o cancelamento só afeta quem espera.
                task = Task.Run(() => Load(blockIndex), CancellationToken.None);
                _loading[blockIndex] = task;
            }
        }
        return cancellationToken.CanBeCanceled ? task.WaitAsync(cancellationToken) : task;
    }

    /// <summary>Fixa os blocos <c>[first, last]</c>: não são despejados até ao próximo <see cref="Pin"/>.</summary>
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
