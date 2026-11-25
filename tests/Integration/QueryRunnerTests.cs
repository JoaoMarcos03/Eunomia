using System.Diagnostics;
using System.Globalization;
using Eunomia.App.Query;
using Eunomia.Drivers.MySql;
using Eunomia.Engine.Execution;
using Eunomia.Engine.Sql.Splitting;
using MySqlConnector;

namespace Eunomia.Integration.Tests;

/// <summary>Running batches in the query tabs against the server (phase 6; appendix C §4.4; FAM-Q01–Q04, Q11).</summary>
public sealed class QueryRunnerTests : IAsyncLifetime
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
        var password = new MySqlConnectionStringBuilder(TestServer.ConnectionString).Password;
        _host = new SessionHost(new SessionConnectionSettings("it", TestServer.Options()) { KeepAlive = null }, new SessionServices
        {
            Provider = new MySqlProvider(),
            ResolveSecret = (_, _) => ValueTask.FromResult(password),
        });
        await _host.ConnectAsync(Ct);
        _database = "eunomia_qr_" + Guid.NewGuid().ToString("N")[..8];
        await _host.RunPrimaryAsync(async (h, ct) =>
        {
            await h.ExecuteNonQueryAsync($"CREATE DATABASE `{_database}`", ct);
            await h.ExecuteNonQueryAsync($"CREATE TABLE `{_database}`.people (id INT PRIMARY KEY, name VARCHAR(20))", ct);
            await h.ExecuteNonQueryAsync($"INSERT INTO `{_database}`.people VALUES (1, 'Ana'), (2, 'Rui')", ct);
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

    private async Task<(QueryRunSummary Summary, List<QueryResult> Results, List<string> Messages)> Run(string sql, QueryRunOptions? options = null)
    {
        var runner = new QueryRunner(_host);
        var results = new List<QueryResult>();
        var messages = new List<string>();
        runner.ResultCompleted += (_, r) => results.Add(r);
        runner.Message += (_, m) => messages.Add(m);
        var summary = await runner.RunAsync(sql, SqlSplitter.Split(sql), options ?? new QueryRunOptions(), Ct);
        return (summary, results, messages);
    }

    [Fact]
    public async Task OneByOneWithResultTabsAndSummary()
    {
        TestServer.RequireServer();
        var (summary, results, _) = await Run($"SELECT 1 AS a;\nSELECT * FROM `{_database}`.people;\nUPDATE `{_database}`.people SET name = name WHERE id = 1;");

        Assert.Equal(["Result #1 (1r × 1c)", "people (2r × 2c)"], results.Select(r => r.Caption));
        Assert.Equal(3, summary.Executed);
        Assert.Equal(3, summary.Total);
        Assert.Equal(3, summary.FoundRows);
        Assert.Empty(summary.Errors);
        Assert.StartsWith("Affected rows: ", summary.LogLine, StringComparison.Ordinal);
        Assert.Contains("  Found rows: 3  Warnings: 0  Duration for 3 queries: ", summary.LogLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResultTabNamesFromComments()
    {
        TestServer.RequireServer();
        var (_, named, _) = await Run("-- names: first, second\nSELECT 1; SELECT 2;");
        Assert.Equal(["first (1r × 1c)", "second (1r × 1c)"], named.Select(r => r.Caption));

        var (_, single, _) = await Run("SELECT 1;\n-- name: meu\nSELECT 2;");
        Assert.Equal(["Result #1 (1r × 1c)", "meu (1r × 1c)"], single.Select(r => r.Caption));
    }

    [Fact]
    public async Task StopOnErrorsStopsAtTheFirstErrorWithTheRightLine()
    {
        TestServer.RequireServer();
        var sql = "SELECT 1;\nSELECT\n  1,\n  2 FORM x;\nSELECT 3;";

        var (stopped, results, _) = await Run(sql);
        Assert.Single(results);
        var error = Assert.Single(stopped.Errors);
        Assert.Equal(1, error.StatementIndex);
        Assert.Equal(4, error.Line);
        Assert.Contains("Duration for 1 of 3 queries", stopped.LogLine, StringComparison.Ordinal);

        var (continued, all, _) = await Run(sql, new QueryRunOptions { StopOnErrors = false });
        Assert.Equal(2, all.Count);
        Assert.Single(continued.Errors);
        Assert.Equal(3, continued.Executed);
    }

    [Fact]
    public async Task BatchInOneGoJoinsTheStatements()
    {
        TestServer.RequireServer();
        var (summary, results, _) = await Run("SELECT 1; SELECT 2; SELECT 3;", new QueryRunOptions { BatchInOneGo = true });

        Assert.Equal(3, results.Count);
        Assert.Equal(3, summary.Executed);
    }

    [Fact]
    public async Task MaximumResultTabs()
    {
        TestServer.RequireServer();
        var sql = string.Concat(Enumerable.Range(1, 12).Select(i => $"SELECT {i};\n"));

        var (summary, results, messages) = await Run(sql);

        Assert.Equal(10, results.Count);
        Assert.True(summary.ReachedMaxResults);
        Assert.Contains(messages, m => m.StartsWith("Reached maximum number of result tabs (10)", StringComparison.Ordinal));
        Assert.Equal(12, summary.Executed);
    }

    [Fact]
    public async Task DelimiterForRoutines()
    {
        TestServer.RequireServer();
        var sql = $"USE `{_database}`;\nDELIMITER //\nCREATE PROCEDURE p() BEGIN SELECT 1; SELECT 2; END//\nDELIMITER ;\nCALL p();";

        var (summary, results, _) = await Run(sql);

        Assert.Empty(summary.Errors);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task WarningsGoToTheLog()
    {
        TestServer.RequireServer();
        var (summary, _, messages) = await Run("SELECT CAST('x' AS SIGNED);");

        Assert.Equal(1, summary.Warnings);
        Assert.Contains(messages, m => m.StartsWith("Warning: (1292)", StringComparison.Ordinal));
    }

    /// <summary>A session of its own with a short query timeout, which editor queries must not apply.</summary>
    private static async Task<SessionHost> HostWithTimeoutAsync(TimeSpan timeout)
    {
        var password = new MySqlConnectionStringBuilder(TestServer.ConnectionString!).Password;
        var host = new SessionHost(new SessionConnectionSettings("it", TestServer.Options()) { KeepAlive = null, QueryTimeout = timeout }, new SessionServices
        {
            Provider = new MySqlProvider(),
            ResolveSecret = (_, _) => ValueTask.FromResult(password),
        });
        await host.ConnectAsync(Ct);
        return host;
    }

    private static async Task<long> RunningAsync(string marker)
    {
        await using var other = await TestServer.OpenAsync();
        return Convert.ToInt64(await other.ExecuteScalarAsync(
            $"SELECT COUNT(*) FROM information_schema.PROCESSLIST WHERE INFO LIKE '%{marker}%' AND INFO NOT LIKE '%PROCESSLIST%'", Ct), CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task EditorQueriesRunPastTheSessionQueryTimeout()
    {
        TestServer.RequireServer();
        await using var host = await HostWithTimeoutAsync(TimeSpan.FromSeconds(1));
        const string sql = "SELECT SLEEP(3) AS slept;";

        var summary = await new QueryRunner(host).RunAsync(sql, SqlSplitter.Split(sql), new QueryRunOptions(), Ct);

        Assert.Empty(summary.Errors);
        Assert.Equal(1, summary.Executed);
        Assert.True(summary.Duration >= TimeSpan.FromSeconds(2.5), $"Duration {summary.Duration}.");
    }

    [Fact]
    public async Task CancellingAnEditorQueryStopsItOnTheServer()
    {
        TestServer.RequireServer();
        await using var host = await HostWithTimeoutAsync(TimeSpan.FromSeconds(1));
        var marker = "eunomia_cancel_" + Guid.NewGuid().ToString("N")[..8];
        var sql = $"SELECT SLEEP(60) AS {marker};";
        using var cts = new CancellationTokenSource();
        var run = new QueryRunner(host).RunAsync(sql, SqlSplitter.Split(sql), new QueryRunOptions(), cts.Token);

        // Running on the server, and past the session's 1 s query timeout.
        var deadline = Stopwatch.StartNew();
        while (await RunningAsync(marker) == 0)
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(10), "The query never showed in PROCESSLIST.");
            await Task.Delay(100, Ct);
        }
        await Task.Delay(1500, Ct);
        Assert.False(run.IsCompleted);
        Assert.Equal(1, await RunningAsync(marker));

        var clock = Stopwatch.StartNew();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"Cancel took {clock.Elapsed.TotalMilliseconds:F0} ms.");

        // KILL QUERY, not only the client giving up: the statement is gone from the server.
        while (await RunningAsync(marker) > 0)
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), "The query is still running on the server after the cancel.");
            await Task.Delay(100, Ct);
        }
        // The session goes on working.
        Assert.Equal(1, (await new QueryRunner(host).RunAsync("SELECT 1;", SqlSplitter.Split("SELECT 1;"), new QueryRunOptions(), Ct)).Executed);
    }
}
