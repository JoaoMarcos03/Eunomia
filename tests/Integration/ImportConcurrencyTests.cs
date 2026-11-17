using System.Diagnostics;
using System.Globalization;
using System.Text;
using Eunomia.App.Data;
using Eunomia.App.Transfer;
using Eunomia.Drivers.Abstractions;
using Eunomia.Drivers.MySql;
using Eunomia.Engine.Execution;
using Eunomia.Engine.Metadata;
using MySqlConnector;

namespace Eunomia.Integration.Tests;

/// <summary>
/// Run SQL file in progress (long transaction, DDL) and, at the same time, the tree, the table structure and the Data tab
/// of other databases and of the one being imported: none of them waits for the import.
/// </summary>
public sealed class ImportConcurrencyTests : IAsyncLifetime
{
    private SessionHost _host = null!;
    private string _prefix = string.Empty;
    private string _directory = string.Empty;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Imported => _prefix + "_imp";

    private string Other => _prefix + "_other";

    public async ValueTask InitializeAsync()
    {
        if (TestServer.ConnectionString is null)
        {
            return;
        }
        var password = new MySqlConnectionStringBuilder(TestServer.ConnectionString).Password;
        // One job connection (the minimum in the session settings): the import holds it the whole time.
        _host = new SessionHost(new SessionConnectionSettings("it", TestServer.Options()) { KeepAlive = null, MaxJobConnections = 1 }, new SessionServices
        {
            Provider = new MySqlProvider(),
            ResolveSecret = (_, _) => ValueTask.FromResult(password),
        });
        await _host.ConnectAsync(Ct);
        _prefix = "eunomia_ic_" + Guid.NewGuid().ToString("N")[..8];
        _directory = Path.Combine(Path.GetTempPath(), "eunomia-it", "import-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_directory);
        await ExecAsync($"CREATE DATABASE `{Other}`");
        await ExecAsync($"CREATE TABLE `{Other}`.t (id INT PRIMARY KEY, v VARCHAR(20))");
        await ExecAsync($"INSERT INTO `{Other}`.t VALUES (1, 'a'), (2, 'b'), (3, 'c')");
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await ExecAsync($"DROP DATABASE IF EXISTS `{Other}`");
            await ExecAsync($"DROP DATABASE IF EXISTS `{Imported}`");
            await _host.DisposeAsync();
        }
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private Task ExecAsync(string sql) => _host.RunPrimaryAsync((h, ct) => h.ExecuteNonQueryAsync(sql, ct), CancellationToken.None);

    private async Task<long> CountAsync(string sql) =>
        Convert.ToInt64(await _host.RunPrimaryAsync((h, ct) => h.ExecuteScalarAsync(sql, ct), Ct), CultureInfo.InvariantCulture);

    [Fact]
    public async Task BrowsingDoesNotWaitForTheImport()
    {
        TestServer.RequireServer();
        // Like an Eunomia dump: its own database, the rows of a table in one transaction, then DDL on that table.
        var script = new StringBuilder();
        script.Append(CultureInfo.InvariantCulture, $"CREATE DATABASE `{Imported}`;\nUSE `{Imported}`;\n");
        script.Append("CREATE TABLE big (id INT PRIMARY KEY, v VARCHAR(20)) ENGINE=InnoDB;\nSTART TRANSACTION;\n");
        for (var i = 1; i <= 500; i++)
        {
            script.Append(CultureInfo.InvariantCulture, $"INSERT INTO big VALUES ({i}, 'row {i}');\n");
        }
        script.Append("SELECT SLEEP(8) AS eunomia_marker_1;\nCOMMIT;\nALTER TABLE big ADD INDEX iv (v);\nSELECT SLEEP(1);\n");
        var path = Path.Combine(_directory, "dump.sql");
        await File.WriteAllTextAsync(path, script.ToString(), Ct);
        var job = new SqlFileJob(_host, new SqlFileRequest { Path = path, Database = Other, Session = "it" });

        var import = Task.Run(() => job.RunAsync(null, Ct), Ct);
        await WaitForAsync("SELECT COUNT(*) FROM information_schema.PROCESSLIST WHERE INFO LIKE 'SELECT SLEEP(8) AS eunomia_marker_1%'");

        var clock = Stopwatch.StartNew();
        var cache = new MetadataCache(_host, new MetadataOptions { FullTableStatus = true });
        Assert.Contains(Other, await cache.GetDatabasesAsync(Ct));
        Assert.Contains((await cache.GetObjectsAsync(Other, Ct)).Objects, o => o.Ref.Name == "t");
        using (var browser = new TableBrowser(_host, await cache.GetTableAsync(new DbObjectRef(Other, Other, "t", DbObjectKind.Table), Ct), new DataBrowseOptions()))
        {
            await browser.LoadAsync(100, Ct);
            Assert.Equal(3, browser.Loaded);
        }
        // The database being imported too: its table shows (empty, the transaction is not committed yet).
        Assert.Contains((await cache.GetObjectsAsync(Imported, Ct)).Objects, o => o.Ref.Name == "big");
        using (var browser = new TableBrowser(_host, await cache.GetTableAsync(new DbObjectRef(Imported, Imported, "big", DbObjectKind.Table), Ct), new DataBrowseOptions()))
        {
            await browser.LoadAsync(100, Ct);
            Assert.Equal(0, browser.Loaded);
        }
        clock.Stop();

        Assert.False(import.IsCompleted, "The import ended before the browsing: the test did not overlap them.");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4), $"Browsing took {clock.Elapsed.TotalSeconds:0.0} s during the import.");

        var result = await import;
        Assert.True(result.Completed, string.Join("; ", result.ErrorList.Select(e => e.Message)));
        Assert.Equal(500, await CountAsync($"SELECT COUNT(*) FROM `{Imported}`.big"));
        Assert.Equal(0, await CountAsync($"SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = '{Other}' AND TABLE_NAME = 'big'"));
    }

    private async Task WaitForAsync(string countSql)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (await CountAsync(countSql) == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "The import did not reach the marker.");
            await Task.Delay(50, Ct);
        }
    }
}
