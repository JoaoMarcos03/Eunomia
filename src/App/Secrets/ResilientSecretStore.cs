namespace Eunomia.App.Secrets;

/// <summary>O cofre do sistema recusou uma operação (keyring bloqueado ou inexistente, acesso negado).</summary>
public sealed class SecretStoreException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Cofre do sistema com recurso (Fase 14): se o cofre falhar (Linux sem Secret Service, Keychain bloqueado, erro do
/// Credential Manager), o segredo fica só em memória até a aplicação fechar, nunca em ficheiro, e a primeira falha é
/// avisada uma vez. Assim um problema do cofre não fecha a aplicação nem impede de ligar nesta execução.
/// </summary>
public sealed class ResilientSecretStore(ISecretStore? primary, Action<string>? warn = null) : ISecretStore
{
    private readonly MemorySecretStore _memory = new();
    private int _warned;

    /// <summary>Os segredos gravados ficam no cofre do sistema (falso sem cofre ou depois de uma falha).</summary>
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
