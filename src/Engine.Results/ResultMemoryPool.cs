namespace Eunomia.Engine.Results;

/// <summary>
/// Global budget for results (section 11.3, PR-17: 1 GB by default, configurable): the results that use it report how
/// much they hold in memory; above the budget, the largest ones spill blocks (the least used, outside the range the
/// grid shows) until the total fits again. Without a spill folder, a result cannot spill and stays
/// as it is. Thread-safe.
/// </summary>
public sealed class ResultMemoryPool(long budgetBytes = ResultMemoryPool.DefaultBudgetBytes)
{
    public const long DefaultBudgetBytes = 1024L * 1024 * 1024;

    private readonly Lock _gate = new();
    private readonly HashSet<MaterializedRowSource> _sources = [];
    private long _total;
    private int _trimming;

    public long BudgetBytes { get; set; } = budgetBytes;

    /// <summary>Bytes in memory of every registered result.</summary>
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

    /// <summary>A result changed size; above the budget, the largest ones spill.</summary>
    internal void Changed(long delta)
    {
        if (Interlocked.Add(ref _total, delta) <= BudgetBytes || delta <= 0)
        {
            return;
        }
        // Only one spilling at a time; spilling calls Changed again with negative deltas.
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
