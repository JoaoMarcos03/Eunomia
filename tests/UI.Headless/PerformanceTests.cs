using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Eunomia.UI.Controls.Tree;
using MySqlConnector;
using static Eunomia.UI.Headless.Tests.RealServer;

namespace Eunomia.UI.Headless.Tests;

/// <summary>
/// Measurements against the test server (section 10). They only run with <c>EUNOMIA_PERF=1</c>: they create once the
/// <c>eunomia_pr08</c> database with 5,000 InnoDB tables (it stays in the test datadir for later runs). Indicative
/// values: this machine is not the reference one.
/// </summary>
public sealed class PerformanceTests
{
    private const string Database = "eunomia_pr08";
    private const int Tables = 5000;

    private static async Task EnsureLargeSchemaAsync(string cs)
    {
        await using var connection = new MySqlConnection(cs);
        await connection.OpenAsync();
        await using (var create = new MySqlCommand($"CREATE DATABASE IF NOT EXISTS `{Database}`", connection))
        {
            await create.ExecuteNonQueryAsync();
        }
        await using var count = new MySqlCommand($"SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = '{Database}'", connection);
        var existing = Convert.ToInt32(await count.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        for (var start = existing; start < Tables; start += 250)
        {
            var sql = new StringBuilder();
            for (var i = start; i < Math.Min(Tables, start + 250); i++)
            {
                sql.Append(CultureInfo.InvariantCulture, $"CREATE TABLE IF NOT EXISTS `{Database}`.`t{i:D5}` (id INT PRIMARY KEY, name VARCHAR(50), created DATETIME) ENGINE=InnoDB;");
            }
            await using var batch = new MySqlCommand(sql.ToString(), connection) { CommandTimeout = 600 };
            await batch.ExecuteNonQueryAsync();
        }
    }

    /// <summary>PR-09: first 1,000 rows painted ≤ server time + 100 ms.</summary>
    [AvaloniaFact(Timeout = UiTimeouts.Measurement)]
    public async Task Pr09OpenTableInTheDataTab()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null || Environment.GetEnvironmentVariable("EUNOMIA_PERF") != "1", "Measurement: needs EUNOMIA_PERF=1 and the test server.");
        const string db = "eunomia_pr09";
        const string digits = "(SELECT 0 n UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9)";
        await ExecAsync(cs, $"CREATE DATABASE IF NOT EXISTS `{db}`; CREATE TABLE IF NOT EXISTS `{db}`.t (id INT PRIMARY KEY, a VARCHAR(40), b INT, c DECIMAL(12,2), d DATETIME, e TEXT, f DOUBLE, g VARCHAR(10), h DATE, i BIGINT) ENGINE=InnoDB; " +
            $"INSERT IGNORE INTO `{db}`.t SELECT seq, CONCAT('name ', seq), seq * 7, seq / 3, '2026-01-01' + INTERVAL seq MINUTE, REPEAT('x', seq % 300), seq / 7, 'abc', '2026-01-01' + INTERVAL (seq % 365) DAY, seq * 1000 " +
            $"FROM (SELECT a.n + (b.n * 10) + (c.n * 100) + (d.n * 1000) + 1 AS seq FROM {digits} a, {digits} b, {digits} c, {digits} d) s WHERE seq <= 5000;");

        var (window, context, session, connector, _) = CreateShell(cs, db);
        Assert.True(await window.ConnectAsync(window, session, null));
        var model = window.Tree.Tree.Model!;
        var root = await WaitAsync(() => model.Roots.FirstOrDefault());
        var dbNode = await WaitAsync(() => root.Children?.FirstOrDefault());
        await model.ExpandAsync(dbNode);
        window.ShowDataTab();
        window.Tree.Tree.FocusedNode = dbNode.Children!.Single(n => n.Name == "t");
        await WaitAsync(() => window.DataGrid.RowCount == 1000 ? "ok" : null);
        var open = connector.Open[0];

        var ui = new List<double>();
        var server = new List<double>();
        for (var run = 0; run < 6; run++)
        {
            // Server: the same SELECT read to the end through the primary connection (server + driver).
            var sql = window.DataBrowser!.Select.Sql;
            var sw = Stopwatch.StartNew();
            await open.Host.RunPrimaryAsync(async (h, ct) =>
            {
                await foreach (var _ in h.Executor.ExecuteAsync(Eunomia.Drivers.Abstractions.SqlBatch.Single(sql), new Eunomia.Drivers.Abstractions.ExecuteOptions(1000) { CollectWarnings = false }, ct))
                {
                }
                return true;
            }, CancellationToken.None);
            server.Add(sw.Elapsed.TotalMilliseconds);

            // Grid: F5 in the Data tab until the 1,000 rows are in the grid and painted.
            window.DataGrid.Focus();
            var previous = window.DataBrowser;
            sw.Restart();
            await context.Commands.ExecuteAsync("view.refresh");
            await WaitAsync(() => window.DataBrowser is { Loaded: 1000 } b && !ReferenceEquals(b, previous) && window.DataGrid.RowCount == 1000 ? "ok" : null, TimeSpan.FromSeconds(30));
            window.CaptureRenderedFrame();
            ui.Add(sw.Elapsed.TotalMilliseconds);
        }

        var medianUi = ui.Skip(1).Order().ElementAt(2);
        var medianServer = server.Skip(1).Order().ElementAt(2);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "eunomia-pr09.txt"),
            string.Create(CultureInfo.InvariantCulture, $"grid_ms={medianUi:F1}\nserver_ms={medianServer:F1}\nruns_grid={string.Join(";", ui.Select(u => u.ToString("F1", CultureInfo.InvariantCulture)))}\nruns_server={string.Join(";", server.Select(u => u.ToString("F1", CultureInfo.InvariantCulture)))}\n"));
        Assert.True(medianUi <= medianServer + 100, $"Grid {medianUi:F0} ms > server {medianServer:F0} ms + 100 ms");

        await connector.DisconnectAllAsync();
        window.Close();
    }

    /// <summary>
    /// Opening a table in the Data tab right after opening the 5,000-table database: the database's statistics (50 s of
    /// <c>SHOW TABLE STATUS</c> on MariaDB 10.4) run on a job connection and do not delay the table's definition, which goes
    /// through the metadata connection.
    /// </summary>
    [AvaloniaFact(Timeout = UiTimeouts.Measurement)]
    public async Task TableRightAfterOpeningALargeDatabase()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null || Environment.GetEnvironmentVariable("EUNOMIA_PERF") != "1", "Measurement: needs EUNOMIA_PERF=1 and the test server.");
        await EnsureLargeSchemaAsync(cs);

        var (window, _, session, connector, _) = CreateShell(cs, Database);
        Assert.True(await window.ConnectAsync(window, session, null));
        var model = window.Tree.Tree.Model!;
        var root = await WaitAsync(() => model.Roots.FirstOrDefault());
        var dbNode = await WaitAsync(() => root.Children?.FirstOrDefault());
        var host = connector.Open[0].Host;
        await model.ExpandAsync(dbNode);
        window.ShowDataTab();
        var sw = Stopwatch.StartNew();
        window.Tree.Tree.FocusedNode = dbNode.Children!.Single(n => n.Name == "t00042");
        await WaitAsync(() => window.DataBrowser?.Table.Ref.Name == "t00042" && window.DataBrowser.Source is not null && !host.IsPrimaryBusy ? "ok" : null, TimeSpan.FromMinutes(2));
        var elapsed = sw.Elapsed;
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "eunomia-table-after-big-db.txt"), string.Create(CultureInfo.InvariantCulture, $"table_ms={elapsed.TotalMilliseconds:F0}\n"));
        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"The table took {elapsed.TotalMilliseconds:F0} ms.");

        await connector.DisconnectAllAsync();
        window.Close();
    }

    /// <summary>PR-08: names visible ≤ server time + 300 ms; statistics in the background.</summary>
    [AvaloniaFact(Timeout = UiTimeouts.Measurement)]
    public async Task Pr08OpenDatabaseWith5000Tables()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null || Environment.GetEnvironmentVariable("EUNOMIA_PERF") != "1", "Measurement: needs EUNOMIA_PERF=1 and the test server.");
        await EnsureLargeSchemaAsync(cs);

        var (window, _, session, connector, _) = CreateShell(cs, Database);
        Assert.True(await window.ConnectAsync(window, session, null));
        var model = window.Tree.Tree.Model!;
        var root = await WaitAsync(() => model.Roots.FirstOrDefault());
        await WaitAsync(() => root.Children?.FirstOrDefault());
        var open = connector.Open[0];

        var ui = new List<double>();
        var server = new List<double>();
        for (var run = 0; run < 5; run++)
        {
            // Server: the same names read, directly on the metadata connection (server + driver).
            var sw = Stopwatch.StartNew();
            var direct = await open.Host.RunMetadataAsync((h, ct) => h.Metadata.GetObjectsAsync(Database, Database, Eunomia.Drivers.Abstractions.ObjectDetail.Names, ct), CancellationToken.None);
            server.Add(sw.Elapsed.TotalMilliseconds);
            Assert.Equal(Tables, direct.Count);

            // Tree: refresh of the closed database and expand until the 5,000 names are in the visible list.
            open.Metadata.Invalidate(Database);
            var dbNode = root.Children!.First(n => n.Name == Database);
            model.Collapse(dbNode);
            await model.ReloadAsync(dbNode);
            sw.Restart();
            await model.ExpandAsync(dbNode);
            ui.Add(sw.Elapsed.TotalMilliseconds);
            Assert.Equal(Tables, dbNode.Children!.Count);
            Assert.Contains(dbNode.Children[0], model.Visible);
            // The statistics arrive later, in the background.
            await open.Metadata.WaitForStatisticsAsync(Database);
        }

        var medianUi = ui.Skip(1).Order().ElementAt(2);
        var medianServer = server.Skip(1).Order().ElementAt(2);
        TestContext.Current.SendDiagnosticMessage($"PR-08: tree {medianUi:F0} ms, server {medianServer:F0} ms (runs: {string.Join(", ", ui.Select(u => u.ToString("F0", CultureInfo.InvariantCulture)))})");
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "eunomia-pr08.txt"),
            string.Create(CultureInfo.InvariantCulture, $"tree_ms={medianUi:F1}\nserver_ms={medianServer:F1}\nruns_tree={string.Join(";", ui.Select(u => u.ToString("F1", CultureInfo.InvariantCulture)))}\nruns_server={string.Join(";", server.Select(u => u.ToString("F1", CultureInfo.InvariantCulture)))}\n"));
        Assert.True(medianUi <= medianServer + 300, $"Tree {medianUi:F0} ms > server {medianServer:F0} ms + 300 ms");

        await connector.DisconnectAllAsync();
        window.Close();
    }
}
