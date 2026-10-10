using Eunomia.Drivers.Abstractions;

namespace Eunomia.Engine.Execution;

/// <summary>SessionHost dependencies (wired in the composition root).</summary>
public sealed class SessionServices
{
    public required IDbProvider Provider { get; init; }

    /// <summary>SSH tunnels (Infra.Ssh); only required for sessions with SSH.</summary>
    public ISshTunnelFactory? Ssh { get; init; }

    /// <summary>Reads a secret from the vault at opening time (it is never kept in the SessionHost).</summary>
    public required Func<SecretReference, CancellationToken, ValueTask<string>> ResolveSecret { get; init; }

    public ISessionLog? Log { get; init; }

    /// <summary>Splits the startup script into statements (the Engine.Sql splitter).</summary>
    public Func<string, IReadOnlyList<string>> SplitScript { get; init; } = s => [s];

    public TimeProvider Clock { get; init; } = TimeProvider.System;
}

/// <summary>
/// An open session, as an isolated actor (section 12): a primary connection for the user's work, a metadata connection
/// for the tree and the autocomplete, job connections on demand; each one with its own queue. Keeps the SSH tunnel,
/// does keep-alive, detects lost connections and reconnects visibly (Reconnecting state, messages in the log,
/// replay of <c>USE</c>, time zone and startup script). "Reconnecting" and "Failed" never block other sessions.
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
    /// TLS cipher of the primary connection, <c>""</c> without TLS, <c>null</c> if unknown (23.2: a connection without
    /// verification is made visible).
    /// </summary>
    public string? Encryption => _primary?.Handle?.Encryption;

    /// <summary>The session goes through an SSH tunnel (data is encrypted up to the SSH server, even without TLS).</summary>
    public bool ThroughSsh => Settings.Connection.Ssh is not null;

    /// <summary>Id of the primary connection on the server (<c>CONNECTION_ID()</c>).</summary>
    public long? PrimaryConnectionId => _primary?.Handle?.ServerConnectionId;

    /// <summary>Server ids of every open connection of the session (primary, metadata and jobs).</summary>
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

    /// <summary>Database in use on the primary connection.</summary>
    public string? CurrentDatabase => _primary?.Database;

    /// <summary>The primary connection has a user operation running.</summary>
    public bool IsPrimaryBusy => _primary?.IsBusy == true;

    /// <summary>Transaction state of the primary connection (section 14.5), tracked from the statements it sends.</summary>
    public TransactionTracker Transactions { get; } = new();

    /// <summary>
    /// The primary connection dropped with an open transaction holding changes: the server rolled it back and those changes were lost.
    /// </summary>
    public event EventHandler? TransactionLost;

    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Connects (opens the SSH tunnel, if any, and the primary connection); the metadata one opens next. Errors (host key to
    /// confirm, wrong password, server down) reach the caller and the session moves to Failed.
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
    /// User work on the primary connection (queries, edits, DDL), with the client timeout (D8).
    /// <paramref name="abandonGrace"/>: see <see cref="ConnectionLane.RunAsync{T}"/> (reads abandoned when
    /// navigating).
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

    /// <summary>Metadata (tree, columns, counts); retried once after reconnecting.</summary>
    public Task<T> RunMetadataAsync<T>(Func<IDbConnectionHandle, CancellationToken, Task<T>> work, CancellationToken cancellationToken, WorkPriority priority = WorkPriority.Visible, TimeSpan abandonGrace = default)
    {
        var lane = _metadata ?? throw new InvalidOperationException("The session is not connected.");
        return lane.RunAsync(work, priority, SessionLogCategory.InternalSql, retryAfterReconnect: true, cancellationToken, abandonGrace);
    }

    /// <summary>
    /// Heavy work (export, import) on a job connection; at most <see cref="SessionConnectionSettings.MaxJobConnections"/>
    /// at the same time, the others wait in the queue (12.6). With <paramref name="logCommands"/> set to <c>false</c>, the
    /// commands do not go to the SQL log.
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
    /// Error of a statement inside a unit of work (driver exceptions are only translated when they leave it): the
    /// <see cref="DbError"/>, or <c>null</c> if the exception does not come from the server, is a cancel or the connection dropped.
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
    /// A connection of its own, outside the queues (the CSV import's <c>LOAD DATA LOCAL</c>, with options the other
    /// connections do not have): the same tunnel and the same options adjusted by <paramref name="adjust"/>, with the session's
    /// time zone and the commands in the SQL log. The caller closes it.
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
    /// Reads again the session state that changes the generated SQL (<c>sql_mode</c>) on the primary connection and updates
    /// <see cref="ServerServices"/>; called inside a unit of work on the primary connection after a user <c>SET</c>.
    /// </summary>
    public async Task RefreshSessionStateAsync(IDbConnectionHandle primary, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(primary);
        await primary.RefreshSessionAsync(cancellationToken).ConfigureAwait(false);
        ServerServices = _services.Provider.ForConnection(primary);
    }

    /// <summary>Cancels the operation in progress on the primary connection (the one from the tab that asked, D2).</summary>
    public void CancelPrimary() => _primary?.CancelCurrent();

    /// <summary>Changes the primary connection's database (<c>USE</c>), restored after reconnecting.</summary>
    public Task UseDatabaseAsync(string database, CancellationToken cancellationToken)
    {
        var dialect = ServerServices?.Dialect ?? throw new InvalidOperationException("The session is not connected.");
        return RunPrimaryAsync(async (h, ct) =>
        {
            await h.ExecuteNonQueryAsync(dialect.ChangeDatabase(database), ct).ConfigureAwait(false);
            return true;
        }, cancellationToken, applyQueryTimeout: false);
    }

    /// <summary>Disconnects: cancels everything (3 s deadline, 12.4) and closes the connections and the tunnel.</summary>
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

    /// <summary>Connection to this computer (name, loopback, socket or named pipe): without TLS it does not leave the machine.</summary>
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
    /// Read-only session: the connection uses read-only transactions on the server and the guard that refuses on the client
    /// everything <see cref="ReadOnlyPolicy"/> does not allow (before the startup script, which also goes through it).
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

    /// <summary>State replayed on every new connection (12.2): read-only mode, database, time zone and startup script.</summary>
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
                // The script may change sql_mode (escaping of simple literals).
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

    /// <summary>Errors from the startup script and the replay only go to the log (as in HeidiSQL).</summary>
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

    /// <summary>Reconnects the primary connection visibly; the other requests wait in the queue.</summary>
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

    /// <summary>Keep-alive (TR-05): asynchronous ping on connections idle for longer than "Ping every X seconds".</summary>
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
