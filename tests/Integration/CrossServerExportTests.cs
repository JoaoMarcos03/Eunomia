using System.Globalization;
using Eunomia.App.Transfer;
using Eunomia.Drivers.Abstractions;
using Eunomia.Drivers.MySql;
using Eunomia.Engine.Execution;
using Eunomia.Engine.Results;
using Eunomia.Engine.Transfer;

namespace Eunomia.Integration.Tests;

/// <summary>
/// Export database as SQL from one server straight to another (output "Server: &lt;session&gt;"), for example MySQL 8.4 to
/// MariaDB and back: a BLOB over half of the target's <c>max_allowed_packet</c>, the column types, a view, a function, a
/// trigger and a table after the BLOB all arrive. Needs a second server in <c>EUNOMIA_IT_MYSQL_TARGET</c>.
/// </summary>
public sealed class CrossServerExportTests : IAsyncLifetime
{
    private SessionHost _first = null!;
    private SessionHost _second = null!;
    private readonly List<(SessionHost Host, string Database)> _cleanup = [];
    private string _directory = string.Empty;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        if (TestServer.ConnectionString is null || TestServer.TargetConnectionString is null)
        {
            return;
        }
        _first = await ConnectAsync("first", TestServer.ConnectionString);
        _second = await ConnectAsync("second", TestServer.TargetConnectionString);
        _directory = Path.Combine(Path.GetTempPath(), "eunomia-it", "export-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_directory);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (host, database) in _cleanup)
        {
            await host.RunPrimaryAsync((h, ct) => h.ExecuteNonQueryAsync($"DROP DATABASE IF EXISTS `{database}`", ct), CancellationToken.None);
        }
        if (_first is not null)
        {
            await _first.DisposeAsync();
        }
        if (_second is not null)
        {
            await _second.DisposeAsync();
        }
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static async Task<SessionHost> ConnectAsync(string name, string connectionString)
    {
        var (options, password) = TestServer.OptionsFor(connectionString);
        var host = new SessionHost(new SessionConnectionSettings(name, options) { KeepAlive = null }, new SessionServices
        {
            Provider = new MySqlProvider(),
            ResolveSecret = (_, _) => ValueTask.FromResult(password),
        });
        await host.ConnectAsync(Ct);
        return host;
    }

    /// <param name="reverse">From the second server to the first one instead.</param>
    /// <param name="toDatabase">Into a chosen database on the target; otherwise into a database with the source's name.</param>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task ExportToAnotherServerCopiesEverything(bool reverse, bool toDatabase)
    {
        TestServer.RequireTargetServer();
        var (source, target) = reverse ? (_second, _first) : (_first, _second);
        Assert.False(await SqlExportToServerJob.SameServerAsync(source, target, Ct), "The two test servers must be different instances.");
        var packet = Math.Min(await PacketAsync(source), await PacketAsync(target));
        var size = packet * 6 / 10 / 10 * 10;
        var name = NewName();
        await CreateDatabaseAsync(source, name);
        var db = $"`{name}`";
        await ExecAsync(
            source,
            $"CREATE TABLE {db}.a_big (id INT PRIMARY KEY, b LONGBLOB)",
            $"INSERT INTO {db}.a_big VALUES (1, REPEAT(0x00FF27225C3B0A0D1A80, {size / 10})), (2, 0xCAFE), (3, NULL), (4, '')",
            $"CREATE TABLE {db}.b_typed (id INT AUTO_INCREMENT PRIMARY KEY, i INT, d DECIMAL(12,3), f DOUBLE, b BIT(4), bin VARBINARY(8), " +
                "dt DATETIME(3), tm TIME, t TEXT, e ENUM('a','b'), s SET('x','y'), u BIGINT UNSIGNED, j JSON, pt POINT NULL, gen INT AS (i * 2) VIRTUAL)",
            $"INSERT INTO {db}.b_typed (i, d, f, b, bin, dt, tm, t, e, s, u, j, pt) VALUES " +
                "(-7, -1234.5, 0.5, b'0101', 0xCAFE, '2026-10-06 12:34:56.789', '-01:02:03', 'it''s \\\\ \"ok\"\\nnew line\\0end ação 😀', 'b', 'x,y', " +
                "18446744073709551615, '{\"k\": [1, 2]}', ST_GeomFromText('POINT(1 2)')), " +
                "(NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL), " +
                "(0, 0, -3.25, b'0', '', '1000-01-01 00:00:00', '838:59:59', '', 'a', '', 0, '[]', NULL)",
            $"CREATE TABLE {db}.c_after (id INT PRIMARY KEY, v VARCHAR(10))",
            $"INSERT INTO {db}.c_after VALUES (1, 'x'), (2, 'y'), (3, NULL)",
            $"CREATE VIEW {db}.v_after AS SELECT id, v FROM {db}.c_after WHERE id > 1",
            $"CREATE FUNCTION {db}.f1(x INT) RETURNS INT DETERMINISTIC RETURN x + 1",
            $"CREATE TRIGGER {db}.trg BEFORE INSERT ON {db}.c_after FOR EACH ROW SET NEW.v = COALESCE(NEW.v, 'new')");
        var targetName = toDatabase ? NewName() : name;
        await CreateDatabaseAsync(target, targetName);
        var objects = await source.RunMetadataAsync((h, ct) => h.Metadata.GetObjectsAsync(name, name, ObjectDetail.Statistics, ct), Ct);
        var request = new SqlExportRequest
        {
            Dump = new SqlDumpOptions { CreateTables = true, DropTables = true, Data = DumpDataMode.Insert, MaxInsertSizeKb = 1024, RemoveDefiner = true },
            Databases = [new SqlExportDatabase(name, [.. objects.OrderBy(o => o.Ref.Name, StringComparer.OrdinalIgnoreCase)])],
            Target = SqlExportTarget.Server,
            TargetSession = Guid.NewGuid(),
            TargetDatabase = toDatabase ? targetName : null,
        };
        Assert.Null(SqlExportToServerJob.Validate(request, sameServer: false));

        var result = await new SqlExportToServerJob(source, target, request, _directory).RunAsync(null, null, Ct);

        Assert.True(result.Completed, result.NotImported ?? string.Join("\n", result.Import!.ErrorList.Select(e => $"#{e.Statement}: {e.Message}\n{e.Sql[..Math.Min(e.Sql.Length, 300)]}")));
        const string big = "SELECT CONCAT_WS('|', id, LENGTH(b), MD5(b), b IS NULL) FROM {0}.a_big ORDER BY id";
        const string typed = "SELECT CONCAT_WS('|', id, i, d, f, b + 0, HEX(bin), dt, tm, HEX(t), e, s, u, JSON_EXTRACT(j, '$.k[1]'), JSON_LENGTH(j), " +
            "ST_AsText(pt), gen) FROM {0}.b_typed ORDER BY id";
        const string after = "SELECT CONCAT_WS('|', id, v, 'v') FROM {0}.c_after ORDER BY id";
        foreach (var query in new[] { big, typed, after, "SELECT CONCAT_WS('|', id, v) FROM {0}.v_after ORDER BY id", "SELECT CONCAT({0}.f1(7))" })
        {
            Assert.Equal(await RowsAsync(source, string.Format(CultureInfo.InvariantCulture, query, db)), await RowsAsync(target, string.Format(CultureInfo.InvariantCulture, query, $"`{targetName}`")));
        }
        Assert.Equal([size.ToString(CultureInfo.InvariantCulture)], await RowsAsync(target, $"SELECT CONCAT(LENGTH(b)) FROM `{targetName}`.a_big WHERE id = 1"));
        await ExecAsync(target, $"INSERT INTO `{targetName}`.c_after (id) VALUES (4)");
        Assert.Equal(["new"], await RowsAsync(target, $"SELECT v FROM `{targetName}`.c_after WHERE id = 4"));
    }

    private string NewName() => "eunomia_xs_" + Guid.NewGuid().ToString("N")[..8];

    private async Task CreateDatabaseAsync(SessionHost host, string name)
    {
        _cleanup.Add((host, name));
        await ExecAsync(host, $"CREATE DATABASE `{name}` CHARACTER SET utf8mb4");
    }

    private static async Task<long> PacketAsync(SessionHost host) =>
        Convert.ToInt64(await host.RunPrimaryAsync((h, ct) => h.ExecuteScalarAsync("SELECT @@max_allowed_packet", ct), Ct), CultureInfo.InvariantCulture);

    private static Task ExecAsync(SessionHost host, params string[] statements) => host.RunPrimaryAsync(
        async (h, ct) =>
        {
            foreach (var sql in statements)
            {
                await h.ExecuteNonQueryAsync(sql, ct);
            }
            return true;
        },
        Ct);

    /// <summary>The first column of every row, as text (<c>NULL</c> for null), in order: the queries return one text column.</summary>
    private static Task<List<string>> RowsAsync(SessionHost host, string sql) => host.RunPrimaryAsync(
        async (h, ct) =>
        {
            var values = new List<string>();
            foreach (var block in (await TestServer.RunAsync(h, sql)).OfType<RowBlockReceived>().Select(e => e.Block))
            {
                for (var row = 0; row < block.RowCount; row++)
                {
                    values.Add(block.IsNull(row, 0) ? "NULL" : block.GetString(row, 0));
                }
            }
            return values;
        },
        Ct);
}
