using Eunomia.Drivers.Abstractions;

namespace Eunomia.Engine.Execution.Tests;

/// <summary>Connection lane: only a fully prepared connection is kept, a ping that never answers, closing.</summary>
public sealed class ConnectionLaneTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ConnectionCancelledDuringAfterOpenIsNotReused()
    {
        var opened = new List<FakeHandle>();
        var afterOpenCalls = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var lane = NewLane(opened, async (_, _, ct) =>
        {
            if (Interlocked.Increment(ref afterOpenCalls) == 1)
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
        });

        using var cts = new CancellationTokenSource();
        var first = lane.RunAsync((h, _) => Task.FromResult(h), WorkPriority.Visible, SessionLogCategory.InternalSql, false, cts.Token);
        await started.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(Wait, TestContext.Current.CancellationToken));

        var handle = await lane.RunAsync((h, _) => Task.FromResult(h), WorkPriority.Visible, SessionLogCategory.InternalSql, false, TestContext.Current.CancellationToken)
            .WaitAsync(Wait, TestContext.Current.CancellationToken);

        Assert.Equal(2, opened.Count);
        Assert.Same(opened[1], handle);
        Assert.Equal(2, afterOpenCalls);
        Assert.True(opened[0].Disposed);
        Assert.False(opened[1].Disposed);
    }

    [Fact]
    public async Task ConnectionWhoseAfterOpenFailsIsDisposed()
    {
        var opened = new List<FakeHandle>();
        var calls = 0;
        await using var lane = NewLane(opened, (_, _, _) =>
            Interlocked.Increment(ref calls) == 1 ? Task.FromException(new InvalidOperationException("guard")) : Task.CompletedTask);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            lane.RunAsync((h, _) => Task.FromResult(h), WorkPriority.Visible, SessionLogCategory.InternalSql, false, TestContext.Current.CancellationToken)
                .WaitAsync(Wait, TestContext.Current.CancellationToken));
        Assert.Null(lane.Handle);
        Assert.True(opened[0].Disposed);

        var handle = await lane.RunAsync((h, _) => Task.FromResult(h), WorkPriority.Visible, SessionLogCategory.InternalSql, false, TestContext.Current.CancellationToken)
            .WaitAsync(Wait, TestContext.Current.CancellationToken);
        Assert.Same(opened[1], handle);
    }

    [Fact]
    public async Task PingThatNeverAnswersCountsAsALostConnection()
    {
        var opened = new List<FakeHandle>();
        await using var lane = NewLane(opened, (_, _, _) => Task.CompletedTask, hangingPings: 1);
        DbError? lost = null;
        lane.ConnectionLost += (_, error, _) => lost = error;
        await lane.OpenNowAsync(TestContext.Current.CancellationToken).WaitAsync(Wait, TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<SessionConnectionLostException>(() =>
            lane.PingAsync(TimeSpan.FromMilliseconds(200), WorkPriority.Statistics, SessionLogCategory.Debug, false, TestContext.Current.CancellationToken)
                .WaitAsync(Wait, TestContext.Current.CancellationToken));

        Assert.Equal(2013, ex.Error.Code);
        Assert.NotNull(lost);
        Assert.True(opened[0].Disposed);
        // The queue is not blocked: the next work opens another connection.
        Assert.True(await lane.PingAsync(TimeSpan.FromSeconds(5), WorkPriority.Visible, SessionLogCategory.Debug, false, TestContext.Current.CancellationToken)
            .WaitAsync(Wait, TestContext.Current.CancellationToken));
        Assert.Equal(2, opened.Count);
    }

    [Fact]
    public async Task WorkQueuedAfterClosingIsCancelled()
    {
        var lane = NewLane([], (_, _, _) => Task.CompletedTask);
        await lane.DisposeAsync();

        var work = lane.RunAsync((_, _) => Task.FromResult(1), WorkPriority.Visible, SessionLogCategory.InternalSql, false, TestContext.Current.CancellationToken);

        Assert.True(work.IsCanceled);
    }

    private static ConnectionLane NewLane(
        List<FakeHandle> opened,
        Func<ConnectionLane, IDbConnectionHandle, CancellationToken, Task> afterOpen,
        int hangingPings = 0) =>
        new(
            ConnectionRole.Primary,
            "test",
            _ =>
            {
                lock (opened)
                {
                    var handle = new FakeHandle(hangingPings-- > 0);
                    opened.Add(handle);
                    return Task.FromResult<IDbConnectionHandle>(handle);
                }
            },
            afterOpen,
            _ => null,
            _ => new ClassifiedError(DbErrorCategory.ConnectionLost, true, false, null),
            null,
            TimeProvider.System);

    private sealed class FakeHandle(bool pingHangs) : IDbConnectionHandle
    {
        public bool Disposed { get; private set; }

        public bool IsOpen => !Disposed;

        public Func<string, DbError?>? Guard { get; set; }

        public string? Database => null;

        public ServerIdentity Server => throw new NotImplementedException();

        public long ServerConnectionId => 1;

        public IQueryExecutor Executor => throw new NotImplementedException();

        public IMetadataProvider Metadata => throw new NotImplementedException();

        public event Action<string>? CommandIssued
        {
            add { }
            remove { }
        }

        // A dropped network without a TCP reset: the ping ignores cancellation and never returns.
        public Task PingAsync(CancellationToken cancellationToken) => pingHangs ? new TaskCompletionSource().Task : Task.CompletedTask;

        public Task CancelCurrentAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<int> ExecuteNonQueryAsync(string sql, CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<object?> ExecuteScalarAsync(string sql, CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<BulkLoadResult> BulkLoadAsync(BulkLoadRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
