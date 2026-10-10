using System.Diagnostics.CodeAnalysis;

namespace Eunomia.Engine.Results;

/// <summary>
/// Display text cache per (block, column) (section 15.1, Renderer). Formatting runs in the
/// background (<see cref="Prefetch"/>); <see cref="TryGet"/> never blocks and, if the block is
/// resident, may format on the spot when the caller allows it (so no placeholder is shown for data
/// already in memory). Bounded by a byte budget with LRU. Thread-safe.
/// </summary>
public sealed class FormattedCellCache
{
    private readonly IRowSource _source;
    private readonly DisplayFormatter _formatter;
    private readonly Lock _lock = new();
    private readonly Dictionary<long, LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _lru = new();
    private readonly HashSet<long> _scheduled = [];
    private long _bytes;

    public FormattedCellCache(IRowSource source, DisplayFormatter formatter, long budgetBytes = 48L * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(formatter);
        _source = source;
        _formatter = formatter;
        BudgetBytes = budgetBytes;
    }

    public IRowSource Source => _source;

    public long BudgetBytes { get; }

    public long Bytes
    {
        get
        {
            lock (_lock)
            {
                return _bytes;
            }
        }
    }

    /// <summary>Columns formatted in the background became available. Raised on the thread pool.</summary>
    public event EventHandler? Formatted;

    /// <summary>
    /// Text of the cells of column <paramref name="column"/> in block <paramref name="blockIndex"/>.
    /// With <paramref name="formatIfResident"/>, formats right away if the block is resident.
    /// </summary>
    public bool TryGet(int blockIndex, int column, bool formatIfResident, [NotNullWhen(true)] out string[]? cells)
    {
        var key = Key(blockIndex, column);
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                cells = node.Value.Cells;
                return true;
            }
        }

        if (formatIfResident && _source.TryGetBlock(blockIndex, out var block))
        {
            cells = _formatter.FormatColumn(block, column);
            Add(key, cells);
            return true;
        }
        cells = null;
        return false;
    }

    /// <summary>
    /// Formats in the background the columns <paramref name="columns"/> of blocks <c>[firstBlock, lastBlock]</c>,
    /// loading the missing blocks.
    /// </summary>
    public void Prefetch(int firstBlock, int lastBlock, ReadOnlySpan<int> columns)
    {
        List<(int Block, int Column)>? work = null;
        lock (_lock)
        {
            for (var b = firstBlock; b <= lastBlock; b++)
            {
                foreach (var c in columns)
                {
                    var key = Key(b, c);
                    if (!_entries.ContainsKey(key) && _scheduled.Add(key))
                    {
                        (work ??= []).Add((b, c));
                    }
                }
            }
        }
        if (work is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            foreach (var group in work.GroupBy(w => w.Block))
            {
                RowBlock block;
                try
                {
                    block = await _source.GetBlockAsync(group.Key).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    Unschedule(group);
                    continue;
                }
                foreach (var (b, c) in group)
                {
                    var key = Key(b, c);
                    bool exists;
                    lock (_lock)
                    {
                        exists = _entries.ContainsKey(key);
                    }
                    if (!exists)
                    {
                        Add(key, _formatter.FormatColumn(block, c));
                    }
                    lock (_lock)
                    {
                        _scheduled.Remove(key);
                    }
                }
                Formatted?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    private void Unschedule(IEnumerable<(int Block, int Column)> items)
    {
        lock (_lock)
        {
            foreach (var (b, c) in items)
            {
                _scheduled.Remove(Key(b, c));
            }
        }
    }

    private void Add(long key, string[] cells)
    {
        long size = 32 + (cells.Length * 8L);
        foreach (var s in cells)
        {
            size += 22 + (s.Length * 2L);
        }

        lock (_lock)
        {
            if (_entries.ContainsKey(key))
            {
                return;
            }
            _entries[key] = _lru.AddFirst(new Entry(key, cells, size));
            _bytes += size;
            // Always keeps at least the most recent entries (the visible ones), even above the budget.
            while (_bytes > BudgetBytes && _lru.Count > 64)
            {
                var last = _lru.Last!;
                _lru.RemoveLast();
                _entries.Remove(last.Value.Key);
                _bytes -= last.Value.Size;
            }
        }
    }

    private static long Key(int blockIndex, int column) => ((long)blockIndex << 20) | (uint)column;

    private sealed record Entry(long Key, string[] Cells, long Size);
}
