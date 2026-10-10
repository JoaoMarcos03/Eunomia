using Eunomia.App.Data;
using Eunomia.Drivers.Abstractions;
using Eunomia.Drivers.MySql;
using Eunomia.Engine.Execution;
using Eunomia.Engine.Results;
using MySqlConnector;

namespace Eunomia.Integration.Tests;

/// <summary>Data tab contra o servidor (Fase 5; anexo D B2–B6; FAM-G02, G03, C01; PR-17).</summary>
public sealed class TableBrowserTests : IAsyncLifetime
{
    private SessionHost _host = null!;
    private string _database = string.Empty;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>0..9 (gerador portátil: o MySQL não tem as tabelas <c>seq_*</c> do MariaDB).</summary>
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
    public async Task PaginaComNextShowAllAteAoMaximo()
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
    public async Task CelulasCortadasMarcadasEValorCompletoPorChave()
    {
        TestServer.RequireServer();
        using var browser = new TableBrowser(_host, await Table("t"), new DataBrowseOptions());
        await browser.LoadAsync(1000, Ct);
        var block = browser.Source!.TryGetBlock(0, out var b) ? b : throw new InvalidOperationException();

        // Linha com id 10: body com 300 caracteres (cortado a 256) e data com 400 bytes.
        Assert.True(block.IsTruncated(9, 2));
        Assert.EndsWith(" [...]", new DisplayFormatter(null).Format(block, 9, 2), StringComparison.Ordinal);
        Assert.False(block.IsTruncated(0, 2));
        Assert.Equal(new string('a', 300), await browser.GetFullTextAsync(9, 2, Ct));
        Assert.Equal("0x" + string.Concat(Enumerable.Repeat("41", 400)), await browser.GetFullTextAsync(9, 3, Ct));
        Assert.Equal("curto", await browser.GetFullTextAsync(0, 2, Ct));
        Assert.Null(await browser.GetFullTextAsync(6, 2, Ct));
    }

    [Fact]
    public async Task InteirosSemSeparadorDeMilhares()
    {
        TestServer.RequireServer();
        using var browser = new TableBrowser(_host, await Table("t"), new DataBrowseOptions());
        await browser.LoadAsync(2000, Ct);

        Assert.Equal("1234", await browser.GetFullTextAsync(1233, 0, Ct));
    }

    [Fact]
    public async Task FiltroOrdenacaoEContagens()
    {
        TestServer.RequireServer();
        var options = new DataBrowseOptions { Filter = "WHERE id <= 30", Sort = [new DataSort("id", true)], RowsPerStep = 10 };
        using var browser = new TableBrowser(_host, await Table("t"), options);
        await browser.LoadAsync(10, Ct);

        Assert.Contains("WHERE id <= 30 ORDER BY `id` DESC LIMIT 10", browser.Select.Sql, StringComparison.Ordinal);
        Assert.Equal(30, browser.Source!.TryGetBlock(0, out var block) ? block.GetInt64(0, 0) : 0);
        var (exact, isExact) = await browser.CountAsync(exact: true, Ct);
        Assert.Equal(30, exact);
        Assert.True(isExact);
        var (approximate, isApproximateExact) = await browser.CountAsync(exact: false, Ct);
        Assert.NotNull(approximate);
        Assert.False(isApproximateExact);
    }

    [Fact]
    public async Task TabelaSemChaveVemCompleta()
    {
        TestServer.RequireServer();
        using var browser = new TableBrowser(_host, await Table("nokey"), new DataBrowseOptions());
        await browser.LoadAsync(1000, Ct);

        Assert.StartsWith($"SELECT * FROM `{_database}`.`nokey`", browser.Select.Sql, StringComparison.Ordinal);
        Assert.Equal(new string('z', 300), await browser.GetFullTextAsync(0, 1, Ct));
        Assert.False(browser.CanLoadMore);
    }

    [Fact]
    public async Task ShowAllCanceladoGuardaAsLinhasENextContinua()
    {
        TestServer.RequireServer();
        // As linhas 11 e 12 demoram: o Show all é cancelado a meio delas (Esc ou outra escolha na árvore).
        var options = new DataBrowseOptions { RowsPerStep = 5, Filter = "SLEEP(IF(id IN (11, 12), 1, 0)) = 0" };
        using var browser = new TableBrowser(_host, await Table("t"), options);
        await browser.LoadAsync(5, Ct);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => browser.ShowAllAsync(cancel.Token));

        // Ficam só blocos inteiros (o que estava a meio não entra) e Next e Show all continuam a partir daí.
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
    public async Task ComSpillOsDadosContinuamAcessiveis()
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
}
