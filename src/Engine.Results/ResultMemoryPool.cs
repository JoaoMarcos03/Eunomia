namespace Eunomia.Engine.Results;

/// <summary>
/// Orçamento global dos resultados (secção 11.3, PR-17: 1 GB por omissão, configurável): os resultados que o usam
/// dizem quanto têm em memória; acima do orçamento, os maiores despejam blocos para o spill (os menos usados e fora do
/// intervalo que o grid mostra) até o total voltar a caber. Sem pasta de spill, um resultado não consegue despejar e
/// fica como está. Thread-safe.
/// </summary>
public sealed class ResultMemoryPool(long budgetBytes = ResultMemoryPool.DefaultBudgetBytes)
{
    public const long DefaultBudgetBytes = 1024L * 1024 * 1024;

    private readonly Lock _gate = new();
    private readonly HashSet<MaterializedRowSource> _sources = [];
    private long _total;
    private int _trimming;

    public long BudgetBytes { get; set; } = budgetBytes;

    /// <summary>Bytes em memória de todos os resultados registados.</summary>
    public long TotalBytes => Interlocked.Read(ref _total);

    internal void Register(MaterializedRowSource source)
    {
        lock (_gate)
        {
            _sources.Add(source);
        }
    }

    internal void Unregister(MaterializedRowSource source, long resident)
    {
        lock (_gate)
        {
            if (!_sources.Remove(source))
            {
                return;
            }
        }
        Interlocked.Add(ref _total, -resident);
    }

    /// <summary>Um resultado mudou de tamanho; acima do orçamento, despeja nos maiores.</summary>
    internal void Changed(long delta)
    {
        if (Interlocked.Add(ref _total, delta) <= BudgetBytes || delta <= 0)
        {
            return;
        }
        // Um só a despejar de cada vez; o despejo volta a chamar Changed com deltas negativos.
        if (Interlocked.CompareExchange(ref _trimming, 1, 0) != 0)
        {
            return;
        }
        try
        {
            List<MaterializedRowSource> sources;
            lock (_gate)
            {
                sources = [.. _sources];
            }
            foreach (var source in sources.OrderByDescending(s => s.ByteSize))
            {
                var excess = Interlocked.Read(ref _total) - BudgetBytes;
                if (excess <= 0)
                {
                    break;
                }
                source.TrimTo(Math.Max(0, source.ByteSize - excess));
            }
        }
        finally
        {
            Volatile.Write(ref _trimming, 0);
        }
    }
}
