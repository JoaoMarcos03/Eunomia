namespace Eunomia.Drivers.MySql.Tests;

/// <summary>
/// Cancel in the middle of a <c>LOAD DATA LOCAL</c>: MySqlConnector wraps the cancel read from the stream in a
/// <c>LOAD DATA</c> error; the driver returns it as a cancellation (cancelling is not an error).
/// </summary>
public sealed class BulkLoadCancelTests
{
    [Fact]
    public void CancelEmbrulhadoContaComoCancelamentoSoComOCancelPedido()
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
}
