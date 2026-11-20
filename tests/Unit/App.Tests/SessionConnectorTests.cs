using Eunomia.App.Secrets;
using Eunomia.App.Sessions;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Execution;

namespace Eunomia.App.Tests;

/// <summary>Conversion of the saved session into SessionHost parameters (TLS, SSH, secrets, timeouts).</summary>
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

    /// <summary>SSH that only records the password it resolves, then fails (nothing connects).</summary>
    private sealed class RecordingSsh : ISshTunnelFactory
    {
        public List<string> Passwords { get; } = [];

        public async Task<ISshTunnel> OpenAsync(SshTunnelOptions options, string targetHost, int targetPort, Func<SecretReference, CancellationToken, ValueTask<string>> resolveSecret, CancellationToken cancellationToken)
        {
            Passwords.Add(options.Password is { } password ? await resolveSecret(password, cancellationToken) : "<none>");
            throw new InvalidOperationException("Recorded.");
        }

        public void TrustHostKey(SshHostKeyException hostKey)
        {
        }
    }

    private static (SessionCatalog Catalog, SessionDefinition Saved) SshCatalog(ISecretStore secrets)
    {
        var catalog = new SessionCatalog();
        var saved = new SessionDefinition { Name = "prod", Host = "prod", User = "root", PasswordSaved = true, NetType = NetType.MySqlSshTunnel, KeepAlive = 0 };
        saved.Ssh.Active = true;
        saved.Ssh.User = "deploy";
        catalog.Sessions.Add(saved);
        secrets.Set(SecretKeys.Password(saved.Id), "pw");
        secrets.Set(SecretKeys.SshPassword(saved.Id), "vault-ssh");
        return (catalog, saved);
    }

    [Fact]
    public void CommandLineSessionWithoutOverridesCopiesNothing()
    {
        var secrets = new MemorySecretStore();
        var (catalog, saved) = SshCatalog(secrets);
        var connector = Connector(secrets);
        var request = CommandLine.Parse(["-d=prod"]);
        var session = CommandLine.Resolve(request, catalog)!;
        Assert.Same(saved, session);

        connector.ApplyCommandLineSecrets(request, session, catalog);

        // Nothing went to memory: without the vault's password, it asks for one again.
        secrets.Delete(SecretKeys.Password(saved.Id));
        secrets.Delete(SecretKeys.SshPassword(saved.Id));
        Assert.True(connector.NeedsCredentials(saved));
        Assert.Null(connector.ToSettings(saved).Connection.Ssh!.Password);
    }

    [Fact]
    public void CommandLineUnknownSessionCopiesNothing()
    {
        var secrets = new MemorySecretStore();
        var (catalog, _) = SshCatalog(secrets);
        var connector = Connector(secrets);
        var request = CommandLine.Parse(["-d=missing", "-h=other"]);
        var session = CommandLine.Resolve(request, catalog)!;

        connector.ApplyCommandLineSecrets(request, session, catalog);

        Assert.Equal("other", session.Host);
        Assert.True(connector.NeedsCredentials(session));
    }

    [Fact]
    public async Task CommandLineSshPasswordWinsOverTheVaultAndGoesOnDisconnect()
    {
        var secrets = new MemorySecretStore();
        var (catalog, saved) = SshCatalog(secrets);
        var ssh = new RecordingSsh();
        var connector = new SessionConnector(new NoProvider(), secrets, ssh, null);
        var request = CommandLine.Parse(["-d=prod", "--ssh-password=cli-ssh"]);
        var session = CommandLine.Resolve(request, catalog)!;
        Assert.Same(saved, session);

        connector.ApplyCommandLineSecrets(request, session, catalog);

        await Assert.ThrowsAnyAsync<Exception>(() => connector.ConnectAsync(session, null, TestContext.Current.CancellationToken));
        // The list of databases takes the secrets asked for in this run too.
        await Assert.ThrowsAnyAsync<Exception>(() => connector.ListDatabasesAsync(session, null, TestContext.Current.CancellationToken));
        Assert.Equal(["cli-ssh", "cli-ssh"], ssh.Passwords);

        // Disconnecting forgets the SSH password too, not only the database one.
        await connector.DisconnectAsync(new OpenSession(session, new SessionHost(connector.ToSettings(session), new SessionServices { Provider = new NoProvider(), ResolveSecret = (_, _) => ValueTask.FromResult(string.Empty) })));
        secrets.Delete(SecretKeys.SshPassword(saved.Id));
        Assert.Null(connector.ToSettings(saved).Connection.Ssh!.Password);
    }

    [Fact]
    public void WithoutSslTlsPreferredWithPasswordFromTheVault()
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
    public void SslVerificationLevels(int verification, TlsMode expected)
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
    public void SshUsesTheDatabaseHostWhenEmptyAndTheSavedSecrets()
    {
        var secrets = new MemorySecretStore();
        var s = new SessionDefinition { Host = "10.0.0.5", Port = 3306, NetType = NetType.MySqlSshTunnel };
        s.Ssh.Active = true;
        s.Ssh.User = "deploy";
        s.Ssh.PrivateKeyFile = "C:\\keys\\id";
        secrets.Set(SecretKeys.SshPassword(s.Id), "ssh-pw");

        var ssh = Connector(secrets).ToSettings(s).Connection.Ssh!;

        Assert.Equal(("10.0.0.5", 22, "deploy"), (ssh.Host, ssh.Port, ssh.User));
        Assert.Equal("C:\\keys\\id", ssh.PrivateKeyPath);
        Assert.Equal(SecretKeys.SshPassword(s.Id), ssh.Password!.Value.Key);
        Assert.Null(ssh.Passphrase);
    }

    [Fact]
    public void CommandLineSshPasswordAndSavedSecretsReachTheAdHocClone()
    {
        var secrets = new MemorySecretStore();
        var catalog = new SessionCatalog();
        var saved = new SessionDefinition { Name = "prod", Host = "prod", User = "root", PasswordSaved = true, NetType = NetType.MySqlSshTunnel };
        saved.Ssh.Active = true;
        catalog.Sessions.Add(saved);
        secrets.Set(SecretKeys.Password(saved.Id), "pw");
        var connector = Connector(secrets);

        // -d with an override: a clone with a new id, which has nothing in the vault.
        var request = CommandLine.Parse(["-d=prod", "-u=reader", "--ssh-password=ssh-pw"]);
        var clone = CommandLine.Resolve(request, catalog)!;
        Assert.Null(connector.ToSettings(clone).Connection.Ssh!.Password);

        connector.ApplyCommandLineSecrets(request, clone, catalog);

        Assert.False(connector.NeedsCredentials(clone));
        Assert.Equal(SecretKeys.SshPassword(clone.Id), connector.ToSettings(clone).Connection.Ssh!.Password!.Value.Key);
        Assert.Null(secrets.Get(SecretKeys.Password(clone.Id)));
        Assert.Null(secrets.Get(SecretKeys.SshPassword(clone.Id)));
    }

    [Fact]
    public async Task WrongPromptedPasswordIsNotCached()
    {
        var connector = Connector(new MemorySecretStore());
        var s = new SessionDefinition { Host = "db.example.com" };
        Assert.True(connector.NeedsCredentials(s));

        await Assert.ThrowsAnyAsync<Exception>(() => connector.ConnectAsync(s, new LoginCredentials("app", "wrong"), TestContext.Current.CancellationToken));

        Assert.True(connector.NeedsCredentials(s));
    }

    [Fact]
    public void AsksForCredentialsWithoutSavedPasswordOrWithPrompt()
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
    public void TimeoutsTimeZoneAndConnections()
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
