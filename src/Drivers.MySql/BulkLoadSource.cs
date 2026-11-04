namespace Eunomia.Drivers.MySql;

/// <summary>
/// Stream of a <c>LOAD DATA LOCAL</c> that records whether a cancel cut it. MySqlConnector keeps the stream's error and
/// sends the end of the data to the server: the server gets the cut file and answers with an error of its own (on
/// MySQL 8, "Row N doesn't contain data for all columns") that no longer carries the cancel in the exception chain.
/// </summary>
internal sealed class BulkLoadSource(Stream inner) : Stream
{
    private volatile bool _cancelled;

    /// <summary>A read ended in <see cref="OperationCanceledException"/>.</summary>
    public bool Cancelled => _cancelled;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        try
        {
            return inner.Read(buffer);
        }
        catch (OperationCanceledException)
        {
            _cancelled = true;
            throw;
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _cancelled = true;
            throw;
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
