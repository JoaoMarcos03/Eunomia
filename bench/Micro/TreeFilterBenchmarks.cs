using BenchmarkDotNet.Attributes;
using Eunomia.UI.Controls.Tree;

namespace Eunomia.Bench.Micro;

/// <summary>
/// PR-11: Table filter over 10,000 nodes ≤ 50 ms. Fully expanded synthetic tree
/// (5 sessions × 20 databases × 100 objects); each operation applies a new filter and rebuilds the visible list.
/// </summary>
[MemoryDiagnoser]
public class TreeFilterBenchmarks
{
    private TreeModel _model = null!;
    private int _toggle;

    [GlobalSetup]
    public void Setup()
    {
        _model = new TreeModel(SyntheticTreeProvider.CreateSessions(), new SyntheticTreeProvider());
        foreach (var s in _model.Roots)
        {
            _model.ExpandAllAsync(s).GetAwaiter().GetResult();
        }
        if (_model.Visible.Count(n => n.IsObject) != 10_000)
        {
            throw new InvalidOperationException("The tree should have 10,000 objects.");
        }
    }

    [Benchmark(Description = "Table filter b2b;prd_products|ecm_*product")]
    public int TableFilter()
    {
        // Alternates between two texts so each call is a new filter (no cache).
        _model.SetTableFilter((_toggle++ & 1) == 0 ? "b2b;prd_products|ecm_*product" : "b2b;prd_products|ecm_*produc");
        return _model.Visible.Count;
    }

    [Benchmark(Description = "Table filter with lookahead ^(?!sys|tmp)")]
    public int Lookahead()
    {
        _model.SetTableFilter((_toggle++ & 1) == 0 ? "^(?!sys|tmp)" : "^(?!sys|tm)");
        return _model.Visible.Count;
    }

    [Benchmark(Description = "Database filter + Table filter")]
    public int Both()
    {
        var odd = (_toggle++ & 1) == 0;
        _model.SetDatabaseFilter(odd ? "db0" : "db1");
        _model.SetTableFilter(odd ? "users" : "orders");
        return _model.Visible.Count;
    }
}
