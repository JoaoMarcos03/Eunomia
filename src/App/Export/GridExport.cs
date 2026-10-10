using System.Globalization;
using System.Text;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Results;

namespace Eunomia.App.Export;

/// <summary>The 16 formats of "Copy as" and "Export grid rows", in HeidiSQL's order and with its names (appendix B §1.1).</summary>
public enum GridExportFormat
{
    ExcelCsv,
    DelimitedText,
    Html,
    Xml,
    SqlInserts,
    SqlInsertIgnores,
    SqlReplaces,
    SqlDeletesInserts,
    SqlUpdates,
    Latex,
    Textile,
    JiraTextile,
    PhpArray,
    Markdown,
    Json,
    JsonLines,
}

/// <summary>An exported column: header name, result type and, when known, the table definition.</summary>
public sealed record GridExportColumn(string Name, ColumnInfo Info, ColumnDefinition? Definition = null)
{
    public bool IsNumeric => Info.IsNumeric;

    public bool IsBinary => Info.Storage == StorageKind.Binary || Info.Category is TypeCategory.Binary or TypeCategory.Spatial;

    public bool IsBit => Definition?.Type.Kind == LogicalTypeKind.Bit || Info.NativeType.StartsWith("BIT", StringComparison.OrdinalIgnoreCase);

    public bool IsGenerated => Definition?.IsGenerated == true;

    public bool IsAutoIncrement => Definition?.IsAutoIncrement == true;
}

/// <summary>
/// Rows to export, already as raw text: numbers without separators, dates as the server gives them, binary as
/// lowercase hex (<c>0x…</c>), <c>null</c> for NULL. The order is the grid's (appendix B §1.2).
/// </summary>
public sealed record GridExportData(
    IReadOnlyList<GridExportColumn> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows,
    string TableName,
    IReadOnlyList<string> KeyColumns);

/// <summary>Dialog options (or the clipboard ones, <c>GridExportClp*</c>, for Copy as). HeidiSQL defaults (appendix B §1.4).</summary>
public sealed record GridExportOptions
{
    public bool IncludeColumnNames { get; init; } = true;

    public bool IncludeAutoIncrement { get; init; } = true;

    public bool RemoveLinebreaks { get; init; }

    /// <summary>Delimited text: separator, encloser and terminator with <c>\t \n \r \0 \b \Z \\</c>.</summary>
    public string Separator { get; init; } = ";";

    public string Encloser { get; init; } = string.Empty;

    public string Terminator { get; init; } = @"\r\n";

    public string NullValue { get; init; } = @"\N";

    /// <summary>Output to the clipboard (Excel CSV uses TAB; PHP without <c>&lt;?php</c>).</summary>
    public bool ToClipboard { get; init; } = true;

    /// <summary>Include the query (HTML, XML, Markdown, JSON).</summary>
    public string? Query { get; init; }

    /// <summary>Decimal separator of numbers in Excel CSV, HTML and Markdown.</summary>
    public CultureInfo Culture { get; init; } = CultureInfo.CurrentCulture;

    /// <summary>Name and version in the HTML ("generated … by …").</summary>
    public string Generator { get; init; } = "Eunomia";

    public DateTime Now { get; init; } = DateTime.Now;

    /// <summary>Indent with spaces instead of tabs ("Tabs to spaces").</summary>
    public string Indent { get; init; } = "\t";
}

/// <summary>Cell values to export (appendix B §1.2).</summary>
public static class GridExportValues
{
    /// <summary>Raw text of a cell: binary as lowercase hex (<c>asLowercaseHex</c>), NULs replaced by spaces.</summary>
    public static string? Text(RowBlock block, int row, int column, bool blobAsText = false)
    {
        ArgumentNullException.ThrowIfNull(block);
        if (block.IsNull(row, column))
        {
            return null;
        }
        var info = block.Schema[column];
        if (info.Storage == StorageKind.Binary)
        {
            var bytes = block.GetBytes(row, column);
            if (blobAsText)
            {
                return Encoding.UTF8.GetString(bytes).Replace('\0', ' ');
            }
            return bytes.Length == 0 ? string.Empty : "0x" + Convert.ToHexStringLower(bytes);
        }
        // Lossless: DOUBLE with every digit, BIT in bits, text without the cut suffix.
        var text = DisplayFormatter.FormatRaw(block, row, column) ?? string.Empty;
        return text.Replace('\0', ' ');
    }
}
