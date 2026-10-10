using Eunomia.Drivers.Abstractions;
using Eunomia.Drivers.MySql;
using Eunomia.Engine.Execution;
using Eunomia.Infra.Ssh;
using MySqlConnector;

namespace Eunomia.Integration.Tests;

/// <summary>
/// In-process SSH tunnel against a real OpenSSH server (REQ-CON-02, PR-07, section 23.2): host key on first
/// contact, blocking a changed key, wrong password, and a MySQL session through the tunnel. Needs
/// <c>EUNOMIA_IT_SSH</c> (<c>host;port;user;password</c>) and <c>EUNOMIA_IT_SSH_TARGET</c> (<c>host;port</c> of the database as seen
/// from the SSH server); CI provides them, locally they are skipped.
/// </summary>
public sealed class SshTunnelTests : IDisposable
{
    private static readonly string[]? Ssh = Environment.GetEnvironmentVariable("EUNOMIA_IT_SSH")?.Split(';');
    private static readonly string[]? Target = Environment.GetEnvironmentVariable("EUNOMIA_IT_SSH_TARGET")?.Split(';');
    private readonly string _knownHosts = Path.Combine(Path.GetTempPath(), "eunomia-kh-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => File.Delete(_knownHosts);

    private static void Require() =>
        Assert.SkipWhen(Ssh is not { Length: 4 } || Target is not { Length: 2 } || TestServer.ConnectionString is null,
            "Sem servidor SSH de teste (EUNOMIA_IT_SSH e EUNOMIA_IT_SSH_TARGET).");

    private static SshTunnelOptions Options(string? password = null) =>
        new(Ssh![0], int.Parse(Ssh[1], System.Globalization.CultureInfo.InvariantCulture), Ssh[2], new SecretReference("ssh"), null)
        {
            UseAgent = false,
            Timeout = TimeSpan.FromSeconds(15),
            Passphrase = null,
        };

    private static Func<SecretReference, CancellationToken, ValueTask<string>> Secrets(string? sshPassword = null) =>
        (r, _) => ValueTask.FromResult(r.Key == "ssh" ? sshPassword ?? Ssh![3] : new MySqlConnectionStringBuilder(TestServer.ConnectionString!).Password);

    /// <summary>The OpenSSH container takes a while to start (mods): retries for 90 s.</summary>
    private async Task<ISshTunnel> OpenWithRetryAsync(SshTunnelFactory factory)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (true)
        {
            try
            {
                return await factory.OpenAsync(Options(), Target![0], int.Parse(Target[1], System.Globalization.CultureInfo.InvariantCulture), Secrets(), Ct);
            }
            catch (SshHostKeyException hostKey) when (hostKey.Problem == SshHostKeyProblem.Unknown)
            {
                factory.TrustHostKey(hostKey);
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(2000, Ct);
            }
        }
    }

    [Fact]
    public async Task HostKeyDesconhecidaPedeConfirmacao()
    {
        Require();
        var factory = new SshTunnelFactory(new KnownHostsStore(_knownHosts), () => null);
        await using (await OpenWithRetryAsync(factory))
        {
        }
        File.Delete(_knownHosts);

        var ex = await Assert.ThrowsAsync<SshHostKeyException>(() =>
            factory.OpenAsync(Options(), Target![0], 3306, Secrets(), Ct));

        Assert.Equal(SshHostKeyProblem.Unknown, ex.Problem);
        Assert.StartsWith("SHA256:", ex.FingerprintSha256, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChaveMudadaEhBloqueada()
    {
        Require();
        var store = new KnownHostsStore(_knownHosts);
        var factory = new SshTunnelFactory(store, () => null);
        await using (await OpenWithRetryAsync(factory))
        {
        }
        var line = File.ReadAllLines(_knownHosts).Single().Split(' ');
        store.Trust(Ssh![0], int.Parse(Ssh[1], System.Globalization.CultureInfo.InvariantCulture), line[1], [1, 2, 3, 4]);

        var ex = await Assert.ThrowsAsync<SshHostKeyException>(() =>
            factory.OpenAsync(Options(), Target![0], 3306, Secrets(), Ct));

        Assert.Equal(SshHostKeyProblem.Changed, ex.Problem);
    }

    [Fact]
    public async Task PasswordErradaFalha()
    {
        Require();
        var factory = new SshTunnelFactory(new KnownHostsStore(_knownHosts), () => null);
        await using (await OpenWithRetryAsync(factory))
        {
        }

        await Assert.ThrowsAnyAsync<Exception>(() =>
            factory.OpenAsync(Options(), Target![0], 3306, Secrets("errada"), Ct));
    }

    [Fact]
    public async Task SessaoMySqlAtravesDoTunel()
    {
        Require();
        var factory = new SshTunnelFactory(new KnownHostsStore(_knownHosts), () => null);
        await using (await OpenWithRetryAsync(factory))
        {
        }
        var target = Target!;
        var db = new MySqlConnectionStringBuilder(TestServer.ConnectionString!);
        var options = new ConnectionOptions(target[0], int.Parse(target[1], System.Globalization.CultureInfo.InvariantCulture), db.UserID,
            db.Password.Length > 0 ? new SecretReference("db") : null, Ssh: Options());
        var host = new SessionHost(new SessionConnectionSettings("ssh", options) { KeepAlive = null }, new SessionServices
        {
            Provider = new MySqlProvider(),
            Ssh = factory,
            ResolveSecret = Secrets(),
        });

        await host.ConnectAsync(Ct);
        var one = await host.RunPrimaryAsync(async (h, ct) => Convert.ToInt64(await h.ExecuteScalarAsync("SELECT 1", ct), System.Globalization.CultureInfo.InvariantCulture), Ct);
        var catalogs = await host.RunMetadataAsync((h, ct) => h.Metadata.GetCatalogsAsync(ct), Ct);

        Assert.Equal(1L, one);
        Assert.Contains("mysql", catalogs);
        await host.DisposeAsync();
    }
}
