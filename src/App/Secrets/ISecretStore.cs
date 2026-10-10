namespace Eunomia.App.Secrets;

/// <summary>
/// Secret vault (section 23.2, ADR-10): database and SSH passwords never go into the settings. On Windows it is the
/// Credential Manager (and DPAPI for large secrets, <c>Infra.Secrets.Windows</c>); on macOS the Keychain
/// (<c>Infra.Secrets.MacOS</c>); on Linux the Secret Service (<c>Infra.Secrets.Linux</c>).
/// </summary>
public interface ISecretStore
{
    string? Get(string key);

    void Set(string key, string secret);

    void Delete(string key);

    /// <summary>Saved secrets survive closing the application (false for an in-memory vault).</summary>
    bool IsPersistent => true;
}

/// <summary>Keys of a session's secrets (by the session's stable id, not by its name).</summary>
public static class SecretKeys
{
    public static string Password(Guid session) => $"conn/{session:D}";

    public static string SshPassword(Guid session) => $"ssh/{session:D}";

    public static string SshPassphrase(Guid session) => $"sshkey/{session:D}";

    public static IEnumerable<string> All(Guid session) => [Password(session), SshPassword(session), SshPassphrase(session)];
}

/// <summary>In-memory vault (tests; ad hoc sessions from the command line).</summary>
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
