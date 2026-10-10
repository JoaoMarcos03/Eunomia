using Eunomia.App.Secrets;
using Eunomia.App.Sessions;
using Eunomia.Drivers.Abstractions;

namespace Eunomia.App.Tests;

/// <summary>Conversão da sessão gravada para os parâmetros do SessionHost (TLS, SSH, segredos, timeouts).</summary>
public sealed class SessionConnectorTests
{
    private sealed class NoProvider : IDbProvider
    {
        public DbEngine Engine => DbEngine.MySql;

        public IConnectionFactory Connections => throw new NotSupportedException();

        public IErrorClassifier Errors => throw new NotSupportedException();

        public IServerServices ForServer(ServerIdentity server) => throw new NotSupportedException();

        public DbError? TranslateException(Exception exception) => null;
    }

    private static SessionConnector Connector(ISecretStore secrets) => new(new NoProvider(), secrets, null, null);

    [Fact]
    public void SemSslETlsPreferidoComPasswordDoCofre()
    {
        var secrets = new MemorySecretStore();
        var s = new SessionDefinition { Name = "local", Host = "db.example.com", Port = 3307, User = "app", PasswordSaved = true, QueryTimeout = 0, KeepAlive = 0 };
        secrets.Set(SecretKeys.Password(s.Id), "pw");

        var settings = Connector(secrets).ToSettings(s);

        Assert.Equal(("db.example.com", 3307, "app"), (settings.Connection.Host, settings.Connection.Port, settings.Connection.User));
        Assert.Equal(TlsMode.Preferred, settings.Connection.Tls);
        Assert.Equal(SecretKeys.Password(s.Id), settings.Connection.Password!.Value.Key);
        Assert.Null(settings.QueryTimeout);
        Assert.Null(settings.KeepAlive);
        Assert.Null(settings.Connection.Ssh);
        Assert.Contains("using password: Yes", settings.ConnectDescription, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, TlsMode.Required)]
    [InlineData(1, TlsMode.VerifyCa)]
    [InlineData(2, TlsMode.VerifyFull)]
    public void NiveisDeVerificacaoSsl(int verification, TlsMode expected)
    {
        var s = new SessionDefinition();
        s.Ssl.Active = true;
        s.Ssl.Verification = verification;
        s.Ssl.CaCertificate = "C:\\ca.pem";

        var settings = Connector(new MemorySecretStore()).ToSettings(s);

        Assert.Equal(expected, settings.Connection.Tls);
        Assert.Equal("C:\\ca.pem", settings.Connection.TlsFiles!.CaCertificate);
    }

    [Fact]
    public void SshUsaOHostDaBaseQuandoVazioEOsSegredosGravados()
    {
        var secrets = new MemorySecretStore();
        var s = new SessionDefinition { Host = "10.0.0.5", Port = 3306, NetType = NetType.MySqlSshTunnel };
        s.Ssh.Active = true;
        s.Ssh.User = "deploy";
        s.Ssh.PrivateKeyFile = "C:\\keys\\id";
        secrets.Set(SecretKeys.SshPassphrase(s.Id), "frase");

        var ssh = Connector(secrets).ToSettings(s).Connection.Ssh!;

        Assert.Equal(("10.0.0.5", 22, "deploy"), (ssh.Host, ssh.Port, ssh.User));
        Assert.Equal("C:\\keys\\id", ssh.PrivateKeyPath);
        Assert.Null(ssh.Password);
        Assert.Equal(SecretKeys.SshPassphrase(s.Id), ssh.Passphrase!.Value.Key);
    }

    [Fact]
    public void PedeCredenciaisSemPasswordGravadaOuComPrompt()
    {
        var secrets = new MemorySecretStore();
        var connector = Connector(secrets);
        var s = new SessionDefinition();

        Assert.True(connector.NeedsCredentials(s));
        secrets.Set(SecretKeys.Password(s.Id), "pw");
        Assert.False(connector.NeedsCredentials(s));
        s.LoginPrompt = true;
        Assert.True(connector.NeedsCredentials(s));
    }

    [Fact]
    public void TimeoutsTimeZoneELigacoes()
    {
        var s = new SessionDefinition { QueryTimeout = 30, KeepAlive = 20, LocalTimeZone = true, MetadataConnection = false, JobConnections = 0, StartupScript = "C:\\s.sql" };

        var settings = Connector(new MemorySecretStore()).ToSettings(s);

        Assert.Equal(TimeSpan.FromSeconds(30), settings.QueryTimeout);
        Assert.Equal(TimeSpan.FromSeconds(20), settings.KeepAlive);
        Assert.Matches(@"^[+-]\d\d:\d\d$", settings.TimeZone!);
        Assert.False(settings.SeparateMetadataConnection);
        Assert.Equal(1, settings.MaxJobConnections);
        Assert.Equal("C:\\s.sql", settings.StartupScriptPath);
    }
}
