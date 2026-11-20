using System.Threading.Channels;
using Eunomia.Drivers.Abstractions;

namespace Eunomia.Engine.Execution;

/// <summary>
/// A session connection with its queue (section 12.3): the protocol only accepts one command at a time, so work is
/// serialised here, by priority. Opens the connection on demand, detects a lost connection (through the driver's
/// classifier), reopens it and retries once the work that allows it (metadata); user queries are never
/// retried on their own.
/// </summary>
internal sealed class ConnectionLane : IAsyncDisposable
{
    private readonly Channel<WorkItem>[] _queues;
    private readonly SemaphoreSlim _pending = new(0);
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>Kept apart: the token of a disposed source cannot be read (RunAsync racing DisposeAsync).</summary>
    private readonly CancellationToken _shutdownToken;
    private readonly Func<CancellationToken, Task<IDbConnectionHandle>> _open;
    private readonly Func<ConnectionLane, IDbConnectionHandle, CancellationToken, Task> _afterOpen;
    private readonly Func<Exception, DbError?> _translate;
    private readonly Func<DbError, ClassifiedError> _classify;
    private readonly Action<ConnectionLane, SessionLogCategory, string>? _log;
    private readonly Task _loop;
    private IDbConnectionHandle? _handle;
    private WorkItem? _current;
    private long _lastActivityTicks;

    public ConnectionLane(
        ConnectionRole role,
        string name,
        Func<CancellationToken, Task<IDbConnectionHandle>> open,
        Func<ConnectionLane, IDbConnectionHandle, CancellationToken, Task> afterOpen,
        Func<Exception, DbError?> translate,
        Func<DbError, ClassifiedError> classify,
        Action<ConnectionLane, SessionLogCategory, string>? log,
        TimeProvider clock)
    {
        Role = role;
        Name = name;
        _open = open;
        _afterOpen = afterOpen;
        _translate = translate;
        _classify = classify;
        _log = log;
        Clock = clock;
        _queues = [Channel.CreateUnbounded<WorkItem>(), Channel.CreateUnbounded<WorkItem>(), Channel.CreateUnbounded<WorkItem>()];
        _shutdownToken = _shutdown.Token;
        Touch();
        _loop = Task.Run(LoopAsync);
    }

    public ConnectionRole Role { get; }

    public string Name { get; }

    public TimeProvider Clock { get; }

    public IDbConnectionHandle? Handle => _handle;

    public bool IsOpen => _handle?.IsOpen == true;

    public bool IsBusy => Volatile.Read(ref _current) is not null;

    public bool HasPendingWork => _queues.Any(q => q.Reader.Count > 0);

    /// <summary>Database in use on this connection (restored with <c>USE</c> after reconnecting).</summary>
    public string? Database { get; set; }

    public DateTimeOffset LastActivity => new(Interlocked.Read(ref _lastActivityTicks), TimeSpan.Zero);

    /// <summary>The connection dropped (before any reopening).</summary>
    public event Action<ConnectionLane, DbError, Exception>? ConnectionLost;

    /// <summary>The connection was reopened after dropping.</summary>
    public event Action<ConnectionLane>? Reopened;

    /// <summary>Statement sent through the connection (all of them, even those that do not go to the log).</summary>
    public event Action<ConnectionLane, string>? Issued;

    /// <summary>The operation in progress changed (the SessionHost moves to Busy and back to Ready).</summary>
    public event Action<ConnectionLane, bool>? BusyChanged;

    /// <summary>
    /// Queues the work. Cancelled by the caller while waiting, it does not run; already running, with
    /// <paramref name="abandonGrace"/> it still has that much time to finish before the command is cancelled on the server
    /// (the result is discarded anyway). Cancelling a short read with <c>KILL QUERY</c> opens another connection and costs
    /// more than letting it finish. Esc (<see cref="CancelCurrent"/>) and closing cancel immediately. Queued on a closed (or
    /// closing) connection, the task is cancelled.
    /// </summary>
    public Task<T> RunAsync<T>(
        Func<IDbConnectionHandle, CancellationToken, Task<T>> work,
        WorkPriority priority,
        SessionLogCategory logAs,
        bool retryAfterReconnect,
        CancellationToken cancellationToken,
        TimeSpan abandonGrace = default)
    {
        if (_shutdownToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(_shutdownToken);
        }
        var item = new WorkItem<T>(work, logAs, retryAfterReconnect, cancellationToken, _shutdownToken, abandonGrace);
        if (!_queues[(int)priority].Writer.TryWrite(item))
        {
            item.Dispose();
            return Task.FromCanceled<T>(_shutdownToken.IsCancellationRequested ? _shutdownToken : new CancellationToken(true));
        }
        try
        {
            _pending.Release();
        }
        catch (ObjectDisposedException)
        {
            // Closed between the write and here: the loop is gone and the shutdown already cancelled the item.
            item.SetCanceled();
            item.Dispose();
        }
        return item.Task;
    }

    /// <summary>
    /// Pings with a deadline (keep-alive and reconnect): a connection that does not answer within <paramref name="timeout"/>
    /// (a dropped VPN, without a TCP reset) is treated as lost instead of blocking the queue forever.
    /// </summary>
    public Task<bool> PingAsync(TimeSpan timeout, WorkPriority priority, SessionLogCategory logAs, bool retryAfterReconnect, CancellationToken cancellationToken) =>
        RunAsync((h, ct) => PingWithTimeoutAsync(h, timeout, Clock, ct), priority, logAs, retryAfterReconnect, cancellationToken);

    private static async Task<bool> PingWithTimeoutAsync(IDbConnectionHandle handle, TimeSpan timeout, TimeProvider clock, CancellationToken cancellationToken)
    {
        using var ping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var task = handle.PingAsync(ping.Token);
        try
        {
            await task.WaitAsync(timeout, clock, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            await ping.CancelAsync().ConfigureAwait(false);
            // The abandoned ping fails when the connection is closed: observed so it does not go unobserved.
            _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw new ConnectionStalledException(new DbError(2013, "HY000",
                $"The server did not answer a ping within {timeout.TotalSeconds:0} seconds (the network may have dropped)."));
        }
    }

    /// <summary>Cancels the operation in progress (the driver sends <c>KILL QUERY</c>, section 12.4).</summary>
    public void CancelCurrent() => Volatile.Read(ref _current)?.Cancel();

    /// <summary>Opens now (connecting the session); returns the open connection.</summary>
    public Task<IDbConnectionHandle> OpenNowAsync(CancellationToken cancellationToken) =>
        RunAsync((h, _) => Task.FromResult(h), WorkPriority.Visible, SessionLogCategory.InternalSql, retryAfterReconnect: false, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        foreach (var q in _queues)
        {
            q.Writer.TryComplete();
        }
        CancelCurrent();
        await _shutdown.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        await DropHandleAsync().ConfigureAwait(false);
        _shutdown.Dispose();
        _pending.Dispose();
    }

    private void Touch() => Interlocked.Exchange(ref _lastActivityTicks, Clock.GetUtcNow().UtcTicks);

    private async Task LoopAsync()
    {
        while (true)
        {
            try
            {
                await _pending.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            WorkItem? item = null;
            foreach (var q in _queues)
            {
                if (q.Reader.TryRead(out item))
                {
                    break;
                }
            }
            if (item is null)
            {
                continue;
            }
            if (!item.TryStart() || item.Token.IsCancellationRequested)
            {
                item.SetCanceled();
                item.Dispose();
                continue;
            }
            Volatile.Write(ref _current, item);
            BusyChanged?.Invoke(this, true);
            try
            {
                await ExecuteAsync(item).ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref _current, null);
                Touch();
                BusyChanged?.Invoke(this, false);
                item.Dispose();
            }
        }
        // Work left in the queue at closing.
        foreach (var q in _queues)
        {
            while (q.Reader.TryRead(out var left))
            {
                left.SetCanceled();
                left.Dispose();
            }
        }
    }

    private async Task ExecuteAsync(WorkItem item)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var handle = await EnsureOpenAsync(item.Token).ConfigureAwait(false);
                _currentCategory = item.LogAs;
                await item.RunAsync(handle).ConfigureAwait(false);
                if (handle.Database is { } db)
                {
                    Database = db;
                }
                return;
            }
            catch (OperationCanceledException) when (item.Token.IsCancellationRequested)
            {
                item.SetCanceled();
                return;
            }
            catch (Exception ex) when (ex is not SessionConnectionLostException)
            {
                var stalled = ex as ConnectionStalledException;
                var error = stalled?.Error ?? _translate(ex);
                if (error is not null && (stalled is not null || _classify(error).Category == DbErrorCategory.ConnectionLost))
                {
                    var wasOpen = _handle is not null;
                    await DropHandleAsync().ConfigureAwait(false);
                    if (wasOpen)
                    {
                        ConnectionLost?.Invoke(this, error, ex);
                    }
                    if (item.RetryAfterReconnect && attempt == 0)
                    {
                        continue;
                    }
                    item.SetException(new SessionConnectionLostException(
                        wasOpen ? $"Connection to the server was lost ({error.Message})." : error.Message, error, ex));
                    return;
                }
                item.SetException(error is not null ? new SessionOperationException(error, _classify(error), ex) : ex);
                return;
            }
        }
    }

    private SessionLogCategory _currentCategory = SessionLogCategory.InternalSql;

    private async Task<IDbConnectionHandle> EnsureOpenAsync(CancellationToken cancellationToken)
    {
        if (_handle is { IsOpen: true } open)
        {
            return open;
        }
        var reopening = _handle is not null || Database is not null;
        await DropHandleAsync().ConfigureAwait(false);
        var handle = await _open(cancellationToken).ConfigureAwait(false);
        handle.CommandIssued += sql =>
        {
            if (_currentCategory != SessionLogCategory.None)
            {
                _log?.Invoke(this, _currentCategory, sql);
            }
            Issued?.Invoke(this, sql);
        };
        _currentCategory = SessionLogCategory.InternalSql;
        try
        {
            await _afterOpen(this, handle, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Only a fully prepared connection is kept: one cancelled before the read-only guard, the time zone or the USE
            // would be reused by the next work as if it had them.
            await DisposeQuietlyAsync(handle).ConfigureAwait(false);
            throw;
        }
        _handle = handle;
        if (reopening)
        {
            Reopened?.Invoke(this);
        }
        return handle;
    }

    private async Task DropHandleAsync()
    {
        var handle = Interlocked.Exchange(ref _handle, null);
        if (handle is not null)
        {
            await DisposeQuietlyAsync(handle).ConfigureAwait(false);
        }
    }

    private static async Task DisposeQuietlyAsync(IDbConnectionHandle handle)
    {
        try
        {
            await handle.DisposeAsync().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Closing an already broken connection cannot fail the operation.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>The connection stopped answering (ping deadline): handled as a lost connection.</summary>
    private sealed class ConnectionStalledException(DbError error) : Exception(error.Message)
    {
        public DbError Error { get; } = error;
    }

    private abstract class WorkItem : IDisposable
    {
        /// <summary>The result is no longer of interest (the caller, Esc, closing).</summary>
        private readonly CancellationTokenSource _cts;

        /// <summary>What the operation receives: cancels the command on the server.</summary>
        private readonly CancellationTokenSource _work = new();
        private readonly CancellationTokenRegistration _abandoned;
        private readonly CancellationTokenRegistration _closing;
        private readonly CancellationTokenRegistration _dropped;

        /// <summary>0 waiting in the queue, 1 taken to run, 2 cancelled while it waited.</summary>
        private int _state;

        protected WorkItem(SessionLogCategory logAs, bool retryAfterReconnect, CancellationToken caller, CancellationToken shutdown, TimeSpan abandonGrace)
        {
            LogAs = logAs;
            RetryAfterReconnect = retryAfterReconnect;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(caller, shutdown);
            _abandoned = caller.Register(() => CancelWork(abandonGrace));
            _closing = shutdown.Register(() => CancelWork(TimeSpan.Zero));
            // Cancelled while it waits (behind a long query): the caller gets it now, not when the connection is free.
            _dropped = _cts.Token.Register(() =>
            {
                if (Interlocked.CompareExchange(ref _state, 2, 0) == 0)
                {
                    SetCanceled();
                }
            });
        }

        /// <summary>Takes the item from the queue to run it; <c>false</c> when it was cancelled while it waited.</summary>
        public bool TryStart() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;

        public SessionLogCategory LogAs { get; }

        public bool RetryAfterReconnect { get; }

        public CancellationToken Token => _cts.Token;

        protected CancellationToken WorkToken => _work.Token;

        /// <summary>Esc: the result is of no interest and the command is cancelled now.</summary>
        public void Cancel()
        {
            try
            {
                _cts.Cancel();
                _work.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void CancelWork(TimeSpan grace)
        {
            try
            {
                if (grace > TimeSpan.Zero)
                {
                    _work.CancelAfter(grace);
                }
                else
                {
                    _work.Cancel();
                }
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public abstract Task RunAsync(IDbConnectionHandle handle);

        public abstract void SetCanceled();

        public abstract void SetException(Exception exception);

        public void Dispose()
        {
            _dropped.Dispose();
            _abandoned.Dispose();
            _closing.Dispose();
            _cts.Dispose();
            _work.Dispose();
        }
    }

    private sealed class WorkItem<T>(
        Func<IDbConnectionHandle, CancellationToken, Task<T>> work,
        SessionLogCategory logAs,
        bool retryAfterReconnect,
        CancellationToken caller,
        CancellationToken shutdown,
        TimeSpan abandonGrace) : WorkItem(logAs, retryAfterReconnect, caller, shutdown, abandonGrace)
    {
        private readonly TaskCompletionSource<T> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T> Task => _tcs.Task;

        public override async Task RunAsync(IDbConnectionHandle handle)
        {
            var result = await work(handle, WorkToken).ConfigureAwait(false);
            if (Token.IsCancellationRequested)
            {
                // Cancelled and the server answered without an error (on MySQL an interrupted SLEEP() returns 1; MariaDB gives
                // error 1317): the result is discarded, as with any cancellation.
                SetCanceled();
                return;
            }
            _tcs.TrySetResult(result);
        }

        public override void SetCanceled() => _tcs.TrySetCanceled(Token.IsCancellationRequested ? Token : new CancellationToken(true));

        public override void SetException(Exception exception) => _tcs.TrySetException(exception);
    }
}
