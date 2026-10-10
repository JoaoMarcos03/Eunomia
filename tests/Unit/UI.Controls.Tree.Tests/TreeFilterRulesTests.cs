namespace Eunomia.UI.Controls.Tree.Tests;

/// <summary>
/// Regras da secção 4.3 que dependem do tipo de nó, uma por teste (FAM-T04).
/// Os casos de regex estão em <c>Engine.Sql.Tests/TreeFilterExpressionTests</c>.
/// </summary>
public sealed class TreeFilterRulesTests
{
    /// <summary>Provider com filhos fixos por nó, sem latência.</summary>
    private sealed class StaticProvider : ITreeDataProvider
    {
        public Dictionary<TreeNode, Func<TreeNode, IReadOnlyList<TreeNode>>> Children { get; } = [];

        public Task<IReadOnlyList<TreeNode>> LoadChildrenAsync(TreeNode parent, CancellationToken cancellationToken) =>
            Task.FromResult(Children.TryGetValue(parent, out var f) ? f(parent) : (IReadOnlyList<TreeNode>)[]);
    }

    /// <summary>
    /// localhost: elmwood (b2b_budgets, prd_products, ecm_orders, v_prd_list [view], sp_prd_sync [proc]),
    ///            prd_archive (users), test (tmp_x).
    /// server-01: elmwood2 (prd_products).
    /// </summary>
    private static async Task<(TreeModel Model, TreeNode[] Sessions)> BuildAsync(bool expandAll = true)
    {
        var provider = new StaticProvider();
        var s1 = new TreeNode(TreeNodeKind.Session, "localhost") { SessionColor = 0xFF006400 };
        var s2 = new TreeNode(TreeNodeKind.Session, "server-01") { SessionColor = 0xFFDC143C };
        provider.Children[s1] = p => [new TreeNode(TreeNodeKind.Database, "elmwood", p), new TreeNode(TreeNodeKind.Database, "prd_archive", p), new TreeNode(TreeNodeKind.Database, "test", p)];
        provider.Children[s2] = p => [new TreeNode(TreeNodeKind.Database, "elmwood2", p)];
        var model = new TreeModel([s1, s2], new DelegatingProvider(provider, (parent) => parent.Kind == TreeNodeKind.Database ? DatabaseObjects(parent) : null));
        if (expandAll)
        {
            await model.ExpandAllAsync(s1, TestContext.Current.CancellationToken);
            await model.ExpandAllAsync(s2, TestContext.Current.CancellationToken);
        }
        return (model, [s1, s2]);
    }

    private static IReadOnlyList<TreeNode> DatabaseObjects(TreeNode db) => db.Name switch
    {
        "elmwood" =>
        [
            new TreeNode(TreeNodeKind.Table, "b2b_budgets", db),
            new TreeNode(TreeNodeKind.Table, "ecm_orders", db),
            new TreeNode(TreeNodeKind.Table, "prd_products", db) { IsFavorite = true },
            new TreeNode(TreeNodeKind.Procedure, "sp_prd_sync", db),
            new TreeNode(TreeNodeKind.View, "v_prd_list", db),
        ],
        "prd_archive" => [new TreeNode(TreeNodeKind.Table, "users", db)],
        "test" => [new TreeNode(TreeNodeKind.Table, "tmp_x", db)],
        _ => [new TreeNode(TreeNodeKind.Table, "prd_products", db)],
    };

    private sealed class DelegatingProvider(StaticProvider inner, Func<TreeNode, IReadOnlyList<TreeNode>?> extra) : ITreeDataProvider
    {
        public int Loads { get; private set; }

        public Task<IReadOnlyList<TreeNode>> LoadChildrenAsync(TreeNode parent, CancellationToken cancellationToken)
        {
            Loads++;
            return extra(parent) is { } list ? Task.FromResult(list) : inner.LoadChildrenAsync(parent, cancellationToken);
        }
    }

    private static List<string> Names(TreeModel model) => model.Visible.Select(n => n.Name).ToList();

    [Fact]
    public async Task SemFiltrosMostraTudoOQueEstaExpandido()
    {
        var (model, _) = await BuildAsync();

        Assert.Equal(
            ["localhost", "elmwood", "b2b_budgets", "ecm_orders", "prd_products", "sp_prd_sync", "v_prd_list",
             "prd_archive", "users", "test", "tmp_x", "server-01", "elmwood2", "prd_products"],
            Names(model));
    }

    [Fact]
    public async Task DatabaseFilterEscondeBasesEOsSeusFilhos()
    {
        var (model, _) = await BuildAsync();

        model.SetDatabaseFilter("^elmwood$");

        Assert.Equal(["localhost", "elmwood", "b2b_budgets", "ecm_orders", "prd_products", "sp_prd_sync", "v_prd_list", "server-01"], Names(model));
    }

    [Fact]
    public async Task DatabaseFilterNaoSeAplicaAObjetos()
    {
        var (model, _) = await BuildAsync();

        // "prd" corresponde à base prd_archive e a várias tabelas; só a base conta.
        model.SetDatabaseFilter("prd");

        Assert.Equal(["localhost", "prd_archive", "users", "server-01"], Names(model));
    }

    [Fact]
    public async Task TableFilterSoSeAplicaAObjetos()
    {
        var (model, _) = await BuildAsync();

        model.SetTableFilter("prd");

        // Sessões e bases ficam; tabela, view e procedure filtradas pelo nome.
        Assert.Equal(
            ["localhost", "elmwood", "prd_products", "sp_prd_sync", "v_prd_list", "prd_archive", "test", "server-01", "elmwood2", "prd_products"],
            Names(model));
    }

    [Fact]
    public async Task BasesSemObjetosQueCorrespondamContinuamVisiveis()
    {
        var (model, _) = await BuildAsync();

        model.SetTableFilter("nada_corresponde");

        Assert.Equal(["localhost", "elmwood", "prd_archive", "test", "server-01", "elmwood2"], Names(model));
    }

    [Fact]
    public async Task TableFilterComparaSoONomeDoObjetoSemABase()
    {
        var (model, _) = await BuildAsync();

        // A base chama-se prd_archive, mas a tabela "users" não contém "prd".
        model.SetTableFilter("prd_archive");

        Assert.DoesNotContain("users", Names(model));
        Assert.Contains("prd_archive", Names(model));
    }

    [Fact]
    public async Task GruposEColunasNuncaSaoEscondidos()
    {
        var db = new TreeNode(TreeNodeKind.Database, "shop");
        var group = new TreeNode(TreeNodeKind.Group, "Tables", db);
        var table = new TreeNode(TreeNodeKind.Table, "orders", group);
        var column = new TreeNode(TreeNodeKind.Column, "prd_col", table);
        var provider = new StaticProvider();
        provider.Children[db] = _ => [group];
        provider.Children[group] = _ => [table];
        var model = new TreeModel([db], provider);
        await model.ExpandAllAsync(db, TestContext.Current.CancellationToken);

        model.SetTableFilter("prd");

        Assert.Equal(["shop", "Tables"], Names(model));
        Assert.True(model.IsVisible(group));
        // Uma coluna não é filtrada pelo nome, mas fica escondida com a tabela.
        Assert.False(model.IsVisible(column));
        model.SetTableFilter("orders");
        Assert.True(model.IsVisible(column));
    }

    [Fact]
    public async Task FiltrosAtuamEmTodasAsSessoes()
    {
        var (model, _) = await BuildAsync();

        model.SetTableFilter("^prd_products$");

        Assert.Equal(2, model.Visible.Count(n => n.Name == "prd_products"));
    }

    [Fact]
    public async Task SoFavoritosMostraSoObjetosFavoritos()
    {
        var (model, _) = await BuildAsync();

        model.SetOnlyFavorites(true);

        Assert.Equal(["localhost", "elmwood", "prd_products", "prd_archive", "test", "server-01", "elmwood2"], Names(model));
    }

    [Fact]
    public async Task FavoritosEFiltroCombinamComoE()
    {
        var (model, _) = await BuildAsync();
        var ecm = model.Visible.Single(n => n.Name == "ecm_orders");
        model.ToggleFavorite(ecm);

        model.SetOnlyFavorites(true);
        model.SetTableFilter("ecm");

        Assert.Equal(["localhost", "elmwood", "ecm_orders", "prd_archive", "test", "server-01", "elmwood2"], Names(model));
        Assert.True(ecm.IsFavorite);
    }

    [Fact]
    public async Task ToggleFavoriteNaoSeAplicaASessoesNemBases()
    {
        var (model, sessions) = await BuildAsync();

        model.ToggleFavorite(sessions[0]);
        model.ToggleFavorite(model.Visible.Single(n => n.Name == "elmwood"));

        Assert.False(sessions[0].IsFavorite);
        Assert.False(model.Visible.Single(n => n.Name == "elmwood").IsFavorite);
    }

    [Fact]
    public async Task BasesExpandidasDepoisDoFiltroSaoFiltradasAoExpandir()
    {
        var (model, sessions) = await BuildAsync(expandAll: false);
        model.SetTableFilter("^b2b");

        await model.ExpandAsync(sessions[0], TestContext.Current.CancellationToken);
        var elmwood = model.Visible.Single(n => n.Name == "elmwood");
        await model.ExpandAsync(elmwood, TestContext.Current.CancellationToken);

        Assert.Equal(["localhost", "elmwood", "b2b_budgets", "prd_archive", "test", "server-01"], Names(model));
    }

    [Fact]
    public async Task LimparOFiltroRepoeTudo()
    {
        var (model, _) = await BuildAsync();
        var all = Names(model);

        model.SetTableFilter("prd");
        model.SetDatabaseFilter("eco");
        model.SetTableFilter("");
        model.SetDatabaseFilter("");

        Assert.Equal(all, Names(model));
    }

    [Fact]
    public async Task RegexInvalidaMostraTudoEReportaUmaVez()
    {
        var (model, _) = await BuildAsync();
        var all = Names(model);
        var errors = new List<string>();
        model.FilterError += (_, e) => errors.Add(e);

        // Enquanto se escreve "(", a árvore mostra tudo.
        model.SetTableFilter("(");
        model.SetTableFilter("(");

        Assert.Equal(all, Names(model));
        Assert.Single(errors);
    }

    [Fact]
    public async Task PontoEVirgulaEhOrSobreNosDaArvore()
    {
        var (model, _) = await BuildAsync();

        model.SetTableFilter("b2b;ecm");

        Assert.Equal(["b2b_budgets", "ecm_orders"], model.Visible.Where(n => n.IsObject).Select(n => n.Name));
    }

    [Fact]
    public async Task FiltroCaseInsensitiveSobreNosDaArvore()
    {
        var (model, _) = await BuildAsync();

        model.SetTableFilter("B2B_BUDGETS");

        Assert.Equal(["b2b_budgets"], model.Visible.Where(n => n.IsObject).Select(n => n.Name));
    }
}
