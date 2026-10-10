namespace Eunomia.App.Secrets;

/// <summary>The system vault refused an operation (keyring locked or missing, access denied).</summary>
public sealed class SecretStoreException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// System vault with a fallback (phase 14): if the vault fails (Linux without Secret Service, locked Keychain, Credential
/// Manager error), the secret stays only in memory until the application closes, never in a file, and the first failure is
/// reported once. That way a vault problem neither closes the application nor prevents connecting in this run.
/// </summary>
public sealed class ResilientSecretStore(ISecretStore? primary, Action<string>? warn = null) : ISecretStore
{
    private readonly MemorySecretStore _memory = new();
    private int _warned;

    /// <summary>Saved secrets go to the system vault (false without a vault or after a failure).</summary>
    public bool IsPersistent => primary is not null && Volatile.Read(ref _warned) == 0;

    public string? Get(string key)
    {
        if (_memory.Get(key) is { } kept)
        {
            return kept;
        }
        if (primary is null)
        {
            return null;
        }
        try
        {
            return primary.Get(key);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Warn(ex);
            return null;
        }
    }

    public void Set(string key, string secret)
    {
        if (primary is not null)
        {
            try
            {
                primary.Set(key, secret);
                _memory.Delete(key);
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Warn(ex);
            }
        }
        _memory.Set(key, secret);
    }

    public void Delete(string key)
    {
        _memory.Delete(key);
        if (primary is null)
        {
            return;
        }
        try
        {
            primary.Delete(key);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Warn(ex);
        }
    }

    private void Warn(Exception ex)
    {
        if (Interlocked.Exchange(ref _warned, 1) == 0)
        {
            warn?.Invoke($"{ex.Message} Passwords are kept only until Eunomia closes.");
        }
    }
}
