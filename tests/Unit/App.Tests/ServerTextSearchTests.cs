using Eunomia.App.Search;
using Eunomia.Drivers.Abstractions;
using Eunomia.Drivers.MySql;
using Eunomia.Engine.Results;

namespace Eunomia.App.Tests;

/// <summary>Find text on server (anexo B §2.4): condições por coluna e consultas por tabela.</summary>
public sealed class ServerTextSearchTests
{
    private static readonly MySqlDialect Dialect = new(MySqlConnectionFactory.ParseIdentity("10.4.32-MariaDB", null));

    private static readonly TableDefinition Table = new(
        new DbObjectRef("shop", "shop", "people", DbObjectKind.Table),
        [
            Column("id", LogicalTypeKind.Integer),
            Column("name", LogicalTypeKind.Text),
            Column("born", LogicalTypeKind.Date),
        ],
        [], [], [], new Dictionary<string, string>());

    private static ColumnDefinition Column(string name, LogicalTypeKind kind) =>
        new(name, kind.ToString().ToLowerInvariant(), new LogicalType(kind), true, null, false, false, false, null);

    [Fact]
    public void TextoSimplesSemMaiusculasEmTodasAsColunas()
    {
        var sql = ServerTextSearch.CountQuery(Dialect, Table, new ServerTextSearchOptions("Ana", false, null, ServerTextMatch.Contains, false));

        Assert.Equal(
            "SELECT COUNT(*) FROM `shop`.`people` WHERE LOWER(CONVERT(`id` USING utf8mb4)) LIKE '%ana%' ESCAPE '!' OR LOWER(CONVERT(`name` USING utf8mb4)) LIKE '%ana%' ESCAPE '!' OR LOWER(CONVERT(`born` USING utf8mb4)) LIKE '%ana%' ESCAPE '!'",
            sql);
    }

    [Fact]
    public void CategoriaMaiusculasERegex()
    {
        var text = new ServerTextSearchOptions("An'a", false, TypeCategory.Text, ServerTextMatch.StartsWith, true);
        Assert.Equal("`name` LIKE BINARY 'An''a%' ESCAPE '!'", ServerTextSearch.Where(Dialect, Table, text));

        var regex = new ServerTextSearchOptions("^A.+", false, TypeCategory.Text, ServerTextMatch.Regex, false);
        Assert.Equal("`name` REGEXP '^A.+'", ServerTextSearch.Where(Dialect, Table, regex));
    }

    [Fact]
    public void ExactoNumericoEPredicadoSql()
    {
        var exact = new ServerTextSearchOptions("42", false, TypeCategory.Integer, ServerTextMatch.Exact, false);
        Assert.Equal("`id` = 42", ServerTextSearch.Where(Dialect, Table, exact));
        Assert.Null(ServerTextSearch.Where(Dialect, Table, exact with { Text = "abc" }));

        var predicate = new ServerTextSearchOptions("> NOW()", true, TypeCategory.Temporal, ServerTextMatch.Contains, false);
        Assert.Equal("SELECT * FROM `shop`.`people` WHERE `born` > NOW();", ServerTextSearch.SelectQuery(Dialect, Table, predicate));
    }

    [Fact]
    public void RelevanciaEmPercentagem()
    {
        Assert.Equal(25, new ServerTextSearchHit(Table.Ref, 5, 20).Relevance);
        Assert.Null(new ServerTextSearchHit(Table.Ref, 5, null).Relevance);
    }
}
