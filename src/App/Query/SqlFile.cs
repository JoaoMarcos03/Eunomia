using System.Text;
using Eunomia.App.Export;

namespace Eunomia.App.Query;

/// <summary>
/// Ficheiros SQL das query tabs (anexo C §4.3; secção 4.7): codificações do Load (com "Auto detect (may fail)"),
/// leitura e escrita, os 20 recentes e o limite de 5 MiB a partir do qual o Load pergunta.
/// </summary>
public static class SqlFile
{
    public const string AutoDetect = "Auto detect (may fail)";

    /// <summary>Ficheiros maiores perguntam antes de entrar no editor.</summary>
    public const long LargeFileBytes = 5L * 1024 * 1024;

    /// <summary><c>SQLFile0..19</c>.</summary>
    public const int MaxRecent = 20;

    /// <summary>Codificações do Load: a deteção automática e as do Save (sem UTF-7).</summary>
    public static IReadOnlyList<string> LoadEncodings { get; } = [AutoDetect, .. ExportFile.Encodings];

    /// <summary>
    /// Texto do ficheiro. Na deteção automática: BOM de UTF-8 ou UTF-16, senão UTF-8 se os bytes forem UTF-8 válido,
    /// senão ANSI. Devolve também a codificação usada (a que o Save volta a usar).
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

    /// <summary>Lista dos recentes com <paramref name="path"/> no topo (sem repetidos, até 20).</summary>
    public static List<string> AddRecent(IEnumerable<string> recent, string path)
    {
        ArgumentNullException.ThrowIfNull(recent);
        return [path, .. recent.Where(f => !string.Equals(f, path, StringComparison.OrdinalIgnoreCase)).Take(MaxRecent - 1)];
    }
}
