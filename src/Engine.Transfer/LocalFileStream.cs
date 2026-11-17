namespace Eunomia.Engine.Transfer;

/// <summary>
/// A <see cref="FileStream"/> whose I/O errors come out as <see cref="LocalFileException"/>: for the files transfers read
/// or write inside a job connection (see there why). Every read and write is passed straight to the file.
/// </summary>
public sealed class LocalFileStream : Stream
{
    private readonly FileStream _file;

    private LocalFileStream(FileStream file) => _file = file;

    /// <summary>Opens the file like the <see cref="FileStream"/> constructor; a file that does not open is a <see cref="LocalFileException"/> too.</summary>
    public static LocalFileStream Open(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize = 4096, FileOptions options = FileOptions.None) =>
        new(LocalFileException.Guard(() => new FileStream(path, mode, access, share, bufferSize, options)));

    public string Name => _file.Name;

    public override bool CanRead => _file.CanRead;

    public override bool CanSeek => _file.CanSeek;

    public override bool CanWrite => _file.CanWrite;

    public override long Length
    {
        get
        {
            try
            {
                return _file.Length;
            }
            catch (IOException e)
            {
                throw new LocalFileException(e);
            }
        }
    }

    public override long Position
    {
        get => _file.Position;
        set
        {
            try
            {
                _file.Position = value;
            }
            catch (IOException e)
            {
                throw new LocalFileException(e);
            }
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        try
        {
            return _file.Read(buffer);
        }
        catch (IOException e)
        {
            throw new LocalFileException(e);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _file.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException e)
        {
            throw new LocalFileException(e);
        }
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        try
        {
            _file.Write(buffer);
        }
        catch (IOException e)
        {
            throw new LocalFileException(e);
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            await _file.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException e)
        {
            throw new LocalFileException(e);
        }
    }

    public override void Flush()
    {
        try
        {
            _file.Flush();
        }
        catch (IOException e)
        {
            throw new LocalFileException(e);
        }
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _file.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException e)
        {
            throw new LocalFileException(e);
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        try
        {
            return _file.Seek(offset, origin);
        }
        catch (IOException e)
        {
            throw new LocalFileException(e);
        }
    }

    public override void SetLength(long value)
    {
        try
        {
            _file.SetLength(value);
        }
        catch (IOException e)
        {
            throw new LocalFileException(e);
        }
    }

    /// <summary>Closing writes what the file still buffers, so it can fail too (a full disk).</summary>
    protected override void Dispose(bool disposing)
    {
        try
        {
            if (disposing)
            {
                _file.Dispose();
            }
        }
        catch (IOException e)
        {
            throw new LocalFileException(e);
        }
        finally
        {
            base.Dispose(disposing);
        }
    }
}
