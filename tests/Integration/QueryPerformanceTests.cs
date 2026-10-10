using System.Diagnostics;
using System.Globalization;
using Eunomia.App.Query;
using Eunomia.Drivers.MySql;
using Eunomia.Engine.Execution;
using Eunomia.Engine.Sql.Splitting;
using MySqlConnector;

namespace Eunomia.Integration.Tests;

/// <summary>
/// PR-10 (overhead per statement: ≤ 1 ms beyond the round trip, zero extra round trips) and PR-20 (stopped cancel ≤ 2 s).
/// Only with <c>EUNOMIA_PERF=1</c>; indicative values (this machine is not the reference one).
/// </summary>
public sealed class QueryPerformanceTests : IAsyncLifetime
{
    private const int Statements = 10_000;
    private SessionHost _host = null!;
    private readonly CommandCounter _commands = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static bool Enabled => TestServer.ConnectionString is not null && Environment.GetEnvironmentVariable("EUNOMIA_PERF") == "1";

    public async ValueTask InitializeAsync()
    {
        if (!Enabled)
        {
            return;
        }
        var password = new MySqlConnectionStringBuilder(TestServer.ConnectionString!).Password;
        _host = new SessionHost(new SessionConnectionSettings("it", TestServer.Options()) { KeepAlive = null }, new SessionServices
        {
            Provider = new MySqlProvider(),
            ResolveSecret = (_, _) => ValueTask.FromResult(password),
            Log = _commands,
        });
        await _host.ConnectAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
    }

    /// <summary>Statements this connection has already sent to the server (the session's <c>Questions</c> status).</summary>
    private Task<long> QuestionsAsync() =>
        _host.RunPrimaryAsync(async (h, ct) =>
        {
            object? value;
            try
            {
                value = await h.ExecuteScalarAsync("SELECT VARIABLE_VALUE FROM information_schema.SESSION_STATUS WHERE VARIABLE_NAME = 'QUESTIONS'", ct);
            }
            catch (MySqlException)
            {
                value = await h.ExecuteScalarAsync("SELECT VARIABLE_VALUE FROM performance_schema.session_status WHERE VARIABLE_NAME = 'Questions'", ct);
            }
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }, Ct);

    [Fact]
    public async Task Pr10OverheadPorInstrucao()
    {
        Assert.SkipUnless(Enabled, "Medição: EUNOMIA_PERF=1 e servidor de teste.");
        var sql = string.Concat(Enumerable.Repeat("SELECT 1;\n", Statements));
        var statements = SqlSplitter.Split(sql);

        // Reference: the same number of round trips with MySqlConnector alone.
        await using (var connection = new MySqlConnection(TestServer.ConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var warm = new MySqlCommand("SELECT 1", connection);
            await warm.ExecuteScalarAsync(Ct);
        }
        var raw = Stopwatch.StartNew();
        await using (var connection = new MySqlConnection(TestServer.ConnectionString))
        {
            await connection.OpenAsync(Ct);
            raw.Restart();
            for (var i = 0; i < Statements; i++)
            {
                await using var command = new MySqlCommand("SELECT 1", connection);
                await using var reader = await command.ExecuteReaderAsync(Ct);
                while (await reader.ReadAsync(Ct))
                {
                }
            }
            raw.Stop();
        }

        var before = await QuestionsAsync();
        var commandsBefore = _commands.Count;
        var oneByOne = Stopwatch.StartNew();
        var summary = await new QueryRunner(_host).RunAsync(sql, statements, new QueryRunOptions(), Ct);
        oneByOne.Stop();
        var roundTrips = _commands.Count - commandsBefore;
        var questions = await QuestionsAsync() - before - 1;

        var batch = Stopwatch.StartNew();
        await new QueryRunner(_host).RunAsync(sql, statements, new QueryRunOptions { BatchInOneGo = true }, Ct);
        batch.Stop();

        var overhead = (oneByOne.Elapsed.TotalMilliseconds - raw.Elapsed.TotalMilliseconds) / Statements;
        TestContext.Current.SendDiagnosticMessage(string.Create(CultureInfo.InvariantCulture,
            $"PR-10: {Statements} x SELECT 1 uma a uma {oneByOne.Elapsed.TotalMilliseconds:F0} ms, MySqlConnector sozinho {raw.Elapsed.TotalMilliseconds:F0} ms, overhead {overhead:F3} ms/instrução, {roundTrips} comandos ({questions} instruções no servidor); batch in one go {batch.Elapsed.TotalMilliseconds:F0} ms"));
        Assert.Equal(Statements, summary.Executed);
        TestContext.Current.TestOutputHelper!.WriteLine(string.Create(CultureInfo.InvariantCulture, $"PR-10: uma a uma {oneByOne.Elapsed.TotalMilliseconds:F0} ms, sozinho {raw.Elapsed.TotalMilliseconds:F0} ms, overhead {overhead:F3} ms, {roundTrips} comandos, {questions} instruções no servidor, batch {batch.Elapsed.TotalMilliseconds:F0} ms"));
        // Zero extra round trips: one command per statement (SHOW WARNINGS follows in the same command).
        Assert.Equal(Statements, roundTrips);
        Assert.True(overhead <= 1.0, $"Overhead {overhead:F3} ms por instrução.");
    }

    [Fact]
    public async Task Pr20CancelDeUmSleep()
    {
        Assert.SkipUnless(Enabled, "Medição: EUNOMIA_PERF=1 e servidor de teste.");
        using var cts = new CancellationTokenSource();
        var run = new QueryRunner(_host).RunAsync("SELECT SLEEP(60);", SqlSplitter.Split("SELECT SLEEP(60);"), new QueryRunOptions(), cts.Token);
        await Task.Delay(500, Ct);
        var clock = Stopwatch.StartNew();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        clock.Stop();
        TestContext.Current.SendDiagnosticMessage(string.Create(CultureInfo.InvariantCulture, $"PR-20: SELECT SLEEP(60) parado {clock.Elapsed.TotalMilliseconds:F0} ms depois do cancel"));
        TestContext.Current.TestOutputHelper!.WriteLine(string.Create(CultureInfo.InvariantCulture, $"PR-20: parado {clock.Elapsed.TotalMilliseconds:F0} ms depois do cancel"));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"Cancel em {clock.Elapsed.TotalMilliseconds:F0} ms.");
    }

    /// <summary>
    /// Reading a large result (300,000 rows × 7 columns of common types): the block executor against
    /// MySqlConnector alone reading every cell, to see what the path to the blocks costs. It only measures and writes to
    /// <c>%TEMP%\eunomia-read-throughput.txt</c>.
    /// </summary>
    [Fact]
    public async Task LeituraDeUmResultadoGrande()
    {
        Assert.SkipUnless(Enabled, "Medição: EUNOMIA_PERF=1 e servidor de teste.");
        const string db = "eunomia_read_perf";
        const string digits = "(SELECT 0 n UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9)";
        await _host.RunPrimaryAsync(async (h, ct) =>
        {
            await h.ExecuteNonQueryAsync($"CREATE DATABASE IF NOT EXISTS `{db}`", ct);
            await h.ExecuteNonQueryAsync($"CREATE TABLE IF NOT EXISTS `{db}`.t (id INT PRIMARY KEY, big BIGINT, name VARCHAR(40), amount DECIMAL(12,2), created DATETIME, price DOUBLE, flag TINYINT) ENGINE=InnoDB", ct);
            if (Convert.ToInt64(await h.ExecuteScalarAsync($"SELECT COUNT(*) FROM `{db}`.t", ct), CultureInfo.InvariantCulture) == 0)
            {
                await h.ExecuteNonQueryAsync(
                    $"INSERT INTO `{db}`.t SELECT seq, seq * 1000003, CONCAT('name ', seq), seq / 7, '2026-01-01' + INTERVAL seq SECOND, seq / 3, seq % 2 " +
                    $"FROM (SELECT a.n + 10 * b.n + 100 * c.n + 1000 * d.n + 10000 * e.n + 100000 * f.n + 1 AS seq FROM {digits} a, {digits} b, {digits} c, {digits} d, {digits} e, (SELECT 0 n UNION ALL SELECT 1 UNION ALL SELECT 2) f) s", ct);
            }
            return true;
        }, Ct);
        var sql = $"SELECT * FROM `{db}`.t";

        var raw = new List<double>();
        var engine = new List<double>();
        for (var run = 0; run < 4; run++)
        {
            await using (var connection = new MySqlConnection(TestServer.ConnectionString))
            {
                await connection.OpenAsync(Ct);
                var clock = Stopwatch.StartNew();
                await using var cmd = new MySqlCommand(sql, connection);
                await using var reader = await cmd.ExecuteReaderAsync(Ct);
                long cells = 0;
                while (await reader.ReadAsync(Ct))
                {
                    for (var c = 0; c < reader.FieldCount; c++)
                    {
                        if (reader.GetValue(c) is not null)
                        {
                            cells++;
                        }
                    }
                }
                raw.Add(clock.Elapsed.TotalMilliseconds);
                Assert.Equal(300_000 * 7, cells);
            }
            var sw = Stopwatch.StartNew();
            var rows = await _host.RunPrimaryAsync(async (h, ct) =>
            {
                long count = 0;
                await foreach (var e in h.Executor.ExecuteAsync(Eunomia.Drivers.Abstractions.SqlBatch.Single(sql), new Eunomia.Drivers.Abstractions.ExecuteOptions(1000) { CollectWarnings = false }, ct))
                {
                    if (e is Eunomia.Drivers.Abstractions.RowBlockReceived block)
                    {
                        count += block.Block.RowCount;
                    }
                }
                return count;
            }, Ct);
            engine.Add(sw.Elapsed.TotalMilliseconds);
            Assert.Equal(300_000, rows);
        }
        var medianRaw = raw.Skip(1).Order().ElementAt(1);
        var medianEngine = engine.Skip(1).Order().ElementAt(1);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "eunomia-read-throughput.txt"), string.Create(CultureInfo.InvariantCulture,
            $"MySqlConnector GetValue: {medianRaw:F0} ms\nexecutor (blocos): {medianEngine:F0} ms\nruns raw: {string.Join(";", raw.Select(r => r.ToString("F0", CultureInfo.InvariantCulture)))}\nruns executor: {string.Join(";", engine.Select(r => r.ToString("F0", CultureInfo.InvariantCulture)))}\n"));
    }

    /// <summary>Counts the commands the session's connections send (each one is a round trip).</summary>
    private sealed class CommandCounter : ISessionLog
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Write(string session, SessionLogCategory category, string message)
        {
            if (category is SessionLogCategory.UserSql or SessionLogCategory.InternalSql)
            {
                Interlocked.Increment(ref _count);
            }
        }
    }
}
