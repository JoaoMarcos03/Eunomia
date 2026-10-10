using Eunomia.App.Query;
using Eunomia.Drivers.Abstractions;
using Eunomia.Drivers.MySql;
using Eunomia.Engine.Execution;
using Eunomia.Engine.Execution.Editing;
using Eunomia.Engine.Metadata;
using Eunomia.Engine.Sql.Splitting;
using MySqlConnector;

namespace Eunomia.Integration.Tests;

/// <summary>Read-only sessions (section 23.2; phase 14): the client refuses writes and so does the server.</summary>
public sealed class ReadOnlySessionTests : IAsyncLifetime
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
        _database = "eunomia_ro_" + Guid.NewGuid().ToString("N")[..8];
        await using (var setup = new MySqlConnection(TestServer.ConnectionString))
        {
            await setup.OpenAsync(Ct);
            await using var cmd = new MySqlCommand($"CREATE DATABASE `{_database}`; CREATE TABLE `{_database}`.t (id INT PRIMARY KEY, v VARCHAR(10)) ENGINE=InnoDB; INSERT INTO `{_database}`.t VALUES (1, 'a');", setup);
            await cmd.ExecuteNonQueryAsync(Ct);
        }
        var password = new MySqlConnectionStringBuilder(TestServer.ConnectionString).Password;
        _host = new SessionHost(new SessionConnectionSettings("prod", TestServer.Options()) { KeepAlive = null, ReadOnly = true }, new SessionServices
        {
            Provider = new MySqlProvider(),
            ResolveSecret = (_, _) => ValueTask.FromResult(password),
            SplitScript = s => [.. SqlSplitter.Split(s).Select(x => x.Text)],
        });
        await _host.ConnectAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
            await using var cleanup = new MySqlConnection(TestServer.ConnectionString);
            await cleanup.OpenAsync(CancellationToken.None);
            await using var cmd = new MySqlCommand($"DROP DATABASE IF EXISTS `{_database}`", cleanup);
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private async Task<string?> ValueAsync()
    {
        await using var connection = new MySqlConnection(TestServer.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var cmd = new MySqlCommand($"SELECT v FROM `{_database}`.t WHERE id = 1", connection);
        return Convert.ToString(await cmd.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task QueryTabReadsButDoesNotWrite()
    {
        TestServer.RequireServer();
        var runner = new QueryRunner(_host);
        var sql = $"SELECT * FROM `{_database}`.t; UPDATE `{_database}`.t SET v = 'x'; SELECT 2;";
        var results = new List<QueryResult>();
        runner.ResultCompleted += (_, r) => results.Add(r);
        var summary = await runner.RunAsync(sql, SqlSplitter.Split(sql), new QueryRunOptions(), Ct);

        Assert.Single(results);
        var error = Assert.Single(summary.Errors);
        Assert.Equal(1, error.StatementIndex);
        Assert.Contains("read-only", error.Error.Message, StringComparison.Ordinal);
        Assert.Equal("a", await ValueAsync());
    }

    [Fact]
    public async Task GridAndDdlAreRefusedToo()
    {
        TestServer.RequireServer();
        var table = await new MetadataCache(_host, new MetadataOptions()).GetTableAsync(new DbObjectRef(_database, _database, "t", DbObjectKind.Table), Ct);
        var writer = new RowWriter(_host);
        var key = new Dictionary<string, CellLiteral>(StringComparer.OrdinalIgnoreCase) { ["id"] = new("1") };
        var change = new Dictionary<string, CellLiteral>(StringComparer.OrdinalIgnoreCase) { ["v"] = new("z") };
        await Assert.ThrowsAnyAsync<Exception>(() => writer.WriteAsync(table, new RowChange(RowChangeKind.Update, key, change), Ct));
        await Assert.ThrowsAnyAsync<Exception>(() => writer.DeleteRowsAsync(table, [key], null, Ct));
        await Assert.ThrowsAnyAsync<Exception>(() => _host.RunPrimaryAsync((h, ct) => h.ExecuteNonQueryAsync($"DROP TABLE `{_database}`.t", ct), Ct));
        await Assert.ThrowsAnyAsync<Exception>(() => _host.RunJobAsync((h, ct) => h.ExecuteNonQueryAsync($"DELETE FROM `{_database}`.t", ct), Ct));
        Assert.Equal("a", await ValueAsync());
    }

    [Fact]
    public async Task TheServerAlsoRefusesWithoutTheClientGuard()
    {
        TestServer.RequireServer();
        // Simulates a failure of the client's guard: the server is in SET SESSION TRANSACTION READ ONLY.
        var error = await Assert.ThrowsAnyAsync<Exception>(() => _host.RunPrimaryAsync(async (h, ct) =>
        {
            var guard = h.Guard;
            h.Guard = null;
            try
            {
                return await h.ExecuteNonQueryAsync($"UPDATE `{_database}`.t SET v = 'server'", ct);
            }
            finally
            {
                h.Guard = guard;
            }
        }, Ct));
        Assert.Contains("READ ONLY", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("a", await ValueAsync());
    }
}
