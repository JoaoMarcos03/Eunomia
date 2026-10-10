using System.Buffers.Binary;
using System.Text;

namespace Eunomia.App.Data;

/// <summary>Formato de imagem reconhecido pelos primeiros bytes (Image preview, anexo D: JPEG, GIF, BMP e PNG).</summary>
public enum ImageFormat
{
    Jpeg,
    Gif,
    Bmp,
    Png,
}

/// <summary>
/// Conteúdo de uma célula para Save BLOB to file e Image preview (anexo D, popupDataGrid e Grid view options): os bytes a
/// partir do texto bruto sem perdas da célula (<c>0x…</c> nos binários, texto em UTF-8), o formato de imagem pelos magic
/// bytes e o nome sugerido para gravar (o nome da coluna; <c>.txt</c> no texto).
/// </summary>
public static class BlobContent
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Bytes da célula: num binário, o hex de <c>0x…</c> (o texto bruto do grid); no resto, o texto em UTF-8. Um binário que
    /// não venha em hex válido é recusado em vez de gravado como texto.
    /// </summary>
    public static byte[] ToBytes(string rawText, bool binary)
    {
        ArgumentNullException.ThrowIfNull(rawText);
        if (!binary)
        {
            return Encoding.UTF8.GetBytes(rawText);
        }
        if (!rawText.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || rawText.Length % 2 != 0)
        {
            throw new FormatException("The binary value is not in hexadecimal form.");
        }
        return Convert.FromHexString(rawText.AsSpan(2));
    }

    /// <summary>Formato de imagem pelos primeiros bytes; <c>null</c> se não for uma imagem conhecida.</summary>
    public static ImageFormat? ImageFormatOf(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith(PngSignature))
        {
            return ImageFormat.Png;
        }
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return ImageFormat.Jpeg;
        }
        if (data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8))
        {
            return ImageFormat.Gif;
        }
        // "BM" e o tamanho do cabeçalho do bitmap (14 + 12/40/52/56/108/124) para não confundir texto que comece por "BM".
        if (data.Length >= 18 && data[0] == (byte)'B' && data[1] == (byte)'M')
        {
            var header = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(14, 4));
            return header is 12 or 40 or 52 or 56 or 64 or 108 or 124 ? ImageFormat.Bmp : null;
        }
        return null;
    }

    /// <summary>Extensão do ficheiro por formato.</summary>
    public static string Extension(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => ".jpg",
        ImageFormat.Gif => ".gif",
        ImageFormat.Bmp => ".bmp",
        ImageFormat.Png => ".png",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    /// <summary>
    /// Nome sugerido no diálogo de gravar: o nome da coluna (sem caracteres que os sistemas de ficheiros recusam) com
    /// <c>.txt</c> no texto, a extensão da imagem num binário reconhecido, ou <c>.bin</c>.
    /// </summary>
    public static string SuggestedFileName(string column, bool binary, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(column);
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var name = new string([.. column.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c)]).Trim().TrimEnd('.');
        if (name.Length == 0)
        {
            name = "blob";
        }
        var extension = !binary ? ".txt" : ImageFormatOf(data) is { } format ? Extension(format) : ".bin";
        return name + extension;
    }
}
