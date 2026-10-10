using System.Globalization;
using Eunomia.App.Secrets;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Execution;
using Eunomia.Engine.Sql;
using Eunomia.Engine.Sql.Splitting;

namespace Eunomia.App.Sessions;

/// <summary>Credenciais pedidas no momento de ligar ("Prompt for credentials" ou password não gravada).</summary>
public sealed record LoginCredentials(string User, string Password);

/// <summary>Uma sessão aberta: a definição gravada (cópia do momento de abrir) e o SessionHost.</summary>
public sealed class OpenSession(SessionDefinition definition, SessionHost host)
{
    public SessionDefinition Definition { get; } = definition;

    public SessionHost Host { get; } = host;

    /// <summary>Cache de metadata da sessão (bases, objetos, tabelas).</summary>
    public Eunomia.Engine.Metadata.MetadataCache Metadata { get; } = new(host, new Eunomia.Engine.Metadata.MetadataOptions
    {
        FullTableStatus = definition.FullTableStatus,
        Databases = definition.Databases,
        HideDatabasePattern = definition.HideDatabasePattern,
    });

    /// <summary>Objetos já selecionados nesta sessão (os outros aparecem esbatidos na árvore, anexo D A2).</summary>
    public HashSet<string> SelectedObjects { get; } = new(StringComparer.Ordinal);

    /// <summary>Favoritos da sessão (caminhos <see cref="ObjectPath.Of"/>), carregados do <c>app.db</c> ao abrir.</summary>
    public HashSet<string> Favorites { get; } = new(StringComparer.Ordinal);

    public Guid Id => Definition.Id;

    public string Name => Definition.Path;

    /// <summary>Cor da sessão (FAM-S02) em 0xRRGGBB, ou <c>null</c>.</summary>
    public uint? Color => Definition.Color;

    /// <summary>
    /// Linguagem SQL do motor da sessão (nomes inseridos no editor, palavras e funções, realce); antes de ligar, a do
    /// MySQL (todos os tipos de ligação são da família MySQL).
    /// </summary>
    public ISqlLanguage Language => Host.ServerServices?.Server.Engine switch
    {
        null or DbEngine.MySql or DbEngine.MariaDb => SqlLanguages.MySql,
        var engine => throw new NotSupportedException($"No SQL language for {engine}."),
    };
}

/// <summary>
/// Liga sessões gravadas ao motor (FAM-S07): converte a definição do Session Manager para o SessionHost, resolve
/// segredos no momento de abrir (cofre, ou credenciais pedidas que só vivem em memória), atualiza as estatísticas
/// (FAM-S08) e mantém a lista de sessões abertas. Várias sessões abertas ao mesmo tempo, isoladas.
/// </summary>
public sealed class SessionConnector(
    IDbProvider provider,
    ISecretStore secrets,
    ISshTunnelFactory? ssh,
    ISessionLog? log,
    TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly MemorySecretStore _prompted = new();
    private readonly List<OpenSession> _open = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<OpenSession> Open
    {
        get
        {
            lock (_gate)
            {
                return [.. _open];
            }
        }
    }

    public event EventHandler<OpenSession>? SessionOpened;

    public event EventHandler<OpenSession>? SessionClosed;

    public OpenSession? Find(Guid id) => Open.FirstOrDefault(s => s.Id == id);

    /// <summary>A sessão precisa de credenciais antes de ligar (o diálogo de login do HeidiSQL).</summary>
    public bool NeedsCredentials(SessionDefinition session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return NetTypes.IsSupported(session.NetType)
               && (session.LoginPrompt
                   || ((!session.PasswordSaved || !secrets.IsPersistent) && !session.WindowsAuth
                       && secrets.Get(SecretKeys.Password(session.Id)) is null && _prompted.Get(SecretKeys.Password(session.Id)) is null));
    }

    /// <summary>
    /// Liga. Se já estiver aberta, devolve a existente (como o "Connect to" do HeidiSQL, que só foca o nó).
    /// Erros (incluindo <see cref="SshHostKeyException"/>) chegam ao chamador; a estatística de falhas sobe.
    /// </summary>
    public async Task<OpenSession> ConnectAsync(SessionDefinition session, LoginCredentials? credentials, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (Find(session.Id) is { } existing && existing.Host.State is not (SessionState.Failed or SessionState.Closed))
        {
            return existing;
        }
        if (!NetTypes.IsSupported(session.NetType))
        {
            throw new NotSupportedException($"\"{NetTypes.DisplayName(session.NetType)}\" is not supported in this version.");
        }
        var definition = session.Clone();
        if (credentials is not null)
        {
            definition.User = credentials.User;
            _prompted.Set(SecretKeys.Password(session.Id), credentials.Password);
        }
        var settings = ToSettings(definition);
        var host = new SessionHost(settings, new SessionServices
        {
            Provider = provider,
            Ssh = ssh,
            ResolveSecret = ResolveSecretAsync,
            Log = log is not null && definition.LogFilePath is { Length: > 0 } file && (definition.LogDdl || definition.LogDml)
                ? new QueryFileLog(log, file, definition.LogDdl, definition.LogDml)
                : log,
            SplitScript = script => SqlSplitter.Split(script).Select(s => s.Text).ToList(),
            Clock = _clock,
        });
        try
        {
            await host.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            session.Statistics.RefusedCount++;
            await host.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        session.Statistics.ConnectCount++;
        session.Statistics.LastConnect = _clock.GetUtcNow();
        session.Statistics.ServerVersionFull = host.Server is { } server
            ? server.VersionText + (server.VersionComment is { Length: > 0 } c ? " - " + c : string.Empty)
            : session.Statistics.ServerVersionFull;
        var open = new OpenSession(definition, host);
        lock (_gate)
        {
            _open.RemoveAll(o => o.Id == session.Id);
            _open.Add(open);
        }
        SessionOpened?.Invoke(this, open);
        return open;
    }

    /// <summary>
    /// Liga só para listar as bases (dropdown do campo Databases, anexo A §6): ligação única e temporária, sem entrar na
    /// lista de sessões abertas nem nas estatísticas.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListDatabasesAsync(SessionDefinition session, LoginCredentials? credentials, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var probe = session.Clone();
        probe.Id = Guid.NewGuid();
        if (credentials is not null)
        {
            probe.User = credentials.User;
            _prompted.Set(SecretKeys.Password(probe.Id), credentials.Password);
        }
        else if (secrets.Get(SecretKeys.Password(session.Id)) is { } saved)
        {
            _prompted.Set(SecretKeys.Password(probe.Id), saved);
        }
        if (secrets.Get(SecretKeys.SshPassword(session.Id)) is { } sshPassword)
        {
            _prompted.Set(SecretKeys.SshPassword(probe.Id), sshPassword);
        }
        var settings = ToSettings(probe) with { SeparateMetadataConnection = false, KeepAlive = null, StartupScriptPath = null };
        var host = new SessionHost(settings, new SessionServices { Provider = provider, Ssh = ssh, ResolveSecret = ResolveSecretAsync, Clock = _clock });
        try
        {
            await host.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return await host.RunMetadataAsync((h, ct) => h.Metadata.GetCatalogsAsync(ct), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await host.DisposeAsync().ConfigureAwait(false);
            foreach (var key in SecretKeys.All(probe.Id))
            {
                _prompted.Delete(key);
            }
        }
    }

    public async Task DisconnectAsync(OpenSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            _open.Remove(session);
        }
        _prompted.Delete(SecretKeys.Password(session.Id));
        await session.Host.DisposeAsync().ConfigureAwait(false);
        SessionClosed?.Invoke(this, session);
    }

    public async Task DisconnectAllAsync()
    {
        foreach (var s in Open)
        {
            await DisconnectAsync(s).ConfigureAwait(false);
        }
    }

    private ValueTask<string> ResolveSecretAsync(SecretReference reference, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_prompted.Get(reference.Key) ?? secrets.Get(reference.Key) ?? string.Empty);

    /// <summary>Definição do Session Manager → parâmetros do SessionHost (TLS, SSH, timeouts, replay).</summary>
    public SessionConnectionSettings ToSettings(SessionDefinition s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var hasPassword = s.LoginPrompt || s.PasswordSaved || secrets.Get(SecretKeys.Password(s.Id)) is not null || _prompted.Get(SecretKeys.Password(s.Id)) is not null;
        var tls = !s.Ssl.Active ? TlsMode.Preferred : s.Ssl.Verification switch
        {
            0 => TlsMode.Required,
            1 => TlsMode.VerifyCa,
            _ => TlsMode.VerifyFull,
        };
        SshTunnelOptions? sshOptions = null;
        if (s.Ssh.Active)
        {
            var sshPassword = secrets.Get(SecretKeys.SshPassword(s.Id)) is not null ? new SecretReference(SecretKeys.SshPassword(s.Id)) : (SecretReference?)null;
            var passphrase = secrets.Get(SecretKeys.SshPassphrase(s.Id)) is not null ? new SecretReference(SecretKeys.SshPassphrase(s.Id)) : (SecretReference?)null;
            sshOptions = new SshTunnelOptions(
                string.IsNullOrWhiteSpace(s.Ssh.Host) ? s.Host : s.Ssh.Host,
                s.Ssh.Port,
                s.Ssh.User,
                sshPassword,
                string.IsNullOrWhiteSpace(s.Ssh.PrivateKeyFile) ? null : s.Ssh.PrivateKeyFile,
                s.Ssh.LocalPort)
            {
                Passphrase = passphrase,
                UseAgent = s.Ssh.UseAgent,
                Timeout = TimeSpan.FromSeconds(Math.Max(1, s.Ssh.Timeout) + 6),
            };
        }
        var user = s.WindowsAuth ? Environment.UserName : s.User;
        var options = new ConnectionOptions(
            s.Ssh.Active && string.IsNullOrWhiteSpace(s.Host) ? "127.0.0.1" : s.Host,
            s.Port,
            user,
            hasPassword ? new SecretReference(SecretKeys.Password(s.Id)) : null,
            InitialDatabase: null,
            Tls: tls,
            Ssh: sshOptions,
            Compression: s.Compressed,
            ConnectTimeout: TimeSpan.FromSeconds(15))
        {
            TlsFiles = s.Ssl.Active
                ? new TlsFiles(Empty(s.Ssl.CaCertificate), Empty(s.Ssl.Certificate), Empty(s.Ssl.PrivateKey), Empty(s.Ssl.Cipher))
                : null,
            CleartextAuth = s.CleartextAuth,
        };
        return new SessionConnectionSettings(s.Path, options)
        {
            StartupScriptPath = Empty(s.StartupScript),
            KeepAlive = s.KeepAlive > 0 ? TimeSpan.FromSeconds(s.KeepAlive) : null,
            QueryTimeout = s.QueryTimeout > 0 ? TimeSpan.FromSeconds(s.QueryTimeout) : null,
            SeparateMetadataConnection = s.MetadataConnection,
            MaxJobConnections = Math.Max(1, s.JobConnections),
            TimeZone = s.LocalTimeZone ? LocalOffset(_clock) : null,
            ConnectDescription = $"Connecting to {s.Host} via {NetTypes.DisplayName(s.NetType)}, username {user}, using password: {(hasPassword ? "Yes" : "No")} ...",
            ReadOnly = s.ReadOnly,
        };
    }

    /// <summary>"Use own client time zone": desvio atual do Windows (como o HeidiSQL, sem seguir a mudança de hora).</summary>
    public static string LocalOffset(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var offset = clock.GetLocalNow().Offset;
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var abs = offset.Duration();
        return string.Create(CultureInfo.InvariantCulture, $"{sign}{abs.Hours:00}:{abs.Minutes:00}");
    }

    private static string? Empty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
