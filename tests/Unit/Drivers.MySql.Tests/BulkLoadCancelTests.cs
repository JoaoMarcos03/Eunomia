namespace Eunomia.Drivers.MySql.Tests;

/// <summary>
/// Cancel a meio de um <c>LOAD DATA LOCAL</c>: o MySqlConnector embrulha o cancel lido do stream num erro do
/// <c>LOAD DATA</c>; o driver devolve-o como cancelamento (cancelar não é erro).
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
