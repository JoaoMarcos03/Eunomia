using System.Buffers;
using System.Runtime.ExceptionServices;

namespace Eunomia.Engine.Transfer;

/// <summary>
/// Writes to another stream on a thread of its own: the caller only copies its bytes into a block and goes on, so the
/// slow writes (the compression of a ZIP entry, a slow disk or network share) run at the same time as the work that
/// produces the bytes (reading rows and turning them into SQL). At most <c>depth</c> full blocks wait to be written; a
/// caller that gets that far ahead waits for the thread. One writer at a time, like any stream.
/// </summary>
/// <remarks>
/// An error of the inner stream comes out unchanged (the same exception object: a <see cref="LocalFileException"/> is
/// still one, so it is not read as a lost connection) in the next <see cref="Write(ReadOnlySpan{byte})"/>,
/// <see cref="Flush"/> or <see cref="Stream.Dispose()"/>; the blocks after it are dropped. <see cref="Flush"/> waits until
/// everything written so far reached the inner stream; closing writes what is left and then closes the inner stream
/// (unless left open), even after an error.
/// </remarks>
public sealed class BackgroundWriteStream : Stream
{
    public const int DefaultBlockSize = 1024 * 1024;

    public const int DefaultDepth = 4;

    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private readonly int _blockSize;
    private readonly int _depth;
    private readonly Thread _thread;

    // Guarded by _gate: the blocks waiting for the thread, the one it is writing, the end of the writes and the first error.
    private readonly object _gate = new();
    private readonly Queue<(byte[] Buffer, int Count)> _queue = new();
    private bool _busy;
    private bool _closing;
    private Exception? _error;

    // Only the caller's thread: the block it fills.
    private byte[]? _block;
    private int _used;
    private bool _disposed;

    public BackgroundWriteStream(Stream inner, bool leaveOpen = false, int blockSize = DefaultBlockSize, int depth = DefaultDepth)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfLessThan(blockSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(depth, 1);
        if (!inner.CanWrite)
        {
            throw new ArgumentException("The stream is not writable.", nameof(inner));
        }
        _inner = inner;
        _leaveOpen = leaveOpen;
        _blockSize = blockSize;
        _depth = depth;
        // A background thread: a write stuck on a lost network share does not keep the application from closing.
        _thread = new Thread(WriteBlocks) { IsBackground = true, Name = "Eunomia background writer" };
        _thread.Start();
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => !_disposed;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        Write(buffer.AsSpan(offset, count));
    }

    public override void WriteByte(byte value) => Write([value]);

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfFailed();
        while (!buffer.IsEmpty)
        {
            _block ??= ArrayPool<byte>.Shared.Rent(_blockSize);
            var take = Math.Min(buffer.Length, _blockSize - _used);
            buffer[..take].CopyTo(_block.AsSpan(_used));
            _used += take;
            buffer = buffer[take..];
            if (_used == _blockSize)
            {
                Enqueue();
            }
        }
    }

    /// <summary>Waits until everything written so far reached the inner stream, and flushes it.</summary>
    public override void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_used > 0)
        {
            Enqueue();
        }
        lock (_gate)
        {
            while ((_queue.Count > 0 || _busy) && _error is null)
            {
                Monitor.Wait(_gate);
            }
        }
        ThrowIfFailed();
        // The thread waits for the next block, which only this caller can give it: the inner stream is not in use.
        _inner.Flush();
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed || !disposing)
        {
            base.Dispose(disposing);
            return;
        }
        _disposed = true;
        Exception? first = null;
        try
        {
            if (_used > 0 && Volatile.Read(ref _error) is null)
            {
                Enqueue();
            }
        }
        catch (Exception e)
        {
            first = e;
        }
        lock (_gate)
        {
            _closing = true;
            Monitor.PulseAll(_gate);
        }
        _thread.Join();
        first ??= _error;
        if (_block is not null)
        {
            ArrayPool<byte>.Shared.Return(_block);
            _block = null;
        }
        if (!_leaveOpen)
        {
            try
            {
                // Closing the inner stream writes what it still holds (the end of a ZIP entry): it can fail too.
                _inner.Dispose();
            }
            catch (Exception e)
            {
                first ??= e;
            }
        }
        base.Dispose(disposing);
        if (first is not null)
        {
            ExceptionDispatchInfo.Throw(first);
        }
    }

    /// <summary>Passes the full block to the thread; waits while <c>depth</c> blocks are already waiting.</summary>
    private void Enqueue()
    {
        var block = (_block!, _used);
        _block = null;
        _used = 0;
        lock (_gate)
        {
            while (_queue.Count >= _depth && _error is null)
            {
                Monitor.Wait(_gate);
            }
            if (_error is null)
            {
                _queue.Enqueue(block);
                Monitor.PulseAll(_gate);
                return;
            }
        }
        ArrayPool<byte>.Shared.Return(block.Item1);
        ThrowIfFailed();
    }

    private void ThrowIfFailed()
    {
        if (Volatile.Read(ref _error) is { } error)
        {
            ExceptionDispatchInfo.Throw(error);
        }
    }

    /// <summary>The thread: writes the blocks in order until the stream closes; after an error it only drops them.</summary>
    private void WriteBlocks()
    {
        while (true)
        {
            (byte[] Buffer, int Count) block;
            bool failed;
            lock (_gate)
            {
                while (_queue.Count == 0 && !_closing)
                {
                    Monitor.Wait(_gate);
                }
                if (_queue.Count == 0)
                {
                    return;
                }
                block = _queue.Dequeue();
                _busy = true;
                failed = _error is not null;
                Monitor.PulseAll(_gate);
            }
            Exception? error = null;
            try
            {
                if (!failed)
                {
                    _inner.Write(block.Buffer, 0, block.Count);
                }
            }
            catch (Exception e)
            {
                // Everything is caught: an exception that leaves the thread would end the process.
                error = e;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(block.Buffer);
            }
            lock (_gate)
            {
                _busy = false;
                if (error is not null && _error is null)
                {
                    Volatile.Write(ref _error, error);
                }
                Monitor.PulseAll(_gate);
            }
        }
    }
}
