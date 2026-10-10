using Eunomia.Engine.Sql.Filters;

namespace Eunomia.Engine.Sql.Tests;

/// <summary>
/// Section 4.3 cases at expression level (FAM-T04). The rules that depend on the node kind
/// (target of each filter, favourites, databases without objects) are in <c>UI.Controls.Tree.Tests</c>.
/// Not validated at runtime against HeidiSQL (phase 0 was not done).
/// </summary>
public sealed class TreeFilterExpressionTests
{
    [Theory]
    // Example from screenshot 1 (b2b;prd_products|ecm_*product).
    [InlineData("b2b;prd_products|ecm_*product", "b2b_budgets", true)]
    [InlineData("b2b;prd_products|ecm_*product", "prd_products", true)]
    [InlineData("b2b;prd_products|ecm_*product", "prd_products_web_roles", true)]
    [InlineData("b2b;prd_products|ecm_*product", "ecm_products_discount", true)]
    [InlineData("b2b;prd_products|ecm_*product", "ecm_stores_products", false)]
    [InlineData("b2b;prd_products|ecm_*product", "ecm_orders", false)]
    // Prefix and suffix with ^ and $.
    [InlineData("^prd_", "prd_products", true)]
    [InlineData("^prd_", "prd_categories", true)]
    [InlineData("^prd_", "std_prd_x", false)]
    [InlineData("_log$", "app_log", true)]
    [InlineData("_log$", "log_app", false)]
    // Normal regex: \d, and * is a quantifier, not a wildcard.
    [InlineData(@"phpbb_\d", "phpbb_1users", true)]
    [InlineData(@"phpbb_\d", "phpbb_users", false)]
    [InlineData("test.*", "my_test_table", true)]
    [InlineData("test.*", "contest", true)]
    [InlineData("test.*", "tset", false)]
    [InlineData("ecm_*product", "ecmproduct", true)]
    [InlineData("ecm_*product", "ecm__product", true)]
    // Partial match.
    [InlineData("ecm_order", "ecm_orders", true)]
    [InlineData("ecm_order", "ecm_orders_status", true)]
    [InlineData("order", "ecm_orders_status", true)]
    // Negation through a negative lookahead, case-insensitive.
    [InlineData("^(?!sys)", "users", true)]
    [InlineData("^(?!sys)", "orders", true)]
    [InlineData("^(?!sys)", "sys_config", false)]
    [InlineData("^(?!sys)", "SYSLOG", false)]
    [InlineData("^(?!tmp_)", "tmp_import", false)]
    // AND through a lookahead.
    [InlineData("^(?=.*prd)(?=.*web)", "prd_products_web_roles", true)]
    [InlineData("^(?=.*prd)(?=.*web)", "prd_products", false)]
    // Always case-insensitive; (?-i) forces case sensitivity.
    [InlineData("PRD", "prd_products", true)]
    [InlineData("prd", "PRD_PRODUCTS", true)]
    [InlineData("(?-i)Prd", "prd_x", false)]
    [InlineData("(?-i)Prd", "Prd_x", true)]
    [InlineData("AÇÃO", "orders_ação", true)]
    // ; and | are both OR.
    [InlineData("a;b|c", "xcx", true)]
    [InlineData("users;orders", "orders", true)]
    [InlineData("users;orders", "items", false)]
    // Spaces are significant (no trim).
    [InlineData("b2b; prd", "prd_products", false)]
    [InlineData("b2b; prd", "b2b_budgets", true)]
    [InlineData("b2b; prd", "old prd", true)]
    // Metacharacters.
    [InlineData("a.c", "abc", true)]
    [InlineData("colou?r", "color", true)]
    [InlineData("colou?r", "colour", true)]
    [InlineData("ab+c", "ac", false)]
    [InlineData(@"a\.b", "a.b", true)]
    [InlineData(@"a\.b", "axb", false)]
    // A literal ; is impossible: \; becomes \| (a literal pipe).
    [InlineData(@"a\;b", "a|b", true)]
    [InlineData(@"a\;b", "a;b", false)]
    public void Matches(string filter, string name, bool expected)
    {
        Assert.Equal(expected, new TreeFilterExpression(filter).IsMatch(name));
    }

    [Fact]
    public void SemicolonBecomesPipeInsideParentheses()
    {
        Assert.Equal("(a|b|c)", new TreeFilterExpression("a;b|c").Pattern);
        Assert.Equal("(b2b| prd)", new TreeFilterExpression("b2b; prd").Pattern);
    }

    [Fact]
    public void EmptyBoxDoesNotFilter()
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
    public void InvalidRegexLeavesEverythingVisibleAndGivesAnError(string filter)
    {
        var f = new TreeFilterExpression(filter);

        Assert.False(f.IsActive);
        Assert.NotNull(f.Error);
        Assert.True(f.IsMatch("prd_products"));
        Assert.True(f.IsMatch(""));
    }

    [Fact]
    public void TimeoutIsFiftyMillisecondsAndLeavesTheNodeVisible()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(50), TreeFilterExpression.MatchTimeout);

        // Catastrophic backtracking: the timeout fires and the node stays visible.
        var f = new TreeFilterExpression("^(x+x+)+y$");
        Assert.True(f.IsMatch(new string('x', 40)));
    }

    [Fact]
    public void HistoryKeepsTwentyMostRecentFirst()
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
