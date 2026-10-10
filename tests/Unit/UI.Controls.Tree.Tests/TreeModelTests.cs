namespace Eunomia.UI.Controls.Tree.Tests;

public sealed class TreeModelTests
{
    [Fact]
    public void ArrancaSoComAsSessoesSemCarregarNada()
    {
        var provider = new SyntheticTreeProvider();
        var model = new TreeModel(SyntheticTreeProvider.CreateSessions(), provider);

        Assert.Equal(5, model.Visible.Count);
        Assert.All(model.Visible, n => Assert.Null(n.Children));
        Assert.Equal(0, provider.LoadCount);
        Assert.Equal(5, model.Visible.Select(n => n.SessionColor).Distinct().Count());
    }

    [Fact]
    public async Task ExpandirMostraPlaceholderEnquantoCarrega()
    {
        var provider = new SyntheticTreeProvider(latency: TimeSpan.FromMilliseconds(100));
        var model = new TreeModel(SyntheticTreeProvider.CreateSessions(1), provider);
        var session = model.Roots[0];

        var expanding = model.ExpandAsync(session, TestContext.Current.CancellationToken);

        Assert.True(session.IsLoading);
        Assert.Equal(TreeNodeKind.Placeholder, model.Visible[1].Kind);
        Assert.Equal(TreeModel.PlaceholderText, model.Visible[1].Name);
        await expanding;
        Assert.False(session.IsLoading);
        Assert.Equal(21, model.Visible.Count);
        Assert.Equal("information_schema", model.Visible[1].Name);
    }

    [Fact]
    public async Task ExpandirDuranteOCarregamentoEsperaPelosFilhos()
    {
        // Before, the second ExpandAsync returned right away with the placeholder as its only child.
        var provider = new SyntheticTreeProvider(latency: TimeSpan.FromMilliseconds(100));
        var model = new TreeModel(SyntheticTreeProvider.CreateSessions(1), provider);
        var session = model.Roots[0];

        var first = model.ExpandAsync(session, TestContext.Current.CancellationToken);
        await model.ExpandAsync(session, TestContext.Current.CancellationToken);

        Assert.Equal(20, session.Children!.Count);
        Assert.DoesNotContain(session.Children, n => n.Kind == TreeNodeKind.Placeholder);
        await first;
        Assert.Equal(1, provider.LoadCount);
    }

    [Fact]
    public async Task ExpandirDuasVezesCarregaUmaVez()
    {
        var provider = new SyntheticTreeProvider();
        var model = new TreeModel(SyntheticTreeProvider.CreateSessions(1), provider);
        var session = model.Roots[0];

        await model.ExpandAsync(session, TestContext.Current.CancellationToken);
        model.Collapse(session);
        Assert.Single(model.Visible);
        await model.ExpandAsync(session, TestContext.Current.CancellationToken);

        Assert.Equal(1, provider.LoadCount);
        Assert.Equal(21, model.Visible.Count);
    }

    [Fact]
    public async Task ArvoreCompletaTemDezMilObjetos()
    {
        var model = new TreeModel(SyntheticTreeProvider.CreateSessions(), new SyntheticTreeProvider());
        foreach (var s in model.Roots)
        {
            await model.ExpandAllAsync(s, TestContext.Current.CancellationToken);
        }

        Assert.Equal(10_000, model.Visible.Count(n => n.IsObject));
        Assert.Equal(5 + 100 + 10_000, model.Visible.Count);
    }

    [Fact]
    public async Task ObjetosOrdenadosComComparacaoNatural()
    {
        var model = new TreeModel(SyntheticTreeProvider.CreateSessions(1), new SyntheticTreeProvider());
        await model.ExpandAllAsync(model.Roots[0], TestContext.Current.CancellationToken);

        foreach (var db in model.Roots[0].Children!)
        {
            var names = db.Children!.Select(c => c.Name).ToList();
            Assert.Equal(names.Order(NaturalComparer.Instance), names);
        }
    }

    [Theory]
    [InlineData("table2", "table10", -1)]
    [InlineData("table10", "table2", 1)]
    [InlineData("a", "B", -1)]
    [InlineData("x007", "x7", 0)]
    [InlineData("abc", "abcd", -1)]
    public void ComparacaoNatural(string a, string b, int sign)
    {
        Assert.Equal(sign, Math.Sign(NaturalComparer.Instance.Compare(a, b)));
    }

    [Fact]
    public async Task TamanhoDaBaseEhASomaDasTabelas()
    {
        var model = new TreeModel(SyntheticTreeProvider.CreateSessions(1), new SyntheticTreeProvider(databasesPerSession: 2));
        var session = model.Roots[0];
        await model.ExpandAsync(session, TestContext.Current.CancellationToken);
        Assert.Null(session.SizeBytes);

        var db0 = session.Children![0];
        await model.ExpandAsync(db0, TestContext.Current.CancellationToken);
        Assert.Equal(db0.Children!.Sum(t => t.SizeBytes ?? 0), db0.SizeBytes);
        // The session only sums when every database is cached.
        Assert.Null(session.SizeBytes);

        var db1 = session.Children[1];
        await model.ExpandAsync(db1, TestContext.Current.CancellationToken);
        Assert.Equal(db0.SizeBytes + db1.SizeBytes, session.SizeBytes);
    }

    [Fact]
    public async Task PesquisaIncrementalPorPrefixoComVolta()
    {
        var model = new TreeModel(SyntheticTreeProvider.CreateSessions(1), new SyntheticTreeProvider());
        await model.ExpandAsync(model.Roots[0], TestContext.Current.CancellationToken);

        var mysql = model.FindByPrefix("MY", 0);
        Assert.Equal("mysql", model.Visible[mysql].Name);
        Assert.Equal(mysql, model.FindByPrefix("mysql", mysql));
        Assert.Equal(mysql, model.FindByPrefix("my", mysql + 1));
        Assert.Equal(-1, model.FindByPrefix("zzz", 0));
    }

    [Fact]
    public async Task GruposPorTipoQuandoLigado()
    {
        var model = new TreeModel(SyntheticTreeProvider.CreateSessions(1), new SyntheticTreeProvider(databasesPerSession: 3, groupByType: true));
        await model.ExpandAllAsync(model.Roots[0], TestContext.Current.CancellationToken);

        var db = model.Roots[0].Children![2];
        Assert.Equal(["Tables", "Views", "Procedures", "Functions"], db.Children!.Select(g => g.Name));
        Assert.All(db.Children![1].Children!, v => Assert.Equal(TreeNodeKind.View, v.Kind));
    }
}
