using Eunomia.App.Query;
using Eunomia.App.Settings;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Sql;

namespace Eunomia.App.Tests;

/// <summary>Query tab helpers panel (appendix C §1.11, §3.9; FAM-Q06).</summary>
public sealed class QueryHelpersTests
{
    private static readonly DbObjectRef People = new("shop", "shop", "people", DbObjectKind.Table);

    private static readonly ISqlLanguage Sql = SqlLanguages.MySql;

    private static ColumnDefinition Column(string name, string type) => new(name, type, new LogicalType(LogicalTypeKind.Text), true, null, false, false, false, null);

    [Fact]
    public void FiveSectionsWithColumnsSnippetsAndHistory()
    {
        var today = new DateTime(2026, 10, 6, 15, 0, 0, DateTimeKind.Local);
        var history = new[]
        {
            new QueryHistoryEntry(1, "it", "shop", "SELECT 1", new DateTimeOffset(today.AddHours(-1)), TimeSpan.FromMilliseconds(123)),
            new QueryHistoryEntry(2, "it", "shop", "SELECT\n2", new DateTimeOffset(today.AddDays(-1)), TimeSpan.FromSeconds(2)),
            new QueryHistoryEntry(3, "it", "shop", "SELECT 3", new DateTimeOffset(today.AddDays(-5)), TimeSpan.Zero),
        };
        var snippets = Path.Combine(Path.GetTempPath(), "s");
        var roots = QueryHelpers.Build(Sql, People, [Column("id", "INT"), Column("name", "VARCHAR(20)")], [Path.Combine(snippets, "b.sql"), Path.Combine(snippets, "a.sql")], history, today);

        Assert.Equal(["Columns in people", "SQL functions", "SQL keywords", "Snippets", "Query history"], roots.Select(r => r.Text));
        Assert.Equal(("name", "varchar(20)"), (roots[0].Children[1].Text, roots[0].Children[1].Attribute));
        Assert.Contains(roots[1].Children, f => f.Text == "CONCAT");
        Assert.Equal(["a", "b"], roots[3].Children.Select(s => s.Text));
        Assert.Equal(["2026-10-06 (today)", "2026-10-05 (yesterday)", "2026-10-01"], roots[4].Children.Select(d => d.Text));
        var item = roots[4].Children[0].Children[0];
        Assert.Equal(("14:00: SELECT 1", "0.123s"), (item.Text, item.Attribute));
        Assert.Equal("15:00: SELECT 2", roots[4].Children[1].Children[0].Text);

        Assert.Equal("Columns", QueryHelpers.Build(Sql, null, null, [], [], today)[0].Text);
    }

    [Fact]
    public void InsertAtCursorAndGenerate()
    {
        Assert.Equal("`select`", QueryHelpers.InsertText(Sql, new HelperItem(HelperKind.Column, "select"), _ => ""));
        Assert.Equal("NOW", QueryHelpers.InsertText(Sql, new HelperItem(HelperKind.Function, "NOW"), _ => ""));
        Assert.Equal("content", QueryHelpers.InsertText(Sql, new HelperItem(HelperKind.Snippet, "s", "", "x.sql"), p => p == "x.sql" ? "content" : ""));
        Assert.Null(QueryHelpers.InsertText(Sql, new HelperItem(HelperKind.Root, "Snippets"), _ => ""));

        var nl = Environment.NewLine;
        Assert.Equal($"SELECT id, name{nl}\tFROM shop.people;", QueryHelpers.Generate(Sql, "select", People, ["id", "name"]));
        Assert.Equal($"DELETE FROM shop.people{nl}\tWHERE id='';", QueryHelpers.Generate(Sql, "DELETE", People, ["id"]));
        Assert.StartsWith("UPDATE shop.people SET", QueryHelpers.Generate(Sql, "UPDATE", People, ["id"]), StringComparison.Ordinal);
        var order = new DbObjectRef("my db", "my db", "order", DbObjectKind.Table);
        Assert.Equal($"SELECT `select`, `a``b`{nl}\tFROM `my db`.`order`;", QueryHelpers.Generate(Sql, "SELECT", order, ["select", "a`b"]));
        Assert.Equal("a_b.sql", QueryHelpers.SnippetFileName("a/b"));
    }
}
