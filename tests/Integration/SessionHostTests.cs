using System.Collections.Concurrent;
using System.Diagnostics;
using Eunomia.Drivers.Abstractions;
using Eunomia.Drivers.MySql;
using Eunomia.Engine.Execution;
using MySqlConnector;

namespace Eunomia.Integration.Tests;

/// <summary>SessionHost against a real server (section 12; REQ-CON-03/04/05; FAM-S07).</summary>
public sealed class SessionHostTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Log : ISessionLog
    {
        public ConcurrentQueue<(SessionLogCategory Category, string Message)> Lines { get; } = new();

        public void Write(string session, SessionLogCategory category, string message) => Lines.Enqueue((category, message));
    }

    private static (SessionHost Host, Log Log) Create(Func<SessionConnectionSettings, SessionConnectionSettings>? tweak = null)
    {
        var password = new MySqlConnectionStringBuilder(TestServer.ConnectionString!).Password;
        var settings = new SessionConnectionSettings("it", TestServer.Options()) { KeepAlive = null };
        settings = tweak?.Invoke(settings) ?? settings;
        var log = new Log();
        var services = new SessionServices
        {
            Provider = new MySqlProvider(),
            ResolveSecret = (_, _) => ValueTask.FromResult(password),
            Log = log,
            SplitScript = s => s.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        };
        return (new SessionHost(settings, services), log);
    }

    private static Task<long> ScalarAsync(SessionHost host, string sql) =>
        host.RunPrimaryAsync(async (h, ct) => Convert.ToInt64(await h.ExecuteScalarAsync(sql, ct), System.Globalization.CultureInfo.InvariantCulture), Ct);

    [Fact]
    public async Task ConnectsWithSeparatePrimaryAndMetadata()
    {
        TestServer.RequireServer();
        var (host, log) = Create();
        var states = new List<SessionState>();
        host.StateChanged += (_, e) => states.Add(e.State);

        await host.ConnectAsync(Ct);
        var primaryId = await ScalarAsync(host, "SELECT CONNECTION_ID()");
        var metadataId = await host.RunMetadataAsync(async (h, ct) => Convert.ToInt64(await h.ExecuteScalarAsync("SELECT CONNECTION_ID()", ct), System.Globalization.CultureInfo.InvariantCulture), Ct);

        Assert.Equal(SessionState.Ready, host.State);
        Assert.NotEqual(primaryId, metadataId);
        Assert.Equal(primaryId, host.PrimaryConnectionId);
        Assert.NotNull(host.Server);
        // The TLS cipher comes in the same round trip as the version; on loopback, without TLS, there is no warning.
        Assert.NotNull(host.Encryption);
        Assert.DoesNotContain(log.Lines, l => l.Message.Contains("not encrypted", StringComparison.Ordinal));
        Assert.Contains(SessionState.Connecting, states);
        Assert.Contains(log.Lines, l => l.Category == SessionLogCategory.InternalSql && l.Message == "SELECT CONNECTION_ID()");
        Assert.Contains(log.Lines, l => l.Category == SessionLogCategory.UserSql && l.Message == "SELECT CONNECTION_ID()");
        await host.DisposeAsync();
        Assert.Equal(SessionState.Closed, host.State);
    }

    [Fact]
    public async Task MetadataDoesNotWaitForALongQueryOnThePrimary()
    {
        TestServer.RequireServer();
        var (host, _) = Create();
        await host.ConnectAsync(Ct);

        var longQuery = ScalarAsync(host, "SELECT SLEEP(3)");
        await Task.Delay(200, Ct);
        var clock = Stopwatch.StartNew();
        var databases = await host.RunMetadataAsync((h, ct) => h.Metadata.GetCatalogsAsync(ct), Ct);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1.5), $"Metadata esperou {clock.Elapsed}.");
        Assert.Contains("information_schema", databases);
        Assert.Equal(SessionState.Busy, host.State);
        host.CancelPrimary();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => longQuery);
        await host.DisposeAsync();
    }

    [Fact]
    public async Task VisibleReconnectAfterThePrimaryIsKilled()
    {
        TestServer.RequireServer();
        var (host, log) = Create();
        var states = new ConcurrentQueue<SessionState>();
        host.StateChanged += (_, e) => states.Enqueue(e.State);
        await host.ConnectAsync(Ct);
        await host.UseDatabaseAsync("mysql", Ct);
        var killed = host.PrimaryConnectionId!.Value;

        await using (var other = await TestServer.OpenAsync())
        {
            await other.ExecuteNonQueryAsync($"KILL {killed}", Ct);
        }

        // A user query is never retried on its own: it fails with "connection lost" and the session reconnects.
        await Assert.ThrowsAsync<SessionConnectionLostException>(() => ScalarAsync(host, "SELECT 1"));
        var deadline = Stopwatch.StartNew();
        while (host.State != SessionState.Ready && deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(50, Ct);
        }

        Assert.Equal(SessionState.Ready, host.State);
        Assert.Contains(SessionState.Reconnecting, states);
        Assert.NotEqual(killed, host.PrimaryConnectionId);
        Assert.Equal("mysql", await host.RunPrimaryAsync(async (h, ct) => (string?)await h.ExecuteScalarAsync("SELECT DATABASE()", ct), Ct));
        Assert.Contains(log.Lines, l => l.Message.StartsWith("Connection lost", StringComparison.Ordinal));
        Assert.Contains(log.Lines, l => l.Message.StartsWith("Reconnected", StringComparison.Ordinal));
        await host.DisposeAsync();
    }

    [Fact]
    public async Task MetadataRetriesAfterReconnecting()
    {
        TestServer.RequireServer();
        var (host, _) = Create();
        await host.ConnectAsync(Ct);
        var metadataId = await host.RunMetadataAsync(async (h, ct) => h.ServerConnectionId, Ct);

        await using (var other = await TestServer.OpenAsync())
        {
            await other.ExecuteNonQueryAsync($"KILL {metadataId}", Ct);
        }
        var catalogs = await host.RunMetadataAsync((h, ct) => h.Metadata.GetCatalogsAsync(ct), Ct);

        Assert.Contains("mysql", catalogs);
        await host.DisposeAsync();
    }

    [Fact]
    public async Task ClientTimeoutCancelsTheQuery()
    {
        TestServer.RequireServer();
        var (host, _) = Create(s => s with { QueryTimeout = TimeSpan.FromMilliseconds(500) });
        await host.ConnectAsync(Ct);
        var clock = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => ScalarAsync(host, "SELECT SLEEP(5)"));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4));
        Assert.Contains("timeout", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1L, await ScalarAsync(host, "SELECT 1"));
        await host.DisposeAsync();
    }

    [Fact]
    public async Task StartupScriptAndTimeZoneOnEveryConnection()
    {
        TestServer.RequireServer();
        var script = Path.Combine(Path.GetTempPath(), "eunomia-startup-" + Guid.NewGuid().ToString("N") + ".sql");
        await File.WriteAllTextAsync(script, "SET @startup = 42; SELEC error; SET @other = 'ok'", Ct);
        var (host, log) = Create(s => s with { StartupScriptPath = script, TimeZone = "+03:00" });
        await host.ConnectAsync(Ct);

        Assert.Equal(42L, await ScalarAsync(host, "SELECT @startup"));
        Assert.Equal("ok", await host.RunPrimaryAsync(async (h, ct) => (string?)await h.ExecuteScalarAsync("SELECT @other", ct), Ct));
        Assert.Equal("+03:00", await host.RunMetadataAsync(async (h, ct) => (string?)await h.ExecuteScalarAsync("SELECT @@session.time_zone", ct), Ct));
        Assert.Contains(log.Lines, l => l.Category == SessionLogCategory.Error && l.Message.Contains("1064", StringComparison.Ordinal));
        await host.DisposeAsync();
        File.Delete(script);
    }

    [Fact]
    public async Task JobsOnTheirOwnConnections()
    {
        TestServer.RequireServer();
        var (host, _) = Create(s => s with { MaxJobConnections = 2 });
        await host.ConnectAsync(Ct);
        var primary = await ScalarAsync(host, "SELECT CONNECTION_ID()");

        var jobs = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
            host.RunJobAsync(async (h, ct) =>
            {
                await h.ExecuteScalarAsync("SELECT SLEEP(0.3)", ct);
                return h.ServerConnectionId;
            }, Ct)));

        Assert.DoesNotContain(primary, jobs);
        Assert.InRange(jobs.Distinct().Count(), 1, 2);
        await host.DisposeAsync();
    }

    [Fact]
    public async Task WrongPasswordFailsAndStaysFailed()
    {
        TestServer.RequireServer();
        var settings = new SessionConnectionSettings("bad", TestServer.Options() with { User = "nao_existe_" + Guid.NewGuid().ToString("N")[..6] });
        var host = new SessionHost(settings, new SessionServices { Provider = new MySqlProvider(), ResolveSecret = (_, _) => ValueTask.FromResult("x") });

        await Assert.ThrowsAnyAsync<Exception>(() => host.ConnectAsync(Ct));

        Assert.Equal(SessionState.Failed, host.State);
        await host.DisposeAsync();
    }
}
