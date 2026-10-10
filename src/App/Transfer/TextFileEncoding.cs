using System.Text;
using Eunomia.App.Export;
using Eunomia.App.Query;

namespace Eunomia.App.Transfer;

/// <summary>Codificação de um ficheiro lido em streaming (Run SQL file e CSV import; secção 13.1: BOM, senão a escolhida).</summary>
internal static class TextFileEncoding
{
    /// <summary>BOM (manda sempre), senão a codificação escolhida; "Auto detect": UTF-8 se o início for UTF-8 válido, senão ANSI.</summary>
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
        // Um carácter multibyte cortado no fim do bloco não conta como inválido.
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

    /// <summary>Codificação sem BOM (o BOM já foi saltado).</summary>
    public static Encoding For(string name) => name switch
    {
        "UTF-8" or "UTF-8-BOM" => new UTF8Encoding(false),
        "Unicode" => new UnicodeEncoding(bigEndian: false, byteOrderMark: false),
        "Unicode Big Endian" => new UnicodeEncoding(bigEndian: true, byteOrderMark: false),
        _ => ExportFile.EncodingOf(name),
    };

    /// <summary>Abre o ficheiro depois do BOM e diz a codificação usada.</summary>
    public static async Task<(FileStream Stream, string Encoding)> OpenAsync(string path, string encoding, CancellationToken cancellationToken)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
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

/// <summary>Stream de leitura que conta os bytes lidos (progresso) e pára com o cancel (o LOAD DATA lê dele).</summary>
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
