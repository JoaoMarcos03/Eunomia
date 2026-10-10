using Eunomia.App.Query;
using Eunomia.Drivers.MySql;
using Eunomia.Engine.Execution;
using Eunomia.Engine.Sql.Splitting;
using MySqlConnector;

namespace Eunomia.Integration.Tests;

/// <summary>Execução de batches nas query tabs contra o servidor (Fase 6; anexo C §4.4; FAM-Q01–Q04, Q11).</summary>
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
    public async Task UmaAUmaComResultTabsEResumo()
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
    public async Task NomesDosResultTabsPorComentario()
    {
        TestServer.RequireServer();
        var (_, named, _) = await Run("-- names: primeiro, segundo\nSELECT 1; SELECT 2;");
        Assert.Equal(["primeiro (1r × 1c)", "segundo (1r × 1c)"], named.Select(r => r.Caption));

        var (_, single, _) = await Run("SELECT 1;\n-- name: meu\nSELECT 2;");
        Assert.Equal(["Result #1 (1r × 1c)", "meu (1r × 1c)"], single.Select(r => r.Caption));
    }

    [Fact]
    public async Task StopOnErrorsParaNoPrimeiroErroComALinhaCerta()
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
    public async Task BatchInOneGoJuntaAsInstrucoes()
    {
        TestServer.RequireServer();
        var (summary, results, _) = await Run("SELECT 1; SELECT 2; SELECT 3;", new QueryRunOptions { BatchInOneGo = true });

        Assert.Equal(3, results.Count);
        Assert.Equal(3, summary.Executed);
    }

    [Fact]
    public async Task MaximoDeResultTabs()
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
    public async Task DelimiterParaRoutines()
    {
        TestServer.RequireServer();
        var sql = $"USE `{_database}`;\nDELIMITER //\nCREATE PROCEDURE p() BEGIN SELECT 1; SELECT 2; END//\nDELIMITER ;\nCALL p();";

        var (summary, results, _) = await Run(sql);

        Assert.Empty(summary.Errors);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task WarningsVaoParaOLog()
    {
        TestServer.RequireServer();
        var (summary, _, messages) = await Run("SELECT CAST('x' AS SIGNED);");

        Assert.Equal(1, summary.Warnings);
        Assert.Contains(messages, m => m.StartsWith("Warning: (1292)", StringComparison.Ordinal));
    }
}
