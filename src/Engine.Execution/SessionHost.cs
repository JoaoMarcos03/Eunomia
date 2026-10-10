using Eunomia.Drivers.Abstractions;

namespace Eunomia.Engine.Execution;

/// <summary>Dependências do SessionHost (ligadas no composition root).</summary>
public sealed class SessionServices
{
    public required IDbProvider Provider { get; init; }

    /// <summary>Túneis SSH (Infra.Ssh); obrigatório só para sessões com SSH.</summary>
    public ISshTunnelFactory? Ssh { get; init; }

    /// <summary>Lê um segredo do cofre no momento de abrir (nunca fica guardado no SessionHost).</summary>
    public required Func<SecretReference, CancellationToken, ValueTask<string>> ResolveSecret { get; init; }

    public ISessionLog? Log { get; init; }

    /// <summary>Separa o startup script em instruções (o splitter do Engine.Sql).</summary>
    public Func<string, IReadOnlyList<string>> SplitScript { get; init; } = s => [s];

    public TimeProvider Clock { get; init; } = TimeProvider.System;
}

/// <summary>
/// Uma sessão aberta, como ator isolado (secção 12): ligação primária para o trabalho do utilizador, ligação de
/// metadata para a árvore e o autocomplete, ligações de job a pedido; cada uma com a sua fila. Mantém o túnel SSH,
/// faz keep-alive, deteta ligações perdidas e volta a ligar de forma visível (estado Reconnecting, mensagens no log,
/// replay de <c>USE</c>, time zone e startup script). "Reconnecting" e "Failed" nunca bloqueiam outras sessões.
/// </summary>
public sealed class SessionHost : IAsyncDisposable
{
    private readonly SessionServices _services;
    private readonly Lock _gate = new();
    private readonly List<ConnectionLane> _jobs = [];
    private readonly SemaphoreSlim _jobSlots;
    private readonly SemaphoreSlim _tunnelGate = new(1, 1);
    private readonly Timer _keepAlive;
    private ConnectionLane? _primary;
    private ConnectionLane? _metadata;
    private ISshTunnel? _tunnel;
    private SessionState _state = SessionState.Disconnected;
    private int _reconnecting;

    public SessionHost(SessionConnectionSettings settings, SessionServices services)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(services);
        Settings = settings;
        _services = services;
        _jobSlots = new SemaphoreSlim(Math.Max(1, settings.MaxJobConnections));
        _keepAlive = new Timer(_ => KeepAliveTick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public SessionConnectionSettings Settings { get; }

    public string Name => Settings.Name;

    public SessionState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public ServerIdentity? Server { get; private set; }

    public IServerServices? ServerServices { get; private set; }

    public DateTimeOffset? ConnectedAt { get; private set; }

    /// <summary>
    /// Cifra TLS da ligação primária, <c>""</c> sem TLS, <c>null</c> se não se sabe (23.2: uma ligação sem verificação
    /// fica visível).
    /// </summary>
    public string? Encryption => _primary?.Handle?.Encryption;

    /// <summary>A sessão passa por um túnel SSH (os dados vão cifrados até ao servidor SSH, mesmo sem TLS).</summary>
    public bool ThroughSsh => Settings.Connection.Ssh is not null;

    /// <summary>Id da ligação primária no servidor (<c>CONNECTION_ID()</c>).</summary>
    public long? PrimaryConnectionId => _primary?.Handle?.ServerConnectionId;

    /// <summary>Ids no servidor de todas as ligações abertas da sessão (primária, metadata e jobs).</summary>
    public IReadOnlyList<long> OwnConnectionIds
    {
        get
        {
            List<ConnectionLane?> lanes;
            lock (_gate)
            {
                lanes = [_primary, _metadata, .. _jobs];
            }
            return [.. lanes.Select(l => l?.Handle).OfType<IDbConnectionHandle>().Where(h => h.IsOpen).Select(h => h.ServerConnectionId).Distinct()];
        }
    }

    /// <summary>Base em uso na ligação primária.</summary>
    public string? CurrentDatabase => _primary?.Database;

    /// <summary>A primária tem uma operação do utilizador a correr.</summary>
    public bool IsPrimaryBusy => _primary?.IsBusy == true;

    /// <summary>Estado da transação da ligação primária (secção 14.5), seguido pelas instruções que ela envia.</summary>
    public TransactionTracker Transactions { get; } = new();

    /// <summary>
    /// A ligação primária caiu com uma transação aberta com alterações: o servidor desfê-la e essas alterações perderam-se.
    /// </summary>
    public event EventHandler? TransactionLost;

    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Liga (abre túnel SSH se houver e a ligação primária); a de metadata abre em seguida. Erros (host key por
    /// confirmar, password errada, servidor em baixo) chegam ao chamador e a sessão fica em Failed.
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        SetState(SessionState.Connecting, null);
        Log(SessionLogCategory.Info, Settings.ConnectDescription ?? $"Connecting to {Settings.Connection.Host} ...");
        try
        {
            _primary = CreateLane(ConnectionRole.Primary, "primary");
            var handle = await _primary.OpenNowAsync(cancellationToken).ConfigureAwait(false);
            Server = handle.Server;
            ServerServices = _services.Provider.ForConnection(handle);
            ConnectedAt = _services.Clock.GetUtcNow();
            _metadata = Settings.SeparateMetadataConnection ? CreateLane(ConnectionRole.Metadata, "metadata") : _primary;
            SetState(SessionState.Ready, null);
            Log(SessionLogCategory.Info, $"Connected. Thread-ID: {handle.ServerConnectionId}");
            if (handle.Encryption is "" && !ThroughSsh && !IsLocalHost(Settings.Connection.Host))
            {
                Log(SessionLogCategory.Info,
                    $"Warning: the connection to {Settings.Connection.Host} is not encrypted (the server does not use TLS): passwords and data travel in clear text. Use SSL or an SSH tunnel in the session settings.");
            }
            if (Settings.KeepAlive is { } interval && interval > TimeSpan.Zero)
            {
                _keepAlive.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            }
        }
        catch (Exception ex)
        {
            Log(SessionLogCategory.Error, ex.Message);
            await CloseLanesAsync().ConfigureAwait(false);
            SetState(SessionState.Failed, ex);
            throw;
        }
    }

    /// <summary>
    /// Trabalho do utilizador na ligação primária (queries, edições, DDL), com o timeout de cliente (D8).
    /// <paramref name="abandonGrace"/>: ver <see cref="ConnectionLane.RunAsync{T}"/> (leituras que se abandonam ao
    /// navegar).
    /// </summary>
    public Task<T> RunPrimaryAsync<T>(Func<IDbConnectionHandle, CancellationToken, Task<T>> work, CancellationToken cancellationToken, bool applyQueryTimeout = true, TimeSpan abandonGrace = default)
    {
        var lane = _primary ?? throw new InvalidOperationException("The session is not connected.");
        if (applyQueryTimeout && Settings.QueryTimeout is { } timeout && timeout > TimeSpan.Zero)
        {
            return WithTimeoutAsync(lane, work, timeout, cancellationToken, abandonGrace);
        }
        return lane.RunAsync(work, WorkPriority.Visible, SessionLogCategory.UserSql, retryAfterReconnect: false, cancellationToken, abandonGrace);
    }

    /// <summary>Metadata (árvore, colunas, contagens); repete uma vez depois de religar.</summary>
    public Task<T> RunMetadataAsync<T>(Func<IDbConnectionHandle, CancellationToken, Task<T>> work, CancellationToken cancellationToken, WorkPriority priority = WorkPriority.Visible, TimeSpan abandonGrace = default)
    {
        var lane = _metadata ?? throw new InvalidOperationException("The session is not connected.");
        return lane.RunAsync(work, priority, SessionLogCategory.InternalSql, retryAfterReconnect: true, cancellationToken, abandonGrace);
    }

    /// <summary>
    /// Trabalho pesado (export, import) numa ligação de job; no máximo <see cref="SessionConnectionSettings.MaxJobConnections"/>
    /// ao mesmo tempo, os outros esperam na fila (12.6). Com <paramref name="logCommands"/> a <c>false</c>, os comandos não
    /// vão para o SQL log.
    /// </summary>
    public async Task<T> RunJobAsync<T>(Func<IDbConnectionHandle, CancellationToken, Task<T>> work, CancellationToken cancellationToken, bool logCommands = true)
    {
        await _jobSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        ConnectionLane? lane = null;
        try
        {
            lock (_gate)
            {
                lane = _jobs.FirstOrDefault(j => !j.IsBusy && !j.HasPendingWork);
                if (lane is null)
                {
                    lane = CreateLane(ConnectionRole.Job, "job" + (_jobs.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    _jobs.Add(lane);
                }
            }
            return await lane.RunAsync(work, WorkPriority.Visible, logCommands ? SessionLogCategory.Script : SessionLogCategory.None, retryAfterReconnect: false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _jobSlots.Release();
        }
    }

    /// <summary>
    /// Erro de uma instrução dentro de um trabalho (as exceções do driver só são traduzidas à saída dele): o
    /// <see cref="DbError"/>, ou <c>null</c> se a exceção não vem do servidor, é um cancel ou a ligação caiu.
    /// </summary>
    public DbError? StatementError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is OperationCanceledException)
        {
            return null;
        }
        var error = _services.Provider.TranslateException(exception);
        return error is not null && Classify(error).Category != DbErrorCategory.ConnectionLost ? error : null;
    }

    /// <summary>
    /// Ligação própria, fora das filas (o <c>LOAD DATA LOCAL</c> do CSV import, com opções que as outras ligações não
    /// têm): o mesmo túnel e as mesmas opções ajustadas por <paramref name="adjust"/>, com a time zone da sessão e os
    /// comandos no SQL log. Quem a pede fecha-a.
    /// </summary>
    public async Task<IDbConnectionHandle> OpenDedicatedAsync(Func<ConnectionOptions, ConnectionOptions> adjust, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(adjust);
        var options = adjust(Settings.Connection);
        if (options.Ssh is { } ssh)
        {
            var tunnel = await EnsureTunnelAsync(ssh, cancellationToken).ConfigureAwait(false);
            options = options with { Host = "127.0.0.1", Port = tunnel.LocalPort, Ssh = null };
        }
        var handle = await _services.Provider.Connections.OpenAsync(options, _services.ResolveSecret, cancellationToken).ConfigureAwait(false);
        handle.CommandIssued += sql => Log(SessionLogCategory.Script, sql);
        await ProtectAsync(handle, cancellationToken).ConfigureAwait(false);
        if (Settings.TimeZone is { Length: > 0 } tz)
        {
            await TryRunAsync(handle, "SET time_zone = " + _services.Provider.ForServer(handle.Server).Dialect.StringLiteral(tz), cancellationToken).ConfigureAwait(false);
        }
        return handle;
    }

    /// <summary>
    /// Relê o estado da sessão que muda o SQL gerado (o <c>sql_mode</c>) na ligação primária e atualiza
    /// <see cref="ServerServices"/>; chamado dentro de um trabalho da primária depois de um <c>SET</c> do utilizador.
    /// </summary>
    public async Task RefreshSessionStateAsync(IDbConnectionHandle primary, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(primary);
        await primary.RefreshSessionAsync(cancellationToken).ConfigureAwait(false);
        ServerServices = _services.Provider.ForConnection(primary);
    }

    /// <summary>Cancela a operação em curso na primária (a do tab que pediu, D2).</summary>
    public void CancelPrimary() => _primary?.CancelCurrent();

    /// <summary>Muda a base da primária (<c>USE</c>), reposta depois de religar.</summary>
    public Task UseDatabaseAsync(string database, CancellationToken cancellationToken)
    {
        var dialect = ServerServices?.Dialect ?? throw new InvalidOperationException("The session is not connected.");
        return RunPrimaryAsync(async (h, ct) =>
        {
            await h.ExecuteNonQueryAsync(dialect.ChangeDatabase(database), ct).ConfigureAwait(false);
            return true;
        }, cancellationToken, applyQueryTimeout: false);
    }

    /// <summary>Desliga: cancela tudo (prazo de 3 s, 12.4) e fecha ligações e túnel.</summary>
    public async Task DisconnectAsync()
    {
        if (State is SessionState.Closed or SessionState.Closing)
        {
            return;
        }
        SetState(SessionState.Closing, null);
        _keepAlive.Change(Timeout.Infinite, Timeout.Infinite);
        var close = CloseLanesAsync();
        await Task.WhenAny(close, Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false);
        SetState(SessionState.Closed, null);
        Log(SessionLogCategory.Info, "Disconnected.");
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        await _keepAlive.DisposeAsync().ConfigureAwait(false);
        _jobSlots.Dispose();
        _tunnelGate.Dispose();
    }

    private async Task<T> WithTimeoutAsync<T>(ConnectionLane lane, Func<IDbConnectionHandle, CancellationToken, Task<T>> work, TimeSpan timeout, CancellationToken cancellationToken, TimeSpan abandonGrace)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(timeout);
        try
        {
            return await lane.RunAsync(work, WorkPriority.Visible, SessionLogCategory.UserSql, retryAfterReconnect: false, timer.Token, abandonGrace).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Query timeout of {timeout.TotalSeconds:0} seconds reached; the query was cancelled.");
        }
    }

    /// <summary>Ligação no próprio computador (nome, loopback, socket ou named pipe): sem TLS não sai da máquina.</summary>
    public static bool IsLocalHost(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var name = host.Trim();
        if (name.Length == 0 || name == "." || name.StartsWith('/') || string.Equals(name, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return System.Net.IPAddress.TryParse(name.Trim('[', ']'), out var address) && System.Net.IPAddress.IsLoopback(address);
    }

    private ConnectionLane CreateLane(ConnectionRole role, string name)
    {
        var lane = new ConnectionLane(
            role,
            name,
            OpenConnectionAsync,
            AfterOpenAsync,
            _services.Provider.TranslateException,
            Classify,
            (l, category, sql) => Log(l.Role == ConnectionRole.Primary ? category : SessionLogCategory.InternalSql, sql),
            _services.Clock);
        lane.ConnectionLost += OnConnectionLost;
        lane.Reopened += OnReopened;
        if (role == ConnectionRole.Primary)
        {
            lane.Issued += (_, sql) => Transactions.Observe(sql.Contains(';', StringComparison.Ordinal) ? _services.SplitScript(sql) : [sql]);
            lane.BusyChanged += (_, busy) =>
            {
                if (State is SessionState.Ready or SessionState.Busy)
                {
                    SetState(busy ? SessionState.Busy : SessionState.Ready, null);
                }
            };
        }
        return lane;
    }

    private static async Task<bool> PingAsync(IDbConnectionHandle handle, CancellationToken cancellationToken)
    {
        await handle.PingAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private ClassifiedError Classify(DbError error) => (ServerServices?.Errors ?? _services.Provider.Errors).Classify(error);

    private async Task<IDbConnectionHandle> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var options = Settings.Connection;
        if (options.Ssh is { } ssh)
        {
            var tunnel = await EnsureTunnelAsync(ssh, cancellationToken).ConfigureAwait(false);
            options = options with { Host = "127.0.0.1", Port = tunnel.LocalPort, Ssh = null };
        }
        return await _services.Provider.Connections.OpenAsync(options, _services.ResolveSecret, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ISshTunnel> EnsureTunnelAsync(SshTunnelOptions ssh, CancellationToken cancellationToken)
    {
        await _tunnelGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_tunnel is { IsConnected: true } open)
            {
                return open;
            }
            if (_tunnel is not null)
            {
                await _tunnel.DisposeAsync().ConfigureAwait(false);
            }
            var factory = _services.Ssh ?? throw new InvalidOperationException("SSH tunnels are not available.");
            Log(SessionLogCategory.Info, $"Opening SSH tunnel to {ssh.Host}:{ssh.Port} as {ssh.User} ...");
            var started = _services.Clock.GetTimestamp();
            _tunnel = await factory.OpenAsync(ssh, Settings.Connection.Host, Settings.Connection.Port, _services.ResolveSecret, cancellationToken).ConfigureAwait(false);
            _tunnel.Closed += (_, error) => Log(SessionLogCategory.Error, "SSH tunnel closed" + (error is null ? "." : ": " + error.Message));
            Log(SessionLogCategory.Info, $"SSH tunnel ready on local port {_tunnel.LocalPort} ({_services.Clock.GetElapsedTime(started).TotalMilliseconds:0} ms).");
            return _tunnel;
        }
        finally
        {
            _tunnelGate.Release();
        }
    }

    /// <summary>
    /// Sessão read-only: a ligação fica em transações só de leitura no servidor e com a guarda que recusa no cliente
    /// tudo o que a <see cref="ReadOnlyPolicy"/> não deixa (antes do startup script, que também passa por ela).
    /// </summary>
    private async Task ProtectAsync(IDbConnectionHandle handle, CancellationToken cancellationToken)
    {
        if (!Settings.ReadOnly)
        {
            return;
        }
        if (_services.Provider.ForServer(handle.Server).Dialect.ReadOnlySession is { } readOnly)
        {
            await TryRunAsync(handle, readOnly, cancellationToken).ConfigureAwait(false);
        }
        handle.Guard = sql =>
        {
            var statements = sql.Contains(';', StringComparison.Ordinal) ? _services.SplitScript(sql) : [sql];
            return statements.All(ReadOnlyPolicy.IsAllowed) ? null : new DbError(1792, "25006", ReadOnlyPolicy.RefusalMessage(Name));
        };
    }

    /// <summary>Estado replicado em cada ligação nova (12.2): modo read-only, base, time zone e startup script.</summary>
    private async Task AfterOpenAsync(ConnectionLane lane, IDbConnectionHandle handle, CancellationToken cancellationToken)
    {
        await ProtectAsync(handle, cancellationToken).ConfigureAwait(false);
        var dialect = _services.Provider.ForServer(handle.Server).Dialect;
        if (Settings.TimeZone is { Length: > 0 } tz)
        {
            await TryRunAsync(handle, "SET time_zone = " + dialect.StringLiteral(tz), cancellationToken).ConfigureAwait(false);
        }
        if (Settings.StartupScriptPath is { Length: > 0 } path)
        {
            if (!File.Exists(path))
            {
                Log(SessionLogCategory.Error, $"Startup script file not found: {path}");
            }
            else
            {
                var script = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                foreach (var statement in _services.SplitScript(script))
                {
                    await TryRunAsync(handle, statement, cancellationToken).ConfigureAwait(false);
                }
                // O script pode mudar o sql_mode (escaping dos literais simples).
                try
                {
                    await handle.RefreshSessionAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && _services.Provider.TranslateException(ex) is { } error && Classify(error).Category != DbErrorCategory.ConnectionLost)
                {
                    Log(SessionLogCategory.Error, $"SQL Error ({error.Code}): {error.Message}");
                }
            }
        }
        var database = lane.Role == ConnectionRole.Primary ? lane.Database : _primary?.Database ?? lane.Database;
        if (!string.IsNullOrEmpty(database))
        {
            await TryRunAsync(handle, dialect.ChangeDatabase(database), cancellationToken).ConfigureAwait(false);
            lane.Database = database;
        }
    }

    /// <summary>Erros do startup script e do replay só vão para o log (como no HeidiSQL).</summary>
    private async Task TryRunAsync(IDbConnectionHandle handle, string sql, CancellationToken cancellationToken)
    {
        try
        {
            await handle.ExecuteNonQueryAsync(sql, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && _services.Provider.TranslateException(ex) is { } error && Classify(error).Category != DbErrorCategory.ConnectionLost)
        {
            Log(SessionLogCategory.Error, $"SQL Error ({error.Code}): {error.Message}");
        }
    }

    private void OnConnectionLost(ConnectionLane lane, DbError error, Exception exception)
    {
        if (State is SessionState.Closing or SessionState.Closed)
        {
            return;
        }
        Log(SessionLogCategory.Error, $"Connection lost ({lane.Name}): {error.Message}");
        if (lane.Role == ConnectionRole.Primary)
        {
            if (Transactions.Reset())
            {
                Log(SessionLogCategory.Error,
                    "The connection was lost while a transaction with changes was open: the server rolled it back, so the changes made since the transaction started were NOT saved.");
                TransactionLost?.Invoke(this, EventArgs.Empty);
            }
            Log(SessionLogCategory.Info, "Warning: user variables, temporary tables and open transactions of the session were lost.");
        }
        if (Interlocked.Exchange(ref _reconnecting, 1) == 0)
        {
            SetState(SessionState.Reconnecting, exception);
            _ = ReconnectAsync();
        }
    }

    private void OnReopened(ConnectionLane lane)
    {
        if (lane.Role == ConnectionRole.Primary && lane.Handle is { } handle)
        {
            ServerServices = _services.Provider.ForConnection(handle);
            Transactions.Reset();
        }
        Log(SessionLogCategory.Info, $"Reconnected ({lane.Name}).");
    }

    /// <summary>Volta a ligar a primária de forma visível; os outros pedidos esperam na fila.</summary>
    private async Task ReconnectAsync()
    {
        try
        {
            var primary = _primary;
            if (primary is null)
            {
                return;
            }
            await primary.RunAsync(PingAsync,
                WorkPriority.Visible, SessionLogCategory.InternalSql, retryAfterReconnect: true, CancellationToken.None).ConfigureAwait(false);
            ConnectedAt = _services.Clock.GetUtcNow();
            SetState(SessionState.Ready, null);
        }
        catch (Exception ex)
        {
            Log(SessionLogCategory.Error, "Reconnect failed: " + ex.Message);
            SetState(SessionState.Failed, ex);
        }
        finally
        {
            Interlocked.Exchange(ref _reconnecting, 0);
        }
    }

    /// <summary>Keep-alive (TR-05): ping assíncrono nas ligações paradas há mais de "Ping every X seconds".</summary>
    private void KeepAliveTick()
    {
        if (Settings.KeepAlive is not { } interval || State is not (SessionState.Ready or SessionState.Busy))
        {
            return;
        }
        var now = _services.Clock.GetUtcNow();
        ConnectionLane?[] lanes = [_primary, _metadata == _primary ? null : _metadata];
        foreach (var lane in lanes)
        {
            if (lane is { IsOpen: true, IsBusy: false, HasPendingWork: false } && now - lane.LastActivity >= interval)
            {
                _ = lane.RunAsync(PingAsync,
                    WorkPriority.Statistics, SessionLogCategory.Debug, retryAfterReconnect: false, CancellationToken.None)
                    .ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
            }
        }
    }

    private async Task CloseLanesAsync()
    {
        _keepAlive.Change(Timeout.Infinite, Timeout.Infinite);
        List<ConnectionLane> lanes;
        lock (_gate)
        {
            lanes = [.. _jobs];
            _jobs.Clear();
        }
        if (_metadata is not null && _metadata != _primary)
        {
            lanes.Add(_metadata);
        }
        if (_primary is not null)
        {
            lanes.Add(_primary);
        }
        _metadata = null;
        _primary = null;
        foreach (var lane in lanes)
        {
            await lane.DisposeAsync().ConfigureAwait(false);
        }
        if (_tunnel is not null)
        {
            await _tunnel.DisposeAsync().ConfigureAwait(false);
            _tunnel = null;
        }
    }

    private void SetState(SessionState state, Exception? error)
    {
        SessionState previous;
        lock (_gate)
        {
            previous = _state;
            if (previous == state && error is null)
            {
                return;
            }
            _state = state;
        }
        StateChanged?.Invoke(this, new SessionStateChangedEventArgs(state, previous, error));
    }

    private void Log(SessionLogCategory category, string message) => _services.Log?.Write(Name, category, message);
}
