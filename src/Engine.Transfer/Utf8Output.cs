using System.Text;

namespace Eunomia.Engine.Transfer;

/// <summary>
/// UTF-8 writing with its own buffer (section 13.1: 1 MB buffer, reused encoder): text is encoded
/// straight into the buffer and the stream only gets large writes. Not thread-safe; <see cref="BytesWritten"/> can be
/// read from another thread for progress.
/// </summary>
public sealed class Utf8Output : IDisposable
{
    public const int DefaultBufferSize = 1024 * 1024;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly byte[] _buffer;
    private int _used;
    private long _flushed;

    public Utf8Output(Stream stream, int bufferSize = DefaultBufferSize, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, 64);
        _stream = stream;
        _leaveOpen = leaveOpen;
        _buffer = new byte[bufferSize];
    }

    /// <summary>Bytes written so far, including those still in the buffer.</summary>
    public long BytesWritten => Volatile.Read(ref _flushed) + _used;

    public void Write(string text) => Write(text.AsSpan());

    public void Write(ReadOnlySpan<char> text)
    {
        while (!text.IsEmpty)
        {
            // In the worst case each character takes 3 bytes; a surrogate pair takes 4 for 2 characters.
            var room = _buffer.Length - _used;
            if (room < 4)
            {
                Flush();
                room = _buffer.Length;
            }
            var take = Math.Min(text.Length, room / 3);
            if (take < text.Length && take > 0 && char.IsHighSurrogate(text[take - 1]))
            {
                take--;
            }
            if (take == 0)
            {
                take = Math.Min(2, text.Length);
            }
            _used += Encoding.UTF8.GetBytes(text[..take], _buffer.AsSpan(_used));
            text = text[take..];
        }
    }

    public void Write(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > _buffer.Length - _used)
        {
            Flush();
            if (bytes.Length >= _buffer.Length)
            {
                _stream.Write(bytes);
                Volatile.Write(ref _flushed, _flushed + bytes.Length);
                return;
            }
        }
        bytes.CopyTo(_buffer.AsSpan(_used));
        _used += bytes.Length;
    }

    public void Write(byte value)
    {
        if (_used == _buffer.Length)
        {
            Flush();
        }
        _buffer[_used++] = value;
    }

    /// <summary>Contiguous space to write at least <paramref name="size"/> bytes (up to the buffer size).</summary>
    public Span<byte> GetSpan(int size)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, _buffer.Length);
        if (_buffer.Length - _used < size)
        {
            Flush();
        }
        return _buffer.AsSpan(_used);
    }

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _buffer.Length - _used);
        _used += count;
    }

    /// <summary>Passes the buffer to the stream (without a stream <c>Flush</c>).</summary>
    public void Flush()
    {
        if (_used == 0)
        {
            return;
        }
        _stream.Write(_buffer, 0, _used);
        Volatile.Write(ref _flushed, _flushed + _used);
        _used = 0;
    }

    public void Dispose()
    {
        Flush();
        _stream.Flush();
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }
    }
}
