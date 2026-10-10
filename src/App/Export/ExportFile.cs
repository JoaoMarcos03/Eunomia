using System.Globalization;
using System.Text;

namespace Eunomia.App.Export;

/// <summary>Ficheiro de saída do Export grid rows (anexo B §1.4): placeholders do nome e codificações.</summary>
public static class ExportFile
{
    /// <summary>Codificações do diálogo, pela ordem do HeidiSQL (sem UTF-7, obsoleto no .NET); a de omissão é "UTF-8".</summary>
    public static readonly IReadOnlyList<string> Encodings = ["ANSI", "ASCII", "Unicode", "Unicode Big Endian", "UTF-8", "UTF-8-BOM"];

    public const string DefaultEncoding = "UTF-8";

    /// <summary>
    /// <c>%session %host %u %db %date %d %m %y %h %i %s</c> (sessão, host, utilizador, base, data, dia, mês, ano, hora,
    /// minuto, segundo). Caracteres inválidos num nome de ficheiro viram <c>_</c> nos valores.
    /// </summary>
    public static string ExpandPlaceholders(string template, string session, string host, string user, string database, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(template);
        static string Safe(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }
        var c = CultureInfo.InvariantCulture;
        return template
            .Replace("%session", Safe(session), StringComparison.Ordinal)
            .Replace("%host", Safe(host), StringComparison.Ordinal)
            .Replace("%date", now.ToString("yyyy-MM-dd", c), StringComparison.Ordinal)
            .Replace("%db", Safe(database), StringComparison.Ordinal)
            .Replace("%u", Safe(user), StringComparison.Ordinal)
            .Replace("%d", now.ToString("dd", c), StringComparison.Ordinal)
            .Replace("%m", now.ToString("MM", c), StringComparison.Ordinal)
            .Replace("%y", now.ToString("yyyy", c), StringComparison.Ordinal)
            .Replace("%h", now.ToString("HH", c), StringComparison.Ordinal)
            .Replace("%i", now.ToString("mm", c), StringComparison.Ordinal)
            .Replace("%s", now.ToString("ss", c), StringComparison.Ordinal);
    }

    /// <summary>Bytes do ficheiro na codificação escolhida (BOM só em UTF-8-BOM e nos Unicode).</summary>
    public static byte[] Encode(string text, string encoding)
    {
        ArgumentNullException.ThrowIfNull(text);
        var enc = EncodingOf(encoding);
        var preamble = enc.GetPreamble();
        var body = enc.GetBytes(text);
        return [.. preamble, .. body];
    }

    /// <summary>A codificação pelo nome da lista (ANSI = página de código da cultura atual).</summary>
    public static Encoding EncodingOf(string encoding) =>
        encoding switch
        {
            "ANSI" => AnsiEncoding(),
            "ASCII" => CodePage(437),
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

    /// <summary>Extensão do formato no nome (a extensão muda quando o formato muda).</summary>
    public static string WithExtension(string fileName, GridExportFormat format)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        return string.IsNullOrWhiteSpace(fileName) ? fileName : Path.ChangeExtension(fileName, GridExporter.Extension(format));
    }

    /// <summary>Formato a partir da extensão do nome (o inverso; <c>null</c> se não se souber).</summary>
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

    /// <summary>Lista dos 10 ficheiros recentes, o último primeiro.</summary>
    public static List<string> AddRecent(IEnumerable<string> recent, string fileName)
    {
        ArgumentNullException.ThrowIfNull(recent);
        return [fileName, .. recent.Where(f => !string.Equals(f, fileName, StringComparison.OrdinalIgnoreCase)).Take(9)];
    }
}
