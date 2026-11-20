using Eunomia.App.Secrets;
using Eunomia.App.Sessions;
using Eunomia.Drivers.MySql;
using MySqlConnector;

namespace Eunomia.Integration.Tests;

/// <summary>Saved sessions connected to the real server (FAM-S04, S07, S08).</summary>
public sealed class SessionConnectorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (SessionConnector Connector, SessionDefinition Session) Create()
    {
        var b = new MySqlConnectionStringBuilder(TestServer.ConnectionString!);
        var secrets = new MemorySecretStore();
        var session = new SessionDefinition { Name = "it", Host = b.Server, Port = (int)b.Port, User = b.UserID, PasswordSaved = true, KeepAlive = 0 };
        secrets.Set(SecretKeys.Password(session.Id), b.Password);
        return (new SessionConnector(new MySqlProvider(), secrets, null, null), session);
    }

    [Fact]
    public async Task ConnectsCountsStatisticsAndKeepsTheOpenList()
    {
        TestServer.RequireServer();
        var (connector, session) = Create();
        OpenSession? opened = null;
        connector.SessionOpened += (_, o) => opened = o;

        var open = await connector.ConnectAsync(session, null, Ct);
        var again = await connector.ConnectAsync(session, null, Ct);

        Assert.Same(open, again);
        Assert.Same(open, opened);
        Assert.Equal(1, session.Statistics.ConnectCount);
        Assert.NotNull(session.Statistics.LastConnect);
        Assert.False(string.IsNullOrEmpty(session.Statistics.ServerVersionFull));
        Assert.Single(connector.Open);
        await connector.DisconnectAsync(open);
        Assert.Empty(connector.Open);
    }

    [Fact]
    public async Task WrongCredentialsCountAsAFailure()
    {
        TestServer.RequireServer();
        var (connector, session) = Create();

        await Assert.ThrowsAnyAsync<Exception>(() => connector.ConnectAsync(session, new LoginCredentials("nao_existe_x", "x"), Ct));

        Assert.Equal(1, session.Statistics.RefusedCount);
        Assert.Empty(connector.Open);
    }

    [Fact]
    public async Task AccessDeniedKeepsAskingForCredentials()
    {
        TestServer.RequireServer();
        // Nothing in the vault: the password is asked for.
        var connector = new SessionConnector(new MySqlProvider(), new MemorySecretStore(), null, null);
        var (_, session) = Create();
        session.PasswordSaved = false;
        Assert.True(connector.NeedsCredentials(session));

        var error = await Assert.ThrowsAnyAsync<Exception>(() => connector.ConnectAsync(session, new LoginCredentials(session.User, "wrong-password"), Ct));

        Assert.Contains("Access denied", error.Message, StringComparison.Ordinal);
        Assert.True(connector.NeedsCredentials(session));
        Assert.Empty(connector.Open);
    }

    /// <summary>A closed session whose prompted password was good keeps it after a wrong retry, and reconnects with it.</summary>
    [Fact]
    public async Task WrongRetryKeepsTheGoodPromptedPassword()
    {
        TestServer.RequireServer();
        var good = new MySqlConnectionStringBuilder(TestServer.ConnectionString!).Password;
        var connector = new SessionConnector(new MySqlProvider(), new MemorySecretStore(), null, null);
        var (_, session) = Create();
        session.PasswordSaved = false;

        var first = await connector.ConnectAsync(session, new LoginCredentials(session.User, good), Ct);
        await first.Host.DisposeAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => connector.ConnectAsync(session, new LoginCredentials(session.User, "wrong-password"), Ct));

        Assert.False(connector.NeedsCredentials(session));
        var again = await connector.ConnectAsync(session, null, Ct);
        Assert.NotSame(first, again);
        Assert.Same(again, Assert.Single(connector.Open));
        await connector.DisconnectAllAsync();
    }

    /// <summary>Two connects of the same session at the same time open one host; the second gets the first one's session.</summary>
    [Fact]
    public async Task ConcurrentConnectsOpenOneSession()
    {
        TestServer.RequireServer();
        var (connector, session) = Create();
        var opened = 0;
        connector.SessionOpened += (_, _) => Interlocked.Increment(ref opened);

        var all = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => connector.ConnectAsync(session, null, Ct), Ct)));

        Assert.All(all, o => Assert.Same(all[0], o));
        Assert.Equal(1, opened);
        Assert.Equal(1, session.Statistics.ConnectCount);
        Assert.Single(connector.Open);

        // A closed session is replaced, and its host disposed.
        await all[0].Host.DisposeAsync();
        var replacement = await connector.ConnectAsync(session, null, Ct);
        Assert.NotSame(all[0], replacement);
        Assert.Same(replacement, Assert.Single(connector.Open));
        await connector.DisconnectAllAsync();
    }

    [Fact]
    public async Task ListsTheDatabasesWithATemporaryConnection()
    {
        TestServer.RequireServer();
        var (connector, session) = Create();
        session.Databases = "so_esta";

        var databases = await connector.ListDatabasesAsync(session, null, Ct);

        Assert.Contains("mysql", databases);
        Assert.Empty(connector.Open);
        Assert.Equal(0, session.Statistics.ConnectCount);
    }
}
