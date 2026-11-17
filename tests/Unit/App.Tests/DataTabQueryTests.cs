using System.Globalization;
using Eunomia.App.Data;
using Eunomia.Drivers.Abstractions;
using Eunomia.Drivers.MySql;

namespace Eunomia.App.Tests;

/// <summary>Data tab SELECT and label (appendix D B2, B3, B6, B7, B8; FAM-G02, G03, G04).</summary>
public sealed class DataTabQueryTests
{
    private static readonly MySqlDialect Dialect = new(MySqlConnectionFactory.ParseIdentity("10.4.32-MariaDB", null));

    private static ColumnDefinition Col(string name, string type, bool nullable = true) =>
        new(name, type, new LogicalType(LogicalTypeKind.Text), nullable, null, false, false, false, null);

    private static TableDefinition Table(IReadOnlyList<ColumnDefinition> columns, params IndexDefinition[] indexes) =>
        new(new DbObjectRef("shop", "shop", "products", DbObjectKind.Table), columns, indexes, [], [], new Dictionary<string, string>());

    private static readonly ColumnDefinition[] Columns =
    [
        Col("id", "int(11)", nullable: false),
        Col("name", "varchar(100)"),
        Col("description", "text"),
        Col("attributes", "longtext"),
        Col("code", "varchar(1000)"),
        Col("photo", "mediumblob"),
        Col("extra", "json"),
    ];

    [Fact]
    public void WithAKeyCutsLongColumnsExceptTheKey()
    {
        var table = Table(Columns, new IndexDefinition("PRIMARY", IndexKind.Primary, ["id"]));

        var select = DataTabQuery.Build(Dialect, table, [], null, [], 0, 1000);

        Assert.Equal(
            "SELECT `id`, `name`, LEFT(`description`, 256), LEFT(`attributes`, 256), LEFT(`code`, 256), LEFT(`photo`, 256), " +
            "LEFT(CAST(`extra` AS CHAR), 256) FROM `shop`.`products` LIMIT 1000", select.Sql);
        Assert.Equal(["id"], select.KeyColumns);
        Assert.Equal(5, select.TruncatedColumns.Count);
        Assert.DoesNotContain("name", select.TruncatedColumns);
    }

    [Fact]
    public void WithoutAKeyBringsEverythingCompleteWithAsterisk()
    {
        var select = DataTabQuery.Build(Dialect, Table(Columns), ["name"], null, [], 0, 1000);

        Assert.Equal("SELECT * FROM `shop`.`products` LIMIT 1000", select.Sql);
        Assert.Empty(select.TruncatedColumns);
        Assert.Contains("name", select.Columns);
    }

    [Fact]
    public void UniqueNotNullServesAsKeyAndHiddenColumnsLeaveTheSelect()
    {
        var columns = new[] { Col("code", "varchar(20)", nullable: false), Col("name", "varchar(50)"), Col("note", "varchar(50)") };
        var table = Table(columns, new IndexDefinition("u_name", IndexKind.Unique, ["name"]), new IndexDefinition("u_code", IndexKind.Unique, ["code"]));

        var select = DataTabQuery.Build(Dialect, table, ["note", "code"], null, [], 0, 1000);

        Assert.Equal(["code"], select.KeyColumns);
        // The hidden key stays in the SELECT; the hidden column that is not a key goes.
        Assert.Equal("SELECT `code`, `name` FROM `shop`.`products` LIMIT 1000", select.Sql);
    }

    [Fact]
    public void FilterSortingAndPaging()
    {
        var table = Table([Col("id", "int(11)", false), Col("name", "varchar(10)")], new IndexDefinition("PRIMARY", IndexKind.Primary, ["id"]));
        IReadOnlyList<DataSort> sort = [new("name", false), new("id", true), new("desaparecida", false)];

        var select = DataTabQuery.Build(Dialect, table, [], "  WHERE name LIKE 'a%'\n", sort, 2000, 1000);

        Assert.Equal("SELECT * FROM `shop`.`products` WHERE name LIKE 'a%' ORDER BY `name` ASC, `id` DESC LIMIT 2000, 1000", select.Sql);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("  -- only a comment\n /* and another */ ", null)]
    [InlineData("WHERE", null)]
    [InlineData("where id = 1", "id = 1")]
    [InlineData("WHEREVER = 1", "WHEREVER = 1")]
    public void NormalisesTheFilter(string text, string? expected)
    {
        Assert.Equal(expected, DataTabQuery.NormalizeFilter(text));
    }

    [Fact]
    public void HeaderClickAccumulatesSorting()
    {
        var sort = DataTabQuery.ToggleSort([], "a", shift: false);
        sort = DataTabQuery.ToggleSort(sort, "b", shift: true);
        Assert.Equal([new DataSort("a", false), new DataSort("b", true)], sort);

        sort = DataTabQuery.ToggleSort(sort, "a", shift: false);
        Assert.Equal(new DataSort("a", true), sort[0]);
        sort = DataTabQuery.ToggleSort(sort, "a", shift: false);
        Assert.Equal([new DataSort("b", true)], sort);
    }

    [Fact]
    public void FormatsSavedPerTable()
    {
        IReadOnlyList<DataSort> sort = [new("name", false), new("created_at", true)];
        Assert.Equal("0_name|1_created_at", DataTabQuery.FormatSort(sort));
        Assert.Equal(sort, DataTabQuery.ParseSort("0_name|1_created_at"));
        Assert.Equal(["a", "b"], DataTabQuery.ParseHidden(DataTabQuery.FormatHidden(["a", "b"])));
        Assert.Empty(DataTabQuery.ParseHidden(null));
    }

    [Fact]
    public void LabelVariants()
    {
        var c = CultureInfo.InvariantCulture;
        Assert.Equal("shop.products: 272 rows total (exact)", DataTabQuery.CountLabel("shop", "products", new DataCount(272, false, false, null, false, true), c));
        Assert.Equal("shop.products: 272 rows total", DataTabQuery.CountLabel("shop", "products", new DataCount(272, false, false, null, false, false), c));
        Assert.Equal("shop.products: 12,345 rows total (approximately), limited to 1,000",
            DataTabQuery.CountLabel("shop", "products", new DataCount(1000, true, false, 12_345, false, true), c));
        Assert.Equal("shop.products: 272 rows total (exact), 3 rows match to filter",
            DataTabQuery.CountLabel("shop", "products", new DataCount(3, false, true, 272, true, true), c));
        Assert.Equal("shop.products: 3 rows total, all rows match to filter",
            DataTabQuery.CountLabel("shop", "products", new DataCount(3, false, true, 3, false, false), c));
        Assert.Equal("shop.v: 10 rows", DataTabQuery.CountLabel("shop", "v", new DataCount(10, false, false, null, false, false, IsView: true), c));
    }

    [Theory]
    // Small rows (or unknown size): the setting.
    [InlineData(1000, 50_000_000L, 1_000_000L, 1000)]
    [InlineData(1000, null, 1_000_000L, 1000)]
    [InlineData(1000, 50_000_000L, null, 1000)]
    [InlineData(1000, 0L, 0L, 1000)]
    [InlineData(1000, 16_384L, 0L, 1000)]
    // 11 GB in 40,000 rows (275 KB each): 32 MiB / 275 KB = 122 rows.
    [InlineData(1000, 11_000_000_000L, 40_000L, 122)]
    // 1 MB rows: 33; 50 MB rows: never fewer than 10.
    [InlineData(1000, 1_000_000_000L, 1_000L, 33)]
    [InlineData(1000, 50_000_000_000L, 1_000L, 10)]
    // Never more than the setting, and a setting below the minimum stays.
    [InlineData(50, 100_000L, 1_000L, 50)]
    [InlineData(5, 50_000_000_000L, 1_000L, 5)]
    [InlineData(0, null, null, 1)]
    public void LargeRowsGetFewerPerPage(int setting, long? dataBytes, long? rows, int expected) =>
        Assert.Equal(expected, DataTabQuery.RowsPerStepFor(setting, dataBytes, rows));

    [Fact]
    public void LoadingAndCancelledLabels()
    {
        Assert.Equal("shop.products: loading ...", DataTabQuery.LoadingLabel("shop", "products", TimeSpan.FromMilliseconds(900)));
        Assert.Equal("shop.products: loading ... 1 s (Esc to cancel)", DataTabQuery.LoadingLabel("shop", "products", TimeSpan.FromSeconds(1.2)));
        Assert.Equal("shop.products: loading ... 59 s (Esc to cancel)", DataTabQuery.LoadingLabel("shop", "products", TimeSpan.FromSeconds(59.9)));
        Assert.Equal("shop.products: loading ... 2 min 05 s (Esc to cancel)", DataTabQuery.LoadingLabel("shop", "products", TimeSpan.FromSeconds(125)));
        Assert.Equal("shop.products: 1,500 rows loaded (cancelled)", DataTabQuery.CancelledLabel("shop", "products", 1500, CultureInfo.InvariantCulture));
        Assert.Equal("shop.products: loading cancelled", DataTabQuery.CancelledLabel("shop", "products", null));
        Assert.Equal("shop.products: loading cancelled", DataTabQuery.CancelledLabel("shop", "products", 0));
    }
}
