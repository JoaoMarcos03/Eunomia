using Eunomia.App.Data;
using Eunomia.Drivers.Abstractions;
using Eunomia.Drivers.MySql;
using Eunomia.Engine.Execution;
using Eunomia.Engine.Results;
using MySqlConnector;

namespace Eunomia.Integration.Tests;

/// <summary>Data tab against the server (phase 5; appendix D B2–B6; FAM-G02, G03, C01; PR-17).</summary>
public sealed class TableBrowserTests : IAsyncLifetime
{
    private SessionHost _host = null!;
    private string _database = string.Empty;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>0..9 (portable generator: MySQL does not have MariaDB's <c>seq_*</c> tables).</summary>
    private const string Digits = "(SELECT 0 n UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9)";

    public async ValueTask InitializeAsync()
    {
        if (TestServer.ConnectionString is null)
        {
            return;
        }
        var password = new MySqlConnectionStringBuilder(TestServer.ConnectionString).Password;
        _host = new SessionHost(new SessionConnectionSettings("it", TestServer.Options()) { KeepAlive = null }, new SessionServices
        {
            Provider = new MySqlProvider(),
            ResolveSecret = (_, _) => ValueTask.FromResult(password),
        });
        await _host.ConnectAsync(Ct);
        _database = "eunomia_tb_" + Guid.NewGuid().ToString("N")[..8];
        await _host.RunPrimaryAsync(async (h, ct) =>
        {
            await h.ExecuteNonQueryAsync($"CREATE DATABASE `{_database}`", ct);
            await h.ExecuteNonQueryAsync($"CREATE TABLE `{_database}`.t (id INT PRIMARY KEY, name VARCHAR(10), body TEXT, data LONGBLOB) ENGINE=InnoDB", ct);
            await h.ExecuteNonQueryAsync(
                $"INSERT INTO `{_database}`.t SELECT seq, CONCAT('n', seq), IF(seq % 10 = 0, REPEAT('a', 300), IF(seq % 7 = 0, NULL, 'curto')), " +
                $"IF(seq = 10, REPEAT(0x41, 400), NULL) FROM (SELECT a.n + (b.n * 10) + (c.n * 100) + (d.n * 1000) + 1 AS seq FROM {Digits} a, {Digits} b, {Digits} c, {Digits} d) s WHERE seq <= 2500", ct);
            await h.ExecuteNonQueryAsync($"CREATE TABLE `{_database}`.nokey (a INT, b TEXT)", ct);
            await h.ExecuteNonQueryAsync($"INSERT INTO `{_database}`.nokey VALUES (1, REPEAT('z', 300))", ct);
            return true;
        }, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.RunPrimaryAsync((h, ct) => h.ExecuteNonQueryAsync($"DROP DATABASE IF EXISTS `{_database}`", ct), CancellationToken.None);
            await _host.DisposeAsync();
        }
    }

    private Task<TableDefinition> Table(string name) =>
        _host.RunMetadataAsync((h, ct) => h.Metadata.GetTableAsync(new DbObjectRef(_database, _database, name, DbObjectKind.Table), ct), Ct);

    [Fact]
    public async Task PagesWithNextAndShowAllUpToTheMaximum()
    {
        TestServer.RequireServer();
        using var browser = new TableBrowser(_host, await Table("t"), new DataBrowseOptions { MaximumRows = 2200 });

        await browser.LoadAsync(1000, Ct);
        Assert.Equal(1000, browser.Loaded);
        Assert.True(browser.CanLoadMore);
        Assert.StartsWith("SELECT `id`, `name`, LEFT(`body`, 256), LEFT(`data`, 256) FROM", browser.Select.Sql, StringComparison.Ordinal);
        var source = browser.Source!;
        Assert.Equal(["id", "name", "body", "data"], source.Schema.Columns.Select(c => c.Name));
        Assert.True(source.Schema[0].IsPrimaryKey);
        Assert.Equal(RowCountKind.Exact, source.RowCount.Kind);

        await browser.NextAsync(Ct);
        Assert.Equal(2000, browser.Loaded);
        Assert.EndsWith("LIMIT 1000, 1000", browser.Select.Sql, StringComparison.Ordinal);
        Assert.True(source.TryGetBlock(1, out var second));
        Assert.Equal(1000, second.FirstRow);
        Assert.Equal(1001, second.GetInt64(0, 0));

        await browser.ShowAllAsync(Ct);
        Assert.Equal(2200, browser.Loaded);
        Assert.True(browser.ReachedMaximum);
        Assert.False(browser.CanLoadMore);
        Assert.Equal(RowCountKind.Final, source.RowCount.Kind);
    }

    [Fact]
    public async Task CutCellsFlaggedAndFullValueByKey()
    {
        TestServer.RequireServer();
        using var browser = new TableBrowser(_host, await Table("t"), new DataBrowseOptions());
        await browser.LoadAsync(1000, Ct);
        var block = browser.Source!.TryGetBlock(0, out var b) ? b : throw new InvalidOperationException();

        // Row with id 10: body with 300 characters (cut at 256) and data with 400 bytes.
        Assert.True(block.IsTruncated(9, 2));
        Assert.EndsWith(" [...]", new DisplayFormatter(null).Format(block, 9, 2), StringComparison.Ordinal);
        Assert.False(block.IsTruncated(0, 2));
        Assert.Equal(new string('a', 300), await browser.GetFullTextAsync(9, 2, Ct));
        Assert.Equal("0x" + string.Concat(Enumerable.Repeat("41", 400)), await browser.GetFullTextAsync(9, 3, Ct));
        Assert.Equal("curto", await browser.GetFullTextAsync(0, 2, Ct));
        Assert.Null(await browser.GetFullTextAsync(6, 2, Ct));
    }

    [Fact]
    public async Task IntegersWithoutThousandsSeparator()
    {
        TestServer.RequireServer();
        using var browser = new TableBrowser(_host, await Table("t"), new DataBrowseOptions());
        await browser.LoadAsync(2000, Ct);

        Assert.Equal("1234", await browser.GetFullTextAsync(1233, 0, Ct));
    }

    [Fact]
    public async Task FilterSortingAndCounts()
    {
        TestServer.RequireServer();
        var options = new DataBrowseOptions { Filter = "WHERE id <= 30", Sort = [new DataSort("id", true)], RowsPerStep = 10 };
        using var browser = new TableBrowser(_host, await Table("t"), options);
        await browser.LoadAsync(10, Ct);

        Assert.Contains("WHERE (id <= 30\n) ORDER BY `id` DESC LIMIT 10", browser.Select.Sql, StringComparison.Ordinal);
        Assert.Equal(30, browser.Source!.TryGetBlock(0, out var block) ? block.GetInt64(0, 0) : 0);
        var (exact, isExact) = await browser.CountAsync(exact: true, Ct);
        Assert.Equal(30, exact);
        Assert.True(isExact);
        var (approximate, isApproximateExact) = await browser.CountAsync(exact: false, Ct);
        Assert.NotNull(approximate);
        Assert.False(isApproximateExact);
    }

    [Theory]
    [InlineData("id <= 30 -- the first ones")]
    [InlineData("id <= 30 # the first ones")]
    public async Task ACommentAtTheEndOfTheFilterKeepsOrderLimitAndGroupBy(string filter)
    {
        TestServer.RequireServer();
        var options = new DataBrowseOptions { Filter = filter, Sort = [new DataSort("id", true)], RowsPerStep = 10 };
        using var browser = new TableBrowser(_host, await Table("t"), options);
        await browser.LoadAsync(10, Ct);

        // ORDER BY id DESC LIMIT 10 still applies: 10 rows, the first is 30.
        Assert.Equal(10, browser.Loaded);
        Assert.Equal(30, browser.Source!.TryGetBlock(0, out var block) ? block.GetInt64(0, 0) : 0);
        Assert.Equal(30, (await browser.CountAsync(exact: true, Ct)).Rows);
        // GROUP BY … LIMIT 50 of "More values" still applies: 30 values, one row each.
        var values = await browser.TopValuesAsync("id", Ct);
        Assert.Equal(30, values.Count);
        Assert.All(values, v => Assert.Equal(1, v.Count));
    }

    [Fact]
    public async Task TableWithoutKeyComesComplete()
    {
        TestServer.RequireServer();
        using var browser = new TableBrowser(_host, await Table("nokey"), new DataBrowseOptions());
        await browser.LoadAsync(1000, Ct);

        Assert.StartsWith($"SELECT * FROM `{_database}`.`nokey`", browser.Select.Sql, StringComparison.Ordinal);
        Assert.Equal(new string('z', 300), await browser.GetFullTextAsync(0, 1, Ct));
        Assert.False(browser.CanLoadMore);
    }

    [Fact]
    public async Task CancelledShowAllKeepsTheRowsAndNextContinues()
    {
        TestServer.RequireServer();
        // Rows 11 and 12 are slow: Show all is cancelled halfway through them (Esc or another choice in the tree).
        var options = new DataBrowseOptions { RowsPerStep = 5, Filter = "SLEEP(IF(id IN (11, 12), 1, 0)) = 0" };
        using var browser = new TableBrowser(_host, await Table("t"), options);
        await browser.LoadAsync(5, Ct);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => browser.ShowAllAsync(cancel.Token));

        // Only whole blocks stay (the one in progress is left out) and Next and Show all continue from there.
        var kept = browser.Loaded;
        Assert.InRange(kept, 5, 10);
        Assert.Equal(0, kept % 5);
        Assert.Equal(kept, browser.Wanted);
        Assert.True(browser.CanLoadMore);
        await browser.NextAsync(Ct);
        Assert.Equal(kept + 5, browser.Loaded);
        for (var row = 0; row < browser.Loaded; row++)
        {
            Assert.True(browser.Source!.TryGetRow(row, out var block, out var r));
            Assert.Equal(row + 1, block.GetInt64(r, 0));
        }
    }

    [Fact]
    public async Task WithSpillTheDataStaysReachable()
    {
        TestServer.RequireServer();
        var spill = Path.Combine(Path.GetTempPath(), "eunomia-it-spill-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var options = new DataBrowseOptions { RowsPerStep = 100, Storage = new ResultStorage(BudgetBytes: 20_000, SpillDirectory: spill) };
            using var browser = new TableBrowser(_host, await Table("t"), options);
            await browser.LoadAsync(100, Ct);
            await browser.ShowAllAsync(Ct);

            Assert.Equal(2500, browser.Loaded);
            Assert.True(browser.Source!.SpilledBytes > 0);
            Assert.Equal("n5", await browser.GetFullTextAsync(4, 1, Ct));
            Assert.Equal(new string('a', 300), await browser.GetFullTextAsync(9, 2, Ct));
        }
        finally
        {
            if (Directory.Exists(spill))
            {
                Directory.Delete(spill, recursive: true);
            }
        }
    }

    /// <summary>
    /// Table for the filter panel search: names <c>r1</c>..<c>r2500</c> in a <c>_bin</c> column and, past the first page,
    /// 2400 <c>Needle</c>, 2401 <c>a;b</c>, 2402 <c>50%</c>, 2403 <c>500</c>, 2404 invalid UTF-8 in the BLOB,
    /// 2405 <c>blobneedle</c> in the BLOB, 2406 JSON with <c>jsonval</c> and 2407 a date in 2031.
    /// </summary>
    private async Task<TableDefinition> SearchTable()
    {
        await _host.RunPrimaryAsync(async (h, ct) =>
        {
            await h.ExecuteNonQueryAsync(
                $"CREATE TABLE `{_database}`.s (id INT PRIMARY KEY, name VARCHAR(20) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin, data BLOB, extra JSON, at DATETIME) ENGINE=InnoDB", ct);
            await h.ExecuteNonQueryAsync(
                $"INSERT INTO `{_database}`.s (id, name) SELECT seq, CONCAT('r', seq) " +
                $"FROM (SELECT a.n + (b.n * 10) + (c.n * 100) + (d.n * 1000) + 1 AS seq FROM {Digits} a, {Digits} b, {Digits} c, {Digits} d) q WHERE seq <= 2500", ct);
            await h.ExecuteNonQueryAsync(
                $"UPDATE `{_database}`.s SET name = CASE id WHEN 2400 THEN 'Needle' WHEN 2401 THEN 'a;b' WHEN 2402 THEN '50%' WHEN 2403 THEN '500' ELSE name END, " +
                "data = CASE id WHEN 2404 THEN 0x00FF41C3 WHEN 2405 THEN 0x626C6F626E6565646C65 END, " +
                "extra = IF(id = 2406, '{\"k\": \"jsonval\"}', NULL), at = IF(id = 2407, '2031-05-06 07:08:09', NULL) WHERE id >= 2400", ct);
            return true;
        }, Ct);
        return await Table("s");
    }

    /// <summary>Ids of the rows loaded with the filter panel search <paramref name="text"/> (first page of 1,000, by id), with commas.</summary>
    private async Task<string> SearchIds(TableDefinition table, string text, bool regex = false, string? filter = null)
    {
        var options = new DataBrowseOptions { Search = SearchTerms.Parse(text, regex), Filter = filter, Sort = [new DataSort("id", false)] };
        using var browser = new TableBrowser(_host, table, options);
        await browser.LoadAsync(1000, Ct);
        var ids = new long[browser.Loaded];
        for (var row = 0; row < ids.Length; row++)
        {
            Assert.True(browser.Source!.TryGetRow(row, out var block, out var r));
            ids[row] = block.GetInt64(r, 0);
        }
        return string.Join(",", ids);
    }

    [Fact]
    public async Task FilterPanelSearchFindsRowsPastTheFirstPage()
    {
        TestServer.RequireServer();
        var table = await SearchTable();

        // Without case (also in the _bin column and in the BLOB), past row 1,000.
        Assert.Equal("2400,2405", await SearchIds(table, "NEEDLE"));
        // ';' separates terms (any of them); '\;' is a ';'.
        Assert.Equal("2400,2405,2406", await SearchIds(table, "needle ; jsonval"));
        Assert.Equal("2401", await SearchIds(table, @"a\;b"));
        // % and _ are not wildcards: 50% does not find 500 (nor the ids 50, 150…).
        Assert.Equal("2402", await SearchIds(table, "50%"));
        Assert.Equal("2407", await SearchIds(table, "2031-05"));
        // Together with the WHERE filter.
        Assert.Equal("2400", await SearchIds(table, "needle;jsonval", filter: "id < 2405"));
    }

    [Fact]
    public async Task FilterPanelRegexSearchOnTextBlobAndDates()
    {
        TestServer.RequireServer();
        var table = await SearchTable();

        // REGEXP without case on CONVERT(… USING utf8mb4): no error 3995 on MySQL 8's BLOB, invalid UTF-8 included (2404);
        // the backslash goes as _utf8mb4 X'…'.
        Assert.Equal("2400,2405,2407", await SearchIds(table, @"^needle$;blob.eedle;\d{4}-05-06", regex: true));
        Assert.Equal("2401", await SearchIds(table, @"^a\;b$", regex: true));
    }

    [Fact]
    public async Task NextAndCountWithTheFilterPanelSearch()
    {
        TestServer.RequireServer();
        var table = await SearchTable();
        // r1, r10..r19, r100..r199 and r1000..r1999: 1,111 rows.
        var options = new DataBrowseOptions { Search = SearchTerms.Parse("R1", false), RowsPerStep = 1000 };
        using var browser = new TableBrowser(_host, table, options);

        await browser.LoadAsync(1000, Ct);
        Assert.Equal(1000, browser.Loaded);
        Assert.True(browser.CanLoadMore);
        Assert.True(browser.IsFiltered);
        await browser.NextAsync(Ct);
        Assert.Equal(1111, browser.Loaded);
        Assert.False(browser.CanLoadMore);
        Assert.Equal((1111L, true), await browser.CountAsync(exact: true, Ct));
    }
}
