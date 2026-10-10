namespace Eunomia.Engine.Results;

/// <summary>
/// Orçamento de um resultado (secção 11.3, PR-17): bytes em memória por resultado, pasta de spill e quota de disco.
/// Sem pasta de spill, tudo fica em memória.
/// </summary>
public sealed record ResultStorage(long BudgetBytes = ResultStorage.DefaultBudgetBytes, string? SpillDirectory = null, long SpillQuotaBytes = ResultStorage.DefaultSpillQuotaBytes)
{
    /// <summary>Orçamento global partilhado com os outros resultados (PR-17), ou <c>null</c>.</summary>
    public ResultMemoryPool? Pool { get; init; }

    public const long DefaultBudgetBytes = 256L * 1024 * 1024;

    public const long DefaultSpillQuotaBytes = 10L * 1024 * 1024 * 1024;

    public static ResultStorage InMemory { get; } = new();
}

/// <summary>A memória e o disco permitidos para um resultado acabaram (o resultado fica parcial mas utilizável).</summary>
public sealed class ResultStorageFullException(string message) : Exception(message);

/// <summary>
/// Resultado com os blocos por ordem de chegada (resultados de queries; páginas do Data tab). Os blocos chegam com
/// <see cref="Append"/> (do executor em streaming); a contagem passa a final em <see cref="Complete"/>. Acima do
/// orçamento, os blocos menos usados e fora do intervalo pedido pelo grid vão para o ficheiro de spill e voltam quando
/// são pedidos. Thread-safe para um escritor e vários leitores.
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

    // O despejo pode vir do próprio resultado e do orçamento global ao mesmo tempo: um de cada vez (reentrante).
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

    /// <summary>Bytes em memória (para o orçamento da secção 11.3).</summary>
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

    /// <summary>Bytes no ficheiro de spill.</summary>
    public long SpilledBytes => _spill?.Length ?? 0;

    public event EventHandler<BlocksAvailableEventArgs>? BlocksAvailable;

    /// <summary>
    /// Acrescenta um bloco (tem de ser o seguinte: índice e primeira linha certos). Se o orçamento e a quota de disco
    /// acabarem, lança <see cref="ResultStorageFullException"/> e o bloco não entra.
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

    /// <summary>Protege do spill os blocos do intervalo pedido pelo grid e traz de volta os que lá estiverem.</summary>
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

    /// <summary>Acima do orçamento, escreve no spill e larga os blocos menos usados fora do intervalo protegido.</summary>
    private void EnforceBudget() => TrimTo(_storage.BudgetBytes);

    /// <summary>
    /// Despeja blocos (os menos usados, fora do intervalo protegido) até ficarem no máximo <paramref name="bytes"/> em
    /// memória ou não haver mais candidatos; sem pasta de spill, não faz nada. O orçamento global usa-o.
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
