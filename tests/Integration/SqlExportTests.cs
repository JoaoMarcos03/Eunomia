using System.IO.Compression;
using System.Text;
using Eunomia.App.Transfer;
using Eunomia.Drivers.Abstractions;
using Eunomia.Drivers.MySql;
using Eunomia.Engine.Execution;
using Eunomia.Engine.Results;
using Eunomia.Engine.Transfer;
using MySqlConnector;

namespace Eunomia.Integration.Tests;

/// <summary>SQL export against the server (phase 8; FAM-E06; section 13.2): the dump re-imports and gives the same data.</summary>
public sealed class SqlExportTests : IAsyncLifetime
{
    private const int BigRows = 25_000;

    private SessionHost _host = null!;
    private string _database = string.Empty;
    private readonly List<string> _cleanup = [];
    private string _directory = string.Empty;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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
        _database = "eunomia_ex_" + Guid.NewGuid().ToString("N")[..8];
        _cleanup.Add(_database);
        _directory = Path.Combine(Path.GetTempPath(), "eunomia-it", "export-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_directory);
        var db = $"`{_database}`";
        await ExecAsync(
            $"CREATE DATABASE {db} CHARACTER SET utf8mb4",
            $"CREATE TABLE {db}.typed (id INT AUTO_INCREMENT PRIMARY KEY, i INT, d DECIMAL(12,3), f DOUBLE, fl FLOAT, b BIT(4), bin VARBINARY(8), " +
                "bl BLOB, dt DATETIME(3), ts TIMESTAMP NULL, tm TIME, t TEXT, e ENUM('a','b'), s SET('x','y'), u BIGINT UNSIGNED, pt POINT NULL, " +
                "gen INT AS (i * 2) VIRTUAL)",
            $"INSERT INTO {db}.typed (i, d, f, fl, b, bin, bl, dt, ts, tm, t, e, s, u, pt) VALUES " +
                "(-7, -1234.5, 0.1, 0.1, b'0101', 0xCAFE, '', '2026-10-06 12:34:56.789', '2026-10-06 12:00:00', '-01:02:03', " +
                "'it''s \\\\ \"ok\"\\nnova linha\\0fim ação', 'b', 'x,y', 18446744073709551615, ST_GeomFromText('POINT(1 2)')), " +
                "(NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL), " +
                "(0, 0, 1e300, -3.5, b'0', '', 0x00FF00, '1000-01-01 00:00:00', NULL, '838:59:59', '', 'a', '', 0, NULL)",
            $"CREATE TABLE {db}.big (id INT PRIMARY KEY, txt VARCHAR(100))",
            $"INSERT INTO {db}.big SELECT a.n * 1000 + b.n * 10 + c.n, REPEAT('z', (a.n + b.n + c.n) % 50) " +
                "FROM (WITH RECURSIVE s(n) AS (SELECT 0 UNION ALL SELECT n + 1 FROM s WHERE n < 24) SELECT n FROM s) a, " +
                "(WITH RECURSIVE s(n) AS (SELECT 0 UNION ALL SELECT n + 1 FROM s WHERE n < 99) SELECT n FROM s) b, " +
                "(WITH RECURSIVE s(n) AS (SELECT 0 UNION ALL SELECT n + 1 FROM s WHERE n < 9) SELECT n FROM s) c",
            $"CREATE TABLE {db}.nokey (a INT, b VARCHAR(10))",
            $"INSERT INTO {db}.nokey VALUES (1, 'x'), (1, 'x'), (2, NULL)");
        // Without the database name, as usual: SHOW CREATE TRIGGER returns the original text, with qualified names
        // if they were qualified (and the dump recreated the trigger in the source database, as in HeidiSQL and mysqldump).
        await using var handle = await TestServer.OpenAsync(_database);
        foreach (var sql in new[]
        {
            "CREATE VIEW v1 AS SELECT id, t FROM typed",
            "CREATE VIEW v0 AS SELECT id FROM v1 WHERE id > 1",
            "CREATE FUNCTION f1(x INT) RETURNS INT DETERMINISTIC RETURN x + 1",
            "CREATE PROCEDURE p1() BEGIN SELECT 1; SELECT 2; END",
            "CREATE TRIGGER trg BEFORE INSERT ON nokey FOR EACH ROW SET NEW.b = COALESCE(NEW.b, 'novo')",
        })
        {
            await handle.ExecuteNonQueryAsync(sql, Ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            foreach (var name in _cleanup)
            {
                await _host.RunPrimaryAsync((h, ct) => h.ExecuteNonQueryAsync($"DROP DATABASE IF EXISTS `{name}`", ct), CancellationToken.None);
            }
            await _host.DisposeAsync();
        }
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private Task ExecAsync(params string[] statements) => _host.RunPrimaryAsync(
        async (h, ct) =>
        {
            foreach (var sql in statements)
            {
                await h.ExecuteNonQueryAsync(sql, ct);
            }
            return true;
        },
        Ct);

    private Task<object?> ScalarAsync(string sql) => _host.RunPrimaryAsync((h, ct) => h.ExecuteScalarAsync(sql, ct), Ct);

    private async Task<SqlExportRequest> RequestAsync(SqlExportTarget target, string path, bool snapshot, DumpDataMode data = DumpDataMode.Insert)
    {
        var objects = await _host.RunMetadataAsync((h, ct) => h.Metadata.GetObjectsAsync(_database, _database, ObjectDetail.Statistics, ct), Ct);
        return new SqlExportRequest
        {
            Dump = new SqlDumpOptions { CreateTables = true, DropTables = true, Data = data, MaxInsertSizeKb = 64 },
            Databases = [new SqlExportDatabase(_database, [.. objects.OrderBy(o => o.Ref.Name, StringComparer.OrdinalIgnoreCase)])],
            Target = target,
            Path = path,
            ConsistentSnapshot = snapshot,
            Host = "127.0.0.1",
            AppVersion = "0.8.0",
        };
    }

    /// <summary>Runs the dump on a new database with Run SQL file and compares the data table by table.</summary>
    private async Task ReimportAndCompareAsync(string dump)
    {
        var copy = _database + "_c" + _cleanup.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _cleanup.Add(copy);
        await ExecAsync($"CREATE DATABASE `{copy}` CHARACTER SET utf8mb4");
        var file = Path.Combine(_directory, copy + ".sql");
        await File.WriteAllTextAsync(file, dump, new UTF8Encoding(false), Ct);
        var run = await new SqlFileJob(_host, new SqlFileRequest { Path = file, Database = copy }).RunAsync(null, Ct);
        Assert.True(run.Completed, string.Join("\n", run.ErrorList.Select(e => $"#{e.Statement} line {e.Line}: {e.Message}\n{e.Sql}")));
        Assert.Equal(0, run.Errors);
        foreach (var table in new[] { "typed", "big", "nokey" })
        {
            var source = await ChecksumAsync(_database, table);
            Assert.Equal(source, await ChecksumAsync(copy, table));
        }
        Assert.Equal(BigRows, Convert.ToInt64(await ScalarAsync($"SELECT COUNT(*) FROM `{copy}`.big"), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(2L, Convert.ToInt64(await ScalarAsync($"SELECT COUNT(*) FROM `{copy}`.v0"), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(8L, Convert.ToInt64(await ScalarAsync($"SELECT `{copy}`.f1(7)"), System.Globalization.CultureInfo.InvariantCulture));
        await ExecAsync($"INSERT INTO `{copy}`.nokey (a) VALUES (3)");
        Assert.Equal("novo", await ScalarAsync($"SELECT b FROM `{copy}`.nokey WHERE a = 3"));
    }

    private async Task<string> ChecksumAsync(string database, string table) =>
        await _host.RunPrimaryAsync(
            async (h, ct) =>
            {
                var events = await TestServer.RunAsync(h, $"CHECKSUM TABLE `{database}`.`{table}`");
                var block = events.OfType<RowBlockReceived>().Single().Block;
                return block.Schema[1].Storage == StorageKind.Int64
                    ? block.GetInt64(0, 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : block.GetString(0, 1);
            },
            Ct);

    [Fact]
    public async Task FicheiroComSnapshotReimportaIgual()
    {
        TestServer.RequireServer();
        var path = Path.Combine(_directory, "dump.sql");
        var progress = new List<SqlExportProgress>();
        var result = await new SqlExportJob(_host, await RequestAsync(SqlExportTarget.File, path, snapshot: true)).RunAsync(new SyncProgress<SqlExportProgress>(progress.Add), Ct);

        Assert.False(result.Cancelled);
        Assert.False(File.Exists(path + ".partial"));
        var dump = await File.ReadAllTextAsync(path, Ct);
        Assert.Equal(new FileInfo(path).Length, result.BytesWritten);
        Assert.StartsWith("-- --------------------------------------------------------\r\n-- Host:                         127.0.0.1\r\n", dump, StringComparison.Ordinal);
        Assert.Contains("-- Dumping data for table " + _database + ".big: ~", dump, StringComparison.Ordinal);
        Assert.Contains("_binary 0xCAFE", dump, StringComparison.Ordinal);
        Assert.DoesNotContain("`gen`", dump.Split("-- Dumping data for table " + _database + ".typed")[1].Split("\r\n\r\n")[0], StringComparison.Ordinal);
        // 25,000 rows with 64 KB per INSERT: several INSERTs; never more than 10,000 rows each.
        Assert.True(dump.Split("INSERT INTO `big`").Length - 1 >= 3);
        Assert.All(result.Items.Where(i => i.Kind == DbObjectKind.Table), i => Assert.Equal(SqlExportItemState.Done, i.State));
        Assert.Equal(BigRows, result.Items.Single(i => i.Name == "big").Rows);
        Assert.NotEmpty(progress);
        var order = new[] { "-- Dumping structure for table " + _database + ".big", "for function " + _database + ".f1", "for table " + _database + ".nokey", "for procedure " + _database + ".p1",
            "for table " + _database + ".typed", "for view " + _database + ".v0", "for view " + _database + ".v1", "for trigger " + _database + ".trg", "create final VIEW structure" };
        Assert.Equal(order.Select(o => dump.IndexOf(o, StringComparison.Ordinal)).Order(), order.Select(o => dump.IndexOf(o, StringComparison.Ordinal)));
        Assert.DoesNotContain(-1, order.Select(o => dump.IndexOf(o, StringComparison.Ordinal)));

        await ReimportAndCompareAsync(dump);
    }

    [Fact]
    public async Task ZipEmParaleloTemOMesmoTextoQueOFicheiroSequencial()
    {
        TestServer.RequireServer();
        var sequential = Path.Combine(_directory, "seq.sql");
        var zip = Path.Combine(_directory, "par.zip");
        // The same request (InnoDB row estimates may change between reads).
        var request = await RequestAsync(SqlExportTarget.File, sequential, snapshot: true);
        await new SqlExportJob(_host, request).RunAsync(null, Ct);
        var result = await new SqlExportJob(_host, request with { Target = SqlExportTarget.Zip, Path = zip, ConsistentSnapshot = false }).RunAsync(null, Ct);

        Assert.False(result.Cancelled);
        using var archive = ZipFile.OpenRead(zip);
        var entry = Assert.Single(archive.Entries);
        Assert.Equal("par.sql", entry.FullName);
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        var text = await reader.ReadToEndAsync(Ct);
        Assert.Equal(await File.ReadAllTextAsync(sequential, Ct), text);
    }

    [Fact]
    public async Task DiretorioClipboardECancel()
    {
        TestServer.RequireServer();
        var directory = Path.Combine(_directory, "dir");
        var result = await new SqlExportJob(_host, await RequestAsync(SqlExportTarget.Directory, directory, snapshot: false)).RunAsync(null, Ct);
        Assert.False(result.Cancelled);
        var files = Directory.GetFiles(Path.Combine(directory, _database)).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["function-f1.sql", "procedure-p1.sql", "table-big.sql", "table-nokey.sql", "table-typed.sql", "trigger-trg.sql", "view-v0.sql", "view-v1.sql"], files);
        var view = await File.ReadAllTextAsync(Path.Combine(directory, _database, "view-v0.sql"), Ct);
        Assert.Contains("Creating temporary table", view, StringComparison.Ordinal);
        Assert.Contains("create final VIEW structure", view, StringComparison.Ordinal);
        Assert.EndsWith("SET SQL_NOTES=IFNULL(@OLD_SQL_NOTES, 1) */;\r\n", view, StringComparison.Ordinal);

        var clipboard = await new SqlExportJob(_host, await RequestAsync(SqlExportTarget.Clipboard, string.Empty, snapshot: false, DumpDataMode.Replace)).RunAsync(null, Ct);
        Assert.Contains("REPLACE INTO `nokey` (`a`, `b`) VALUES", clipboard.ClipboardText, StringComparison.Ordinal);
        var limited = await RequestAsync(SqlExportTarget.Clipboard, string.Empty, snapshot: false);
        await Assert.ThrowsAsync<ClipboardLimitException>(() => new SqlExportJob(_host, limited with { ClipboardLimit = 10_000 }).RunAsync(null, Ct));

        var path = Path.Combine(_directory, "cancel.sql");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var cancelled = await new SqlExportJob(_host, await RequestAsync(SqlExportTarget.File, path, snapshot: true))
            .RunAsync(new SyncProgress<SqlExportProgress>(p => { if (p.Items.Any(i => i.Rows > 0)) { cts.Cancel(); } }), cts.Token);
        Assert.True(cancelled.Cancelled || File.Exists(path));
        if (cancelled.Cancelled)
        {
            Assert.False(File.Exists(path));
            Assert.False(File.Exists(path + ".partial"));
        }
        // The job connection was reset and still works.
        Assert.Equal(1L, Convert.ToInt64(await _host.RunJobAsync((h, ct) => h.ExecuteScalarAsync("SELECT 1", ct), Ct), System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>IProgress that calls right away, on the reporting thread (Progress&lt;T&gt; needs a synchronisation context).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
