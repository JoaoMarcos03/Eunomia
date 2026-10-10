using Eunomia.App.Query;
using Eunomia.Drivers.Abstractions;
using Eunomia.Drivers.MySql;
using Eunomia.Engine.Execution;
using Eunomia.Engine.Execution.Editing;
using Eunomia.Engine.Metadata;
using Eunomia.Engine.Sql.Splitting;
using MySqlConnector;

namespace Eunomia.Integration.Tests;

/// <summary>Transactions of the primary connection (section 14.5; phase 14) against the server.</summary>
public sealed class TransactionTests : IAsyncLifetime
{
    private SessionHost _host = null!;
    private string _database = string.Empty;
    private string _password = string.Empty;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        if (TestServer.ConnectionString is null)
        {
            return;
        }
        _password = new MySqlConnectionStringBuilder(TestServer.ConnectionString).Password;
        _host = new SessionHost(new SessionConnectionSettings("it", TestServer.Options()) { KeepAlive = null }, new SessionServices
        {
            Provider = new MySqlProvider(),
            ResolveSecret = (_, _) => ValueTask.FromResult(_password),
            SplitScript = s => [.. SqlSplitter.Split(s).Select(x => x.Text)],
        });
        await _host.ConnectAsync(Ct);
        _database = "eunomia_tx_" + Guid.NewGuid().ToString("N")[..8];
        await ExecAsync($"CREATE DATABASE `{_database}`");
        await ExecAsync($"CREATE TABLE `{_database}`.t (id INT PRIMARY KEY, v VARCHAR(10)) ENGINE=InnoDB");
        await ExecAsync($"INSERT INTO `{_database}`.t VALUES (1, 'a'), (2, 'b'), (3, 'c')");
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.RunPrimaryAsync((h, ct) => h.ExecuteNonQueryAsync($"DROP DATABASE IF EXISTS `{_database}`", ct), CancellationToken.None);
            await _host.DisposeAsync();
        }
    }

    private Task<int> ExecAsync(string sql) => _host.RunPrimaryAsync((h, ct) => h.ExecuteNonQueryAsync(sql, ct), Ct);

    private async Task<long> CountFromOtherConnectionAsync()
    {
        await using var connection = new MySqlConnection(TestServer.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var cmd = new MySqlCommand($"SELECT COUNT(*) FROM `{_database}`.t", connection);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private Task RunAsync(string sql) => new QueryRunner(_host).RunAsync(sql, SqlSplitter.Split(sql), new QueryRunOptions(), Ct);

    [Fact]
    public async Task EstadoDaTransacaoSegueAsInstrucoesDaQueryTab()
    {
        TestServer.RequireServer();
        await RunAsync($"START TRANSACTION; INSERT INTO `{_database}`.t VALUES (4, 'd')");
        Assert.True(_host.Transactions.HasChanges);
        // Not yet committed for the other connections.
        Assert.Equal(3, await CountFromOtherConnectionAsync());
        await RunAsync("COMMIT");
        Assert.False(_host.Transactions.IsOpen);
        Assert.Equal(4, await CountFromOtherConnectionAsync());

        await RunAsync($"SET autocommit = 0; DELETE FROM `{_database}`.t WHERE id = 4");
        Assert.True(_host.Transactions.HasChanges);
        await RunAsync("ROLLBACK; SET autocommit = 1");
        Assert.False(_host.Transactions.IsOpen);
        Assert.Equal(4, await CountFromOtherConnectionAsync());
    }

    [Fact]
    public async Task LigacaoQueCaiComAlteracoesAvisaQueSePerderam()
    {
        TestServer.RequireServer();
        var lost = 0;
        _host.TransactionLost += (_, _) => lost++;
        await RunAsync($"BEGIN; UPDATE `{_database}`.t SET v = 'x' WHERE id = 1");
        Assert.True(_host.Transactions.HasChanges);
        // Another connection kills the primary one (like a server timeout or the network dropping).
        await using (var killer = new MySqlConnection(TestServer.ConnectionString))
        {
            await killer.OpenAsync(Ct);
            await using var kill = new MySqlCommand($"KILL {_host.PrimaryConnectionId}", killer);
            await kill.ExecuteNonQueryAsync(Ct);
        }
        await Assert.ThrowsAnyAsync<Exception>(() => ExecAsync("SELECT 1"));
        Assert.Equal(1, lost);
        Assert.False(_host.Transactions.IsOpen);
        Assert.Equal("a", Convert.ToString(await _host.RunPrimaryAsync((h, ct) => h.ExecuteScalarAsync($"SELECT v FROM `{_database}`.t WHERE id = 1", ct), Ct), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task ApagarVariasLinhasETudoOuNada()
    {
        TestServer.RequireServer();
        var table = await new MetadataCache(_host, new MetadataOptions()).GetTableAsync(new DbObjectRef(_database, _database, "t", DbObjectKind.Table), Ct);
        var writer = new RowWriter(_host);
        static Dictionary<string, CellLiteral> Key(string id) => new(StringComparer.OrdinalIgnoreCase) { ["id"] = new CellLiteral(id) };

        // The third row no longer exists: none is deleted.
        await Assert.ThrowsAsync<RowWriteException>(() => writer.DeleteRowsAsync(table, [Key("1"), Key("2"), Key("99")], null, Ct));
        Assert.Equal(3, await CountFromOtherConnectionAsync());
        Assert.False(_host.Transactions.IsOpen);

        Assert.Equal(2, await writer.DeleteRowsAsync(table, [Key("1"), Key("2")], null, Ct));
        Assert.Equal(1, await CountFromOtherConnectionAsync());
        Assert.False(_host.Transactions.IsOpen);
    }
}
