using System.Text;
using Eunomia.App.Export;

namespace Eunomia.App.Query;

/// <summary>
/// SQL files of the query tabs (appendix C §4.3; section 4.7): Load encodings (with "Auto detect (may fail)"),
/// reading and writing, the 20 recent files and the 5 MiB limit above which Load asks.
/// </summary>
public static class SqlFile
{
    public const string AutoDetect = "Auto detect (may fail)";

    /// <summary>Larger files ask before going into the editor.</summary>
    public const long LargeFileBytes = 5L * 1024 * 1024;

    /// <summary><c>SQLFile0..19</c>.</summary>
    public const int MaxRecent = 20;

    /// <summary>Load encodings: automatic detection and the Save ones (no UTF-7).</summary>
    public static IReadOnlyList<string> LoadEncodings { get; } = [AutoDetect, .. ExportFile.Encodings];

    /// <summary>
    /// File text. With automatic detection: UTF-8 or UTF-16 BOM, otherwise UTF-8 if the bytes are valid UTF-8,
    /// otherwise ANSI. Also returns the encoding used (the one Save uses again).
    /// </summary>
    public static (string Text, string Encoding) Decode(byte[] bytes, string encoding)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(encoding);
        if (encoding == AutoDetect)
        {
            encoding = Detect(bytes);
        }
        var text = encoding switch
        {
            "UTF-8-BOM" => Strip(new UTF8Encoding(false).GetString(bytes)),
            "UTF-8" => Strip(new UTF8Encoding(false).GetString(bytes)),
            "Unicode" => Strip(new UnicodeEncoding(bigEndian: false, byteOrderMark: true).GetString(bytes)),
            "Unicode Big Endian" => Strip(new UnicodeEncoding(bigEndian: true, byteOrderMark: true).GetString(bytes)),
            _ => ExportFile.EncodingOf(encoding).GetString(bytes),
        };
        return (text, encoding);
    }

    private static string Strip(string text) => text.Length > 0 && text[0] == '﻿' ? text[1..] : text;

    private static string Detect(byte[] bytes)
    {
        if (bytes is [0xEF, 0xBB, 0xBF, ..])
        {
            return "UTF-8-BOM";
        }
        if (bytes is [0xFF, 0xFE, ..])
        {
            return "Unicode";
        }
        if (bytes is [0xFE, 0xFF, ..])
        {
            return "Unicode Big Endian";
        }
        try
        {
            _ = new UTF8Encoding(false, throwOnInvalidBytes: true).GetCharCount(bytes);
            return "UTF-8";
        }
        catch (DecoderFallbackException)
        {
            return "ANSI";
        }
    }

    /// <summary>Recent list with <paramref name="path"/> on top (no duplicates, up to 20).</summary>
    public static List<string> AddRecent(IEnumerable<string> recent, string path)
    {
        ArgumentNullException.ThrowIfNull(recent);
        return [path, .. recent.Where(f => !string.Equals(f, path, StringComparison.OrdinalIgnoreCase)).Take(MaxRecent - 1)];
    }
}
