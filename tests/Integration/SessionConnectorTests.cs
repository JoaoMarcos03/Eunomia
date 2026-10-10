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
    public async Task LigaContaEstatisticasEMantemAListaDeAbertas()
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
    public async Task CredenciaisErradasContamComoFalha()
    {
        TestServer.RequireServer();
        var (connector, session) = Create();

        await Assert.ThrowsAnyAsync<Exception>(() => connector.ConnectAsync(session, new LoginCredentials("nao_existe_x", "x"), Ct));

        Assert.Equal(1, session.Statistics.RefusedCount);
        Assert.Empty(connector.Open);
    }

    [Fact]
    public async Task ListaAsBasesComUmaLigacaoTemporaria()
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
