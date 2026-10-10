using BenchmarkDotNet.Attributes;
using Eunomia.UI.Controls.Tree;

namespace Eunomia.Bench.Micro;

/// <summary>
/// PR-11: Table filter sobre 10.000 nós ≤ 50 ms. Árvore sintética completamente expandida
/// (5 sessões × 20 bases × 100 objetos); cada operação aplica um filtro novo e reconstrói os visíveis.
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
            throw new InvalidOperationException("A árvore devia ter 10.000 objetos.");
        }
    }

    [Benchmark(Description = "Table filter b2b;prd_products|ecm_*product")]
    public int TableFilter()
    {
        // Alterna entre dois textos para que cada chamada seja um filtro novo (sem cache).
        _model.SetTableFilter((_toggle++ & 1) == 0 ? "b2b;prd_products|ecm_*product" : "b2b;prd_products|ecm_*produc");
        return _model.Visible.Count;
    }

    [Benchmark(Description = "Table filter com lookahead ^(?!sys|tmp)")]
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
