namespace Eunomia.Drivers.MySql.Tests;

/// <summary>
/// Cancel in the middle of a <c>LOAD DATA LOCAL</c>: MySqlConnector wraps the cancel read from the stream in a
/// <c>LOAD DATA</c> error; the driver returns it as a cancellation (cancelling is not an error).
/// </summary>
public sealed class BulkLoadCancelTests
{
    [Fact]
    public void WrappedCancelCountsAsCancellationOnlyWhenRequested()
    {
        using var cts = new CancellationTokenSource();
        var wrapped = new InvalidOperationException("Error during LOAD DATA LOCAL INFILE", new OperationCanceledException());
        var deeper = new InvalidOperationException("outer", new IOException("io", new TaskCanceledException()));

        Assert.False(MySqlConnectionHandle.IsCancellation(wrapped, cts.Token));

        cts.Cancel();
        Assert.True(MySqlConnectionHandle.IsCancellation(wrapped, cts.Token));
        Assert.True(MySqlConnectionHandle.IsCancellation(deeper, cts.Token));
        Assert.False(MySqlConnectionHandle.IsCancellation(new InvalidOperationException("Duplicate entry '1' for key 'PRIMARY'"), cts.Token));
    }

    [Fact]
    public async Task StreamRecordsThatTheCancelCutIt()
    {
        using var cts = new CancellationTokenSource();
        await using var source = new BulkLoadSource(new CancelOnSecondRead(cts));
        var buffer = new byte[16];

        Assert.Equal(1, await source.ReadAsync(buffer, Ct));
        Assert.False(source.Cancelled);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.ReadAsync(buffer, Ct).AsTask());
        Assert.True(source.Cancelled);
    }

    [Fact]
    public void StreamLidoAteAoFimNaoContaComoCancelado()
    {
        using var source = new BulkLoadSource(new MemoryStream([1, 2, 3]));
        var buffer = new byte[16];

        Assert.Equal(3, source.Read(buffer, 0, buffer.Length));
        Assert.Equal(0, source.Read(buffer, 0, buffer.Length));
        Assert.False(source.Cancelled);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>One byte on the first read; the second cancels and throws, like the CSV import stream.</summary>
    private sealed class CancelOnSecondRead(CancellationTokenSource cancel) : MemoryStream([1, 2, 3])
    {
        private int _reads;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (++_reads > 1)
            {
                cancel.Cancel();
                cancel.Token.ThrowIfCancellationRequested();
            }
            return base.ReadAsync(buffer[..1], cancellationToken);
        }
    }
}
