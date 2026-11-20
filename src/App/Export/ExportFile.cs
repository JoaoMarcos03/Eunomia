using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Eunomia.App.Export;

/// <summary>Output file of Export grid rows (appendix B §1.4): name placeholders and encodings.</summary>
public static partial class ExportFile
{
    /// <summary>Encodings of the dialog, in HeidiSQL's order (no UTF-7, obsolete in .NET); the default is "UTF-8".</summary>
    public static readonly IReadOnlyList<string> Encodings = ["ANSI", "ASCII", "Unicode", "Unicode Big Endian", "UTF-8", "UTF-8-BOM"];

    public const string DefaultEncoding = "UTF-8";

    /// <summary>
    /// <c>%session %host %u %db %date %d %m %y %h %i %s</c> (session, host, user, database, date, day, month, year, hour,
    /// minute, second). The values become valid file names (<see cref="FileNames.Safe"/>). One pass over the template: a
    /// value with <c>%</c> in it (a database called <c>%d</c>) stays as it is.
    /// </summary>
    public static string ExpandPlaceholders(string template, string session, string host, string user, string database, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(template);
        var c = CultureInfo.InvariantCulture;
        return Placeholder().Replace(template, match => match.Groups[1].Value switch
        {
            "session" => FileNames.Safe(session),
            "host" => FileNames.Safe(host),
            "date" => now.ToString("yyyy-MM-dd", c),
            "db" => FileNames.Safe(database),
            "u" => FileNames.Safe(user),
            "d" => now.ToString("dd", c),
            "m" => now.ToString("MM", c),
            "y" => now.ToString("yyyy", c),
            "h" => now.ToString("HH", c),
            "i" => now.ToString("mm", c),
            _ => now.ToString("ss", c),
        });
    }

    /// <summary>The longer names first: <c>%date</c> and <c>%db</c> are not <c>%d</c>.</summary>
    [GeneratedRegex("%(session|host|date|db|u|d|m|y|h|i|s)", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    /// <summary>File bytes in the chosen encoding (BOM only for UTF-8-BOM and the Unicode ones).</summary>
    public static byte[] Encode(string text, string encoding)
    {
        ArgumentNullException.ThrowIfNull(text);
        var enc = EncodingOf(encoding);
        var preamble = enc.Preamble;
        var bytes = new byte[preamble.Length + enc.GetByteCount(text)];
        preamble.CopyTo(bytes);
        enc.GetBytes(text, bytes.AsSpan(preamble.Length));
        return bytes;
    }

    /// <summary>The encoding by its name in the list (ANSI = code page of the current culture).</summary>
    public static Encoding EncodingOf(string encoding) =>
        encoding switch
        {
            "ANSI" => AnsiEncoding(),
            "ASCII" => CodePage(20127),
            "Unicode" => new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
            "Unicode Big Endian" => new UnicodeEncoding(bigEndian: true, byteOrderMark: true),
            "UTF-8-BOM" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            _ => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };

    private static Encoding AnsiEncoding() => CodePage(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);

    private static Encoding CodePage(int codePage)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            return Encoding.GetEncoding(codePage);
        }
        catch (NotSupportedException)
        {
            return Encoding.Latin1;
        }
    }

    /// <summary>Format extension in the name (the extension changes when the format changes).</summary>
    public static string WithExtension(string fileName, GridExportFormat format)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        return string.IsNullOrWhiteSpace(fileName) ? fileName : Path.ChangeExtension(fileName, GridExporter.Extension(format));
    }

    /// <summary>Format from the name's extension (the inverse; <c>null</c> if unknown).</summary>
    public static GridExportFormat? FormatOf(string fileName)
    {
        var extension = Path.GetExtension(fileName).TrimStart('.');
        foreach (GridExportFormat format in Enum.GetValues<GridExportFormat>())
        {
            if (string.Equals(GridExporter.Extension(format), extension, StringComparison.OrdinalIgnoreCase))
            {
                return format;
            }
        }
        return null;
    }

    /// <summary>List of the 10 recent files, the latest first.</summary>
    public static List<string> AddRecent(IEnumerable<string> recent, string fileName)
    {
        ArgumentNullException.ThrowIfNull(recent);
        return [fileName, .. recent.Where(f => !string.Equals(f, fileName, StringComparison.OrdinalIgnoreCase)).Take(9)];
    }
}
