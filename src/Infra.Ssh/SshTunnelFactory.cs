using System.Net;
using System.Net.Sockets;
using Eunomia.Drivers.Abstractions;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Eunomia.Infra.Ssh;

/// <summary>
/// Túneis SSH in-process com SSH.NET (ADR-09; D7; PR-07). Autenticação por agent (OpenSSH ou Pageant), chave
/// privada com passphrase do cofre, e password (também keyboard-interactive); nada em linha de comandos. A host key
/// é verificada contra o known_hosts da aplicação antes de qualquer autenticação.
/// </summary>
public sealed class SshTunnelFactory(KnownHostsStore knownHosts, Func<SshAgentClient?>? agentFactory = null) : ISshTunnelFactory
{
    private readonly Func<SshAgentClient?> _agent = agentFactory ?? SshAgentClient.TryConnect;

    public KnownHostsStore KnownHosts { get; } = knownHosts;

    public async Task<ISshTunnel> OpenAsync(
        SshTunnelOptions options,
        string targetHost,
        int targetPort,
        Func<SecretReference, CancellationToken, ValueTask<string>> resolveSecret,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(resolveSecret);
        var methods = new List<AuthenticationMethod>();
        var disposables = new List<IDisposable>();

        if (options.UseAgent && _agent() is { } agent)
        {
            try
            {
                var algorithms = agent.HostAlgorithms();
                if (algorithms.Count > 0)
                {
                    methods.Add(new PrivateKeyAuthenticationMethod(options.User, new AgentKeySource(algorithms)));
                }
            }
            catch (IOException)
            {
                // Agent indisponível: segue para chave e password.
            }
        }
        if (!string.IsNullOrEmpty(options.PrivateKeyPath))
        {
            var passphrase = options.Passphrase is { } p ? await resolveSecret(p, cancellationToken).ConfigureAwait(false) : null;
            var key = string.IsNullOrEmpty(passphrase) ? new PrivateKeyFile(options.PrivateKeyPath) : new PrivateKeyFile(options.PrivateKeyPath, passphrase);
            disposables.Add(key);
            methods.Add(new PrivateKeyAuthenticationMethod(options.User, key));
        }
        if (options.Password is { } secret)
        {
            var password = await resolveSecret(secret, cancellationToken).ConfigureAwait(false);
            methods.Add(new PasswordAuthenticationMethod(options.User, password));
            var interactive = new KeyboardInteractiveAuthenticationMethod(options.User);
            interactive.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts)
                {
                    prompt.Response = password;
                }
            };
            methods.Add(interactive);
        }
        if (methods.Count == 0)
        {
            methods.Add(new NoneAuthenticationMethod(options.User));
        }

        var info = new ConnectionInfo(options.Host, options.Port, options.User, [.. methods]) { Timeout = options.Timeout };
        var client = new SshClient(info) { KeepAliveInterval = options.KeepAlive };
        SshHostKeyException? rejected = null;
        client.HostKeyReceived += (_, e) =>
        {
            var check = KnownHosts.Check(options.Host, options.Port, e.HostKeyName, e.HostKey);
            e.CanTrust = check == HostKeyCheck.Match;
            if (check != HostKeyCheck.Match)
            {
                rejected = new SshHostKeyException(
                    check switch
                    {
                        HostKeyCheck.Unknown => SshHostKeyProblem.Unknown,
                        HostKeyCheck.OtherTypeKnown => SshHostKeyProblem.NewKeyType,
                        _ => SshHostKeyProblem.Changed,
                    },
                    options.Host, options.Port, e.HostKeyName, KnownHostsStore.Fingerprint(e.HostKey), e.HostKey);
            }
        };
        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SshConnectionException) when (rejected is not null)
        {
            Cleanup(client, disposables);
            throw rejected;
        }
        catch
        {
            Cleanup(client, disposables);
            throw;
        }

        var localPort = options.LocalPort > 0 ? options.LocalPort : FreeLocalPort();
        var forward = new ForwardedPortLocal(IPAddress.Loopback.ToString(), (uint)localPort, targetHost, (uint)targetPort);
        client.AddForwardedPort(forward);
        try
        {
            forward.Start();
        }
        catch
        {
            Cleanup(client, disposables);
            throw;
        }
        return new SshTunnel(client, forward, localPort, disposables);
    }

    public void TrustHostKey(SshHostKeyException hostKey)
    {
        ArgumentNullException.ThrowIfNull(hostKey);
        KnownHosts.Trust(hostKey.Host, hostKey.Port, hostKey.KeyType, hostKey.Key);
    }

    /// <summary>Porta livre em 127.0.0.1 escolhida pelo sistema (sem a sondagem de portas do HeidiSQL).</summary>
    public static int FreeLocalPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void Cleanup(SshClient client, List<IDisposable> disposables)
    {
        client.Dispose();
        foreach (var d in disposables)
        {
            d.Dispose();
        }
    }
}

/// <summary>Túnel aberto: avisa quando o cliente SSH ou o forward falham.</summary>
internal sealed class SshTunnel : ISshTunnel
{
    private readonly SshClient _client;
    private readonly ForwardedPortLocal _forward;
    private readonly List<IDisposable> _disposables;
    private int _closed;

    public SshTunnel(SshClient client, ForwardedPortLocal forward, int localPort, List<IDisposable> disposables)
    {
        _client = client;
        _forward = forward;
        _disposables = disposables;
        LocalPort = localPort;
        _client.ErrorOccurred += (_, e) => RaiseClosed(e.Exception);
        _forward.Exception += (_, e) => RaiseClosed(e.Exception);
    }

    public int LocalPort { get; }

    public bool IsConnected => _client.IsConnected && _forward.IsStarted;

    public event EventHandler<Exception?>? Closed;

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _closed, 1);
        try
        {
            if (_forward.IsStarted)
            {
                _forward.Stop();
            }
        }
        catch (ObjectDisposedException)
        {
        }
        _client.Dispose();
        foreach (var d in _disposables)
        {
            d.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    private void RaiseClosed(Exception? error)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            Closed?.Invoke(this, error);
        }
    }
}
