using Eunomia.Engine.Sql.Filters;

namespace Eunomia.Engine.Sql.Tests;

/// <summary>
/// Casos da secção 4.3 ao nível da expressão (FAM-T04). As regras que dependem do tipo de nó
/// (alvo de cada filtro, favoritos, bases sem objetos) estão em <c>UI.Controls.Tree.Tests</c>.
/// Não validados em runtime no HeidiSQL (a Fase 0 não foi feita).
/// </summary>
public sealed class TreeFilterExpressionTests
{
    [Theory]
    // Exemplo do screenshot 1 (b2b;prd_products|ecm_*product).
    [InlineData("b2b;prd_products|ecm_*product", "b2b_budgets", true)]
    [InlineData("b2b;prd_products|ecm_*product", "prd_products", true)]
    [InlineData("b2b;prd_products|ecm_*product", "prd_products_web_roles", true)]
    [InlineData("b2b;prd_products|ecm_*product", "ecm_products_discount", true)]
    [InlineData("b2b;prd_products|ecm_*product", "ecm_stores_products", false)]
    [InlineData("b2b;prd_products|ecm_*product", "ecm_orders", false)]
    // Prefixo e sufixo com ^ e $.
    [InlineData("^prd_", "prd_products", true)]
    [InlineData("^prd_", "prd_categories", true)]
    [InlineData("^prd_", "std_prd_x", false)]
    [InlineData("_log$", "app_log", true)]
    [InlineData("_log$", "log_app", false)]
    // Regex normal: \d, e * é quantificador, não wildcard.
    [InlineData(@"phpbb_\d", "phpbb_1users", true)]
    [InlineData(@"phpbb_\d", "phpbb_users", false)]
    [InlineData("test.*", "my_test_table", true)]
    [InlineData("test.*", "contest", true)]
    [InlineData("test.*", "tset", false)]
    [InlineData("ecm_*product", "ecmproduct", true)]
    [InlineData("ecm_*product", "ecm__product", true)]
    // Correspondência parcial.
    [InlineData("ecm_order", "ecm_orders", true)]
    [InlineData("ecm_order", "ecm_orders_status", true)]
    [InlineData("order", "ecm_orders_status", true)]
    // Negação por lookahead negativo, case-insensitive.
    [InlineData("^(?!sys)", "users", true)]
    [InlineData("^(?!sys)", "orders", true)]
    [InlineData("^(?!sys)", "sys_config", false)]
    [InlineData("^(?!sys)", "SYSLOG", false)]
    [InlineData("^(?!tmp_)", "tmp_import", false)]
    // AND por lookahead.
    [InlineData("^(?=.*prd)(?=.*web)", "prd_products_web_roles", true)]
    [InlineData("^(?=.*prd)(?=.*web)", "prd_products", false)]
    // Sempre case-insensitive; (?-i) força sensibilidade.
    [InlineData("PRD", "prd_products", true)]
    [InlineData("prd", "PRD_PRODUCTS", true)]
    [InlineData("(?-i)Prd", "prd_x", false)]
    [InlineData("(?-i)Prd", "Prd_x", true)]
    [InlineData("AÇÃO", "tabela_ação", true)]
    // ; e | são ambos OR.
    [InlineData("a;b|c", "xcx", true)]
    [InlineData("users;orders", "orders", true)]
    [InlineData("users;orders", "items", false)]
    // Espaços são significativos (sem trim).
    [InlineData("b2b; prd", "prd_products", false)]
    [InlineData("b2b; prd", "b2b_budgets", true)]
    [InlineData("b2b; prd", "old prd", true)]
    // Metacaracteres.
    [InlineData("a.c", "abc", true)]
    [InlineData("colou?r", "color", true)]
    [InlineData("colou?r", "colour", true)]
    [InlineData("ab+c", "ac", false)]
    [InlineData(@"a\.b", "a.b", true)]
    [InlineData(@"a\.b", "axb", false)]
    // Um ; literal é impossível: \; vira \| (pipe literal).
    [InlineData(@"a\;b", "a|b", true)]
    [InlineData(@"a\;b", "a;b", false)]
    public void Corresponde(string filter, string name, bool expected)
    {
        Assert.Equal(expected, new TreeFilterExpression(filter).IsMatch(name));
    }

    [Fact]
    public void PontoEVirgulaViraPipeDentroDeParenteses()
    {
        Assert.Equal("(a|b|c)", new TreeFilterExpression("a;b|c").Pattern);
        Assert.Equal("(b2b| prd)", new TreeFilterExpression("b2b; prd").Pattern);
    }

    [Fact]
    public void CaixaVaziaNaoFiltra()
    {
        var f = new TreeFilterExpression("");

        Assert.False(f.IsActive);
        Assert.Null(f.Pattern);
        Assert.True(f.IsMatch("qualquer"));
    }

    [Theory]
    [InlineData("(")]
    [InlineData("prd_[")]
    [InlineData("*abc")]
    public void RegexInvalidaDeixaTudoVisivelEDaErro(string filter)
    {
        var f = new TreeFilterExpression(filter);

        Assert.False(f.IsActive);
        Assert.NotNull(f.Error);
        Assert.True(f.IsMatch("prd_products"));
        Assert.True(f.IsMatch(""));
    }

    [Fact]
    public void TimeoutEhCinquentaMilissegundosEDeixaONoVisivel()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(50), TreeFilterExpression.MatchTimeout);

        // Backtracking catastrófico: o timeout dispara e o nó fica visível.
        var f = new TreeFilterExpression("^(x+x+)+y$");
        Assert.True(f.IsMatch(new string('x', 40)));
    }

    [Fact]
    public void HistoricoGuardaVinteMaisRecentesPrimeiro()
    {
        var h = new FilterHistory();
        for (var i = 0; i < 25; i++)
        {
            h.Add($"f{i}");
        }

        Assert.Equal(20, h.Entries.Count);
        Assert.Equal("f24", h.Entries[0]);
        Assert.Equal("f5", h.Entries[19]);

        h.Add("f10");
        Assert.Equal("f10", h.Entries[0]);
        Assert.Equal(20, h.Entries.Count);

        h.Add("");
        Assert.Equal("f10", h.Entries[0]);

        h.Clear();
        Assert.Empty(h.Entries);
    }
}
