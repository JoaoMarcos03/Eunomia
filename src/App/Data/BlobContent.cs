using System.Buffers.Binary;
using System.Text;

namespace Eunomia.App.Data;

/// <summary>Image format recognised from the first bytes (Image preview, appendix D: JPEG, GIF, BMP and PNG).</summary>
public enum ImageFormat
{
    Jpeg,
    Gif,
    Bmp,
    Png,
}

/// <summary>
/// Content of a cell for Save BLOB to file and Image preview (appendix D, popupDataGrid and Grid view options): the bytes
/// from the cell's lossless raw text (<c>0x…</c> for binaries, UTF-8 for text), the image format from the magic
/// bytes and the suggested file name (the column name; <c>.txt</c> for text).
/// </summary>
public static class BlobContent
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// The cell's bytes: for a binary, the hex of <c>0x…</c> (the grid's raw text); otherwise the text in UTF-8. A binary that
    /// does not come as valid hex is refused instead of being written as text.
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

    /// <summary>Image format from the first bytes; <c>null</c> if it is not a known image.</summary>
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
        // "BM" plus the bitmap header size (14 + 12/40/52/56/108/124), so that text starting with "BM" is not mistaken for one.
        if (data.Length >= 18 && data[0] == (byte)'B' && data[1] == (byte)'M')
        {
            var header = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(14, 4));
            return header is 12 or 40 or 52 or 56 or 64 or 108 or 124 ? ImageFormat.Bmp : null;
        }
        return null;
    }

    /// <summary>File extension per format.</summary>
    public static string Extension(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => ".jpg",
        ImageFormat.Gif => ".gif",
        ImageFormat.Bmp => ".bmp",
        ImageFormat.Png => ".png",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    /// <summary>
    /// Name suggested in the save dialog: the column name (without characters that file systems refuse) with
    /// <c>.txt</c> for text, the image extension for a recognised binary, or <c>.bin</c>.
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
