namespace Eunomia.App.Secrets;

/// <summary>
/// Cofre de segredos (secção 23.2, ADR-10): passwords de BD e SSH nunca vão para as settings. Em Windows é o
/// Credential Manager (e DPAPI para segredos grandes, <c>Infra.Secrets.Windows</c>); em macOS o Keychain
/// (<c>Infra.Secrets.MacOS</c>); em Linux o Secret Service (<c>Infra.Secrets.Linux</c>).
/// </summary>
public interface ISecretStore
{
    string? Get(string key);

    void Set(string key, string secret);

    void Delete(string key);

    /// <summary>Os segredos gravados sobrevivem ao fecho da aplicação (falso num cofre só em memória).</summary>
    bool IsPersistent => true;
}

/// <summary>Chaves dos segredos de uma sessão (pelo id estável da sessão, não pelo nome).</summary>
public static class SecretKeys
{
    public static string Password(Guid session) => $"conn/{session:D}";

    public static string SshPassword(Guid session) => $"ssh/{session:D}";

    public static string SshPassphrase(Guid session) => $"sshkey/{session:D}";

    public static IEnumerable<string> All(Guid session) => [Password(session), SshPassword(session), SshPassphrase(session)];
}

/// <summary>Cofre em memória (testes; sessões ad hoc da linha de comandos).</summary>
public sealed class MemorySecretStore : ISecretStore
{
    public bool IsPersistent => false;

    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public string? Get(string key)
    {
        lock (_gate)
        {
            return _values.TryGetValue(key, out var v) ? v : null;
        }
    }

    public void Set(string key, string secret)
    {
        lock (_gate)
        {
            _values[key] = secret;
        }
    }

    public void Delete(string key)
    {
        lock (_gate)
        {
            _values.Remove(key);
        }
    }
}
