namespace Eunomia.Drivers.Abstractions;

/// <summary>An open SSH tunnel: the database connection goes to <c>127.0.0.1:</c><see cref="LocalPort"/>.</summary>
public interface ISshTunnel : IAsyncDisposable
{
    int LocalPort { get; }

    bool IsConnected { get; }

    /// <summary>The tunnel dropped (network error, the SSH server closed it); the SessionHost moves to Reconnecting.</summary>
    event EventHandler<Exception?>? Closed;
}

/// <summary>
/// Opens in-process SSH tunnels (section 23.2, ADR-09, D7): agent, key and password, never on a command line, host
/// keys checked against Eunomia's own known_hosts. Implemented in <c>Infra.Ssh</c>.
/// </summary>
public interface ISshTunnelFactory
{
    /// <summary>
    /// Connects to the SSH server and forwards a local port to <paramref name="targetHost"/>:<paramref name="targetPort"/>
    /// (resolved on the SSH server, like HeidiSQL's <c>-L</c>). An unknown or changed host key gives an
    /// <see cref="SshHostKeyException"/>: the UI asks and, if the user accepts, calls <see cref="TrustHostKey"/> and retries.
    /// </summary>
    Task<ISshTunnel> OpenAsync(
        SshTunnelOptions options,
        string targetHost,
        int targetPort,
        Func<SecretReference, CancellationToken, ValueTask<string>> resolveSecret,
        CancellationToken cancellationToken);

    /// <summary>Writes the host key to known_hosts (after the user confirms).</summary>
    void TrustHostKey(SshHostKeyException hostKey);
}

public enum SshHostKeyProblem
{
    /// <summary>First contact: ask for confirmation.</summary>
    Unknown,

    /// <summary>The key changed: block (possible man-in-the-middle attack).</summary>
    Changed,

    /// <summary>
    /// Key of a new type on a host that already has another one saved: ask for confirmation with a warning (it may be a new
    /// server key or someone presenting another type to avoid the changed-key error; as in OpenSSH).
    /// </summary>
    NewKeyType,
}

/// <summary>Host key not yet confirmed or different from the saved one.</summary>
public sealed class SshHostKeyException(SshHostKeyProblem problem, string host, int port, string keyType, string fingerprintSha256, byte[] key)
    : Exception(problem switch
    {
        SshHostKeyProblem.Unknown => $"The authenticity of host '{host}' ({keyType}) can't be established. SHA256 fingerprint: {fingerprintSha256}.",
        SshHostKeyProblem.NewKeyType => $"WARNING: host '{host}' presented a {keyType} key, but a key of another type is already trusted for it. " +
            $"Continue only if you know the server got a new key: otherwise someone may be intercepting the connection. SHA256 fingerprint: {fingerprintSha256}.",
        _ => $"WARNING: the host key of '{host}' ({keyType}) has changed! SHA256 fingerprint: {fingerprintSha256}. The connection was blocked.",
    })
{
    public SshHostKeyProblem Problem { get; } = problem;

    public string Host { get; } = host;

    public int Port { get; } = port;

    public string KeyType { get; } = keyType;

    public string FingerprintSha256 { get; } = fingerprintSha256;

    public byte[] Key { get; } = key;
}
