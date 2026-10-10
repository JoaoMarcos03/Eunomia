namespace Eunomia.Drivers.Abstractions;

/// <summary>Um túnel SSH aberto: a ligação à base de dados vai para <c>127.0.0.1:</c><see cref="LocalPort"/>.</summary>
public interface ISshTunnel : IAsyncDisposable
{
    int LocalPort { get; }

    bool IsConnected { get; }

    /// <summary>O túnel caiu (erro de rede, servidor SSH fechou); o SessionHost passa a Reconnecting.</summary>
    event EventHandler<Exception?>? Closed;
}

/// <summary>
/// Abre túneis SSH in-process (secção 23.2, ADR-09, D7): agent, chave e password, nunca em linha de comandos, host
/// keys verificadas contra o known_hosts próprio. Implementado em <c>Infra.Ssh</c>.
/// </summary>
public interface ISshTunnelFactory
{
    /// <summary>
    /// Liga ao servidor SSH e faz forward de uma porta local para <paramref name="targetHost"/>:<paramref name="targetPort"/>
    /// (resolvido no servidor SSH, como o <c>-L</c> do HeidiSQL). Host key desconhecida ou mudada dá
    /// <see cref="SshHostKeyException"/>: a UI pergunta e, se o utilizador aceitar, chama <see cref="TrustHostKey"/> e repete.
    /// </summary>
    Task<ISshTunnel> OpenAsync(
        SshTunnelOptions options,
        string targetHost,
        int targetPort,
        Func<SecretReference, CancellationToken, ValueTask<string>> resolveSecret,
        CancellationToken cancellationToken);

    /// <summary>Grava a host key no known_hosts (depois de o utilizador confirmar).</summary>
    void TrustHostKey(SshHostKeyException hostKey);
}

public enum SshHostKeyProblem
{
    /// <summary>Primeiro contacto: pedir confirmação.</summary>
    Unknown,

    /// <summary>A chave mudou: bloquear (possível ataque man-in-the-middle).</summary>
    Changed,

    /// <summary>
    /// Chave de um tipo novo num host que já tem outra gravada: pedir confirmação com aviso (pode ser uma chave nova do
    /// servidor ou alguém a apresentar outro tipo para não dar o erro de chave mudada; como no OpenSSH).
    /// </summary>
    NewKeyType,
}

/// <summary>Host key por confirmar ou diferente da gravada.</summary>
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
