using System.Globalization;
using System.Text;
using Eunomia.App.Export;
using Eunomia.App.Query;
using Eunomia.Engine.Transfer;

namespace Eunomia.App.Transfer;

/// <summary>Encoding of a file read in streaming (Run SQL file and CSV import; section 13.1: BOM, otherwise the chosen one).</summary>
internal static class TextFileEncoding
{
    /// <summary>BOM (always wins), otherwise the chosen encoding; "Auto detect": UTF-8 if the start is valid UTF-8, otherwise ANSI.</summary>
    public static (string Encoding, int BomLength) Detect(ReadOnlySpan<byte> head, string encoding)
    {
        if (head.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return ("UTF-8", 3);
        }
        if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return ("Unicode", 2);
        }
        if (head.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return ("Unicode Big Endian", 2);
        }
        if (encoding != SqlFile.AutoDetect)
        {
            return (encoding == "UTF-8-BOM" ? "UTF-8" : encoding, 0);
        }
        // A multibyte character cut at the end of the block does not count as invalid.
        var decoder = new UTF8Encoding(false, throwOnInvalidBytes: true).GetDecoder();
        try
        {
            _ = decoder.GetCharCount(head, flush: false);
            return ("UTF-8", 0);
        }
        catch (DecoderFallbackException)
        {
            return ("ANSI", 0);
        }
    }

    /// <summary>Encoding without a BOM (the BOM was already skipped).</summary>
    public static Encoding For(string name) => name switch
    {
        "UTF-8" or "UTF-8-BOM" => new UTF8Encoding(false),
        "Unicode" => new UnicodeEncoding(bigEndian: false, byteOrderMark: false),
        "Unicode Big Endian" => new UnicodeEncoding(bigEndian: true, byteOrderMark: false),
        _ => ExportFile.EncodingOf(name),
    };

    /// <summary>Opens the file after the BOM and reports the encoding used; its read errors are <see cref="LocalFileException"/>s (they happen inside the import's connection).</summary>
    public static async Task<(LocalFileStream Stream, string Encoding)> OpenAsync(string path, string encoding, CancellationToken cancellationToken)
    {
        var stream = LocalFileStream.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
        try
        {
            var head = new byte[(int)Math.Min(1024 * 1024, stream.Length)];
            var read = await stream.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            var (name, bom) = Detect(head.AsSpan(0, read), encoding);
            stream.Seek(bom, SeekOrigin.Begin);
            return (stream, name);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>
/// Read stream that checks that every byte passing through is valid UTF-8 (Auto detect only looks at the first 1 MB, and
/// a lenient decoder turned a Latin-1 byte further on into U+FFFD, or the server cut the value). The first invalid
/// sequence fails the read with <see cref="InvalidDataException"/>, naming its line and byte offset in the file;
/// <see cref="Error"/> keeps it for when a reader (MySqlConnector's LOAD DATA) replaces it with an error of its own.
/// </summary>
internal sealed class Utf8ValidatingStream(Stream inner, long startOffset) : Stream
{
    private readonly Decoder _decoder = new UTF8Encoding(false, throwOnInvalidBytes: true).GetDecoder();
    private readonly char[] _chars = new char[4096];
    private long _offset = startOffset;
    private long _line = 1;

    /// <summary>The invalid UTF-8 found, if any.</summary>
    public InvalidDataException? Error { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var n = inner.Read(buffer);
        Validate(buffer[..n]);
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Validate(buffer.Span[..n]);
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private void Validate(ReadOnlySpan<byte> bytes)
    {
        if (Error is not null)
        {
            throw Error;
        }
        // At the end of the file (0 bytes), a sequence cut there is invalid too.
        var flush = bytes.IsEmpty;
        var done = 0;
        try
        {
            do
            {
                _decoder.Convert(bytes[done..], _chars, flush, out var used, out _, out var completed);
                done += used;
                if (completed)
                {
                    break;
                }
            }
            while (done < bytes.Length);
        }
        catch (DecoderFallbackException e)
        {
            // Index is relative to the bytes of the failing call; negative when the sequence began in an earlier read.
            var at = done + e.Index;
            var lines = at > 0 ? bytes[..Math.Min(at, bytes.Length)].Count((byte)'\n') : 0;
            Error = new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                $"The file is not valid UTF-8 at line {_line + lines} (byte offset {_offset + at}). Choose the file's encoding instead of \"{SqlFile.AutoDetect}\" or UTF-8."), e);
            throw Error;
        }
        _line += bytes.Count((byte)'\n');
        _offset += bytes.Length;
    }

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
}

/// <summary>Read stream that counts the bytes read (progress) and stops on cancel (LOAD DATA reads from it).</summary>
internal sealed class CountingStream(Stream inner, CancellationToken cancellationToken) : Stream
{
    private long _read;

    public long BytesRead => Interlocked.Read(ref _read);

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var n = inner.Read(buffer);
        Interlocked.Add(ref _read, n);
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var n = await inner.ReadAsync(buffer, token).ConfigureAwait(false);
        Interlocked.Add(ref _read, n);
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
        ReadAsync(buffer.AsMemory(offset, count), token).AsTask();

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
}
