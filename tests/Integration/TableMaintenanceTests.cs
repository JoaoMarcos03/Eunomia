using System.Globalization;
using Eunomia.App.Objects;
using Eunomia.Drivers.Abstractions;
using Eunomia.Drivers.MySql;
using Eunomia.Engine.Execution;
using Eunomia.Engine.Sql.Splitting;
using MySqlConnector;

namespace Eunomia.Integration.Tests;

/// <summary>Maintenance (appendix B §2.2) against the server: results per table and identical data after OPTIMIZE and REPAIR.</summary>
public sealed class TableMaintenanceTests : IAsyncLifetime
{
    private SessionHost _host = null!;
    private string _database = string.Empty;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        if (TestServer.ConnectionString is null)
        {
            return;
        }
        _database = "eunomia_mt_" + Guid.NewGuid().ToString("N")[..8];
        await ExecAsync($"CREATE DATABASE `{_database}`; " +
            $"CREATE TABLE `{_database}`.t (id INT PRIMARY KEY, name VARCHAR(20)) ENGINE=InnoDB; " +
            $"INSERT INTO `{_database}`.t VALUES (1, 'ação'), (2, NULL), (3, 'x''y'); " +
            $"CREATE TABLE `{_database}`.copy LIKE `{_database}`.t; INSERT INTO `{_database}`.copy SELECT * FROM `{_database}`.t; " +
            $"CREATE TABLE `{_database}`.m (id INT PRIMARY KEY, v TEXT) ENGINE=MyISAM; INSERT INTO `{_database}`.m VALUES (1, 'a'), (2, 'b'); " +
            $"CREATE VIEW `{_database}`.v AS SELECT id FROM `{_database}`.t;");
        _host = await ConnectAsync(readOnly: false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
            await ExecAsync($"DROP DATABASE IF EXISTS `{_database}`");
        }
    }

    private static async Task ExecAsync(string sql)
    {
        await using var connection = new MySqlConnection(TestServer.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var cmd = new MySqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task<string?> ScalarAsync(string sql)
    {
        await using var connection = new MySqlConnection(TestServer.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var cmd = new MySqlCommand(sql, connection);
        return Convert.ToString(await cmd.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture);
    }

    private static async Task<SessionHost> ConnectAsync(bool readOnly)
    {
        var password = new MySqlConnectionStringBuilder(TestServer.ConnectionString ?? string.Empty).Password;
        var host = new SessionHost(new SessionConnectionSettings("it", TestServer.Options()) { KeepAlive = null, ReadOnly = readOnly }, new SessionServices
        {
            Provider = new MySqlProvider(),
            ResolveSecret = (_, _) => ValueTask.FromResult(password),
            SplitScript = s => [.. SqlSplitter.Split(s).Select(x => x.Text)],
        });
        await host.ConnectAsync(Ct);
        return host;
    }

    private DbObjectRef Table(string name, DbObjectKind kind = DbObjectKind.Table) => new(_database, _database, name, kind);

    private Task<IReadOnlyList<MaintenanceMessage>> RunAsync(MaintenanceOperation operation, string table, MaintenanceOptions options = MaintenanceOptions.None,
        DbObjectKind kind = DbObjectKind.Table) =>
        TableMaintenance.RunAsync(_host, operation, Table(table, kind), options, Ct);

    [Fact]
    public async Task CheckEAnalyzeDizemOkPorTabelaEView()
    {
        TestServer.RequireServer();

        var check = await RunAsync(MaintenanceOperation.Check, "t", MaintenanceOptions.Quick | MaintenanceOptions.Extended);
        var view = await RunAsync(MaintenanceOperation.Check, "v", kind: DbObjectKind.View);
        var analyze = await RunAsync(MaintenanceOperation.Analyze, "t");

        Assert.Contains(check, m => m.Table == $"{_database}.t" && m.Operation == "check" && m.Type == "status" && m.Text == "OK");
        Assert.Contains(view, m => m.Type == "status" && m.Text == "OK");
        Assert.Contains(analyze, m => m.Operation == "analyze" && m.Type == "status");
        Assert.DoesNotContain(check.Concat(view).Concat(analyze), m => m.IsError);
    }

    [Fact]
    public async Task ChecksumIgualEmTabelasComOsMesmosDados()
    {
        TestServer.RequireServer();

        var original = Assert.Single(await RunAsync(MaintenanceOperation.Checksum, "t", MaintenanceOptions.Extended));
        var copy = Assert.Single(await RunAsync(MaintenanceOperation.Checksum, "copy", MaintenanceOptions.Extended));
        await ExecAsync($"UPDATE `{_database}`.copy SET name = 'ação!' WHERE id = 1");
        var changed = Assert.Single(await RunAsync(MaintenanceOperation.Checksum, "copy", MaintenanceOptions.Extended));

        Assert.Equal("status", original.Type);
        Assert.True(long.TryParse(original.Text, NumberStyles.None, CultureInfo.InvariantCulture, out _), original.Text);
        Assert.Equal(original.Text, copy.Text);
        Assert.NotEqual(original.Text, changed.Text);
    }

    [Fact]
    public async Task OptimizeERepairNaoMudamOsDados()
    {
        TestServer.RequireServer();
        var before = (await ScalarAsync($"CHECKSUM TABLE `{_database}`.t EXTENDED"), await ScalarAsync($"SELECT GROUP_CONCAT(v ORDER BY id) FROM `{_database}`.m"));
        var innodb = await RunAsync(MaintenanceOperation.Optimize, "t");
        var repairInnoDb = await RunAsync(MaintenanceOperation.Repair, "t", MaintenanceOptions.Quick);
        var myisam = await RunAsync(MaintenanceOperation.Repair, "m", MaintenanceOptions.Extended);
        var optimizeMyIsam = await RunAsync(MaintenanceOperation.Optimize, "m");

        // InnoDB: OPTIMIZE recreates the table (note + OK); REPAIR does not exist (a server note, no error).
        Assert.Contains(innodb, m => m.Type == "status" && m.Text == "OK");
        Assert.Contains(repairInnoDb, m => m.Operation == "repair");
        Assert.Contains(myisam, m => m.Type == "status" && m.Text == "OK");
        Assert.Contains(optimizeMyIsam, m => m.Type == "status");
        Assert.Equal(before.Item1, await ScalarAsync($"CHECKSUM TABLE `{_database}`.t EXTENDED"));
        Assert.Equal(before.Item2, await ScalarAsync($"SELECT GROUP_CONCAT(v ORDER BY id) FROM `{_database}`.m"));
        Assert.Equal("3", await ScalarAsync($"SELECT COUNT(*) FROM `{_database}`.t"));
    }

    [Fact]
    public async Task OutrosObjetosSaoSaltadosETabelaQueNaoExisteDaErro()
    {
        TestServer.RequireServer();

        var routine = Assert.Single(await RunAsync(MaintenanceOperation.Check, "p", kind: DbObjectKind.Procedure));
        var gone = await RunAsync(MaintenanceOperation.Check, "gone");

        Assert.Equal("Skipped - a procedure cannot be maintained.", routine.Text);
        Assert.Contains(gone, m => m.IsError);
    }

    [Fact]
    public async Task SessaoReadOnlyDeixaOChecksumMasRecusaOOptimize()
    {
        TestServer.RequireServer();
        await using var readOnly = await ConnectAsync(readOnly: true);

        var checksum = Assert.Single(await TableMaintenance.RunAsync(readOnly, MaintenanceOperation.Checksum, Table("t"), MaintenanceOptions.None, Ct));
        var refused = await Assert.ThrowsAnyAsync<Exception>(() => TableMaintenance.RunAsync(readOnly, MaintenanceOperation.Optimize, Table("t"), MaintenanceOptions.None, Ct));

        Assert.Equal("status", checksum.Type);
        Assert.Contains("read-only", refused.Message, StringComparison.OrdinalIgnoreCase);
    }
}
