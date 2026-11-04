using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Eunomia.Engine.Sql.Formatting;

/// <summary>
/// Syntax of the window cell editor (appendix C §3.18: HeidiSQL's highlighter combo), with only the formats found in
/// text columns: plain text, JSON (API responses, JSON columns), XML, HTML and SQL.
/// </summary>
public enum TextSyntax
{
    Text,
    Json,
    Xml,
    Html,
    Sql,
}

/// <summary>
/// Detection and formatting of the cell editor syntaxes. Detection only picks the highlighting; the text never changes
/// unless the user asks (<see cref="TryFormat"/> runs on the Format button, like HeidiSQL's "Format code once").
/// </summary>
public static class TextSyntaxes
{
    /// <summary>Above this length, detection does not validate the JSON (it goes by the brackets).</summary>
    public const int MaxValidatedLength = 4 * 1024 * 1024;

    private const int MaxJsonDepth = 512;

    /// <summary>Syntaxes in the combo's order, with the text the combo shows.</summary>
    public static IReadOnlyList<(TextSyntax Syntax, string Caption)> All { get; } =
    [
        (TextSyntax.Text, "Text"),
        (TextSyntax.Json, "JSON"),
        (TextSyntax.Xml, "XML"),
        (TextSyntax.Html, "HTML"),
        (TextSyntax.Sql, "SQL"),
    ];

    public static string Caption(TextSyntax syntax) => All.First(s => s.Syntax == syntax).Caption;

    /// <summary>Syntax saved by its <see cref="Caption"/> (per-column state); unknown = <c>false</c>.</summary>
    public static bool TryParse(string? caption, out TextSyntax syntax)
    {
        foreach (var (s, c) in All)
        {
            if (string.Equals(c, caption, StringComparison.OrdinalIgnoreCase))
            {
                syntax = s;
                return true;
            }
        }
        syntax = TextSyntax.Text;
        return false;
    }

    /// <summary>There is a formatter for the syntax (HTML is rarely valid XML, so it has none).</summary>
    public static bool CanFormat(TextSyntax syntax) => syntax is TextSyntax.Json or TextSyntax.Xml or TextSyntax.Sql;

    /// <summary>
    /// Likely syntax of a value: JSON by the column (<c>JSON</c>) or by text between <c>{}</c>/<c>[]</c> that is valid
    /// JSON; HTML by <c>&lt;!DOCTYPE html</c> or <c>&lt;html</c>; XML by text between <c>&lt;</c> and <c>&gt;</c>;
    /// otherwise, text.
    /// </summary>
    public static TextSyntax Detect(string? text, string? nativeType)
    {
        if (nativeType is not null && nativeType.StartsWith("JSON", StringComparison.OrdinalIgnoreCase))
        {
            return TextSyntax.Json;
        }
        var t = (text ?? string.Empty).AsSpan().Trim();
        if (t.Length < 2)
        {
            return TextSyntax.Text;
        }
        if ((t[0] == '{' && t[^1] == '}') || (t[0] == '[' && t[^1] == ']'))
        {
            return t.Length > MaxValidatedLength || IsJson(t) ? TextSyntax.Json : TextSyntax.Text;
        }
        if (t[0] == '<' && t[^1] == '>')
        {
            return t.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase) || t.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
                ? TextSyntax.Html
                : TextSyntax.Xml;
        }
        return TextSyntax.Text;
    }

    private static bool IsJson(ReadOnlySpan<char> text)
    {
        var bytes = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(text.Length));
        try
        {
            var length = Encoding.UTF8.GetBytes(text, bytes);
            var reader = new Utf8JsonReader(bytes.AsSpan(0, length), new JsonReaderOptions { MaxDepth = MaxJsonDepth });
            while (reader.Read())
            {
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    /// <summary>
    /// Indents the text (JSON and XML with 2 spaces and the line break the text already uses, LF if it has none; SQL
    /// through <see cref="SqlFormatter"/>). Text that is not valid in the syntax stays as it is and
    /// <paramref name="error"/> says why. JSON keeps numbers exactly as written and repeated keys; in strings, an accented
    /// letter written as a Unicode escape becomes the character itself (the same value).
    /// </summary>
    public static bool TryFormat(string text, TextSyntax syntax, out string formatted, out string? error)
    {
        ArgumentNullException.ThrowIfNull(text);
        formatted = text;
        error = null;
        try
        {
            switch (syntax)
            {
                case TextSyntax.Json:
                    formatted = FormatJson(text);
                    return true;
                case TextSyntax.Xml:
                    formatted = FormatXml(text);
                    return true;
                case TextSyntax.Sql:
                    formatted = SqlFormatter.Format(text);
                    return true;
                default:
                    error = $"No formatter for {Caption(syntax)}.";
                    return false;
            }
        }
        catch (Exception ex) when (ex is JsonException or XmlException or ArgumentException or InvalidOperationException)
        {
            error = $"The text is not valid {Caption(syntax)}: {ex.Message}";
            return false;
        }
    }

    private static string NewLineOf(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static string FormatJson(string text)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = MaxJsonDepth });
        var buffer = new ArrayBufferWriter<byte>(Math.Max(256, text.Length * 2));
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = true,
            MaxDepth = MaxJsonDepth,
            NewLine = NewLineOf(text),
            // Accents and symbols stay as they are (only quotes, backslashes and control characters are escaped).
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            document.WriteTo(writer);
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string FormatXml(string text)
    {
        // No DTD (external entities and entity expansion are not processed).
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(text), settings);
        var document = XDocument.Load(reader);
        var newLine = NewLineOf(text);
        var output = new StringBuilder();
        if (document.Declaration is { } declaration)
        {
            output.Append(declaration).Append(newLine);
        }
        var writerSettings = new XmlWriterSettings { OmitXmlDeclaration = true, Indent = true, IndentChars = "  ", NewLineChars = newLine, NewLineHandling = NewLineHandling.None };
        using (var writer = XmlWriter.Create(output, writerSettings))
        {
            foreach (var node in document.Nodes())
            {
                node.WriteTo(writer);
            }
        }
        return output.ToString();
    }
}
