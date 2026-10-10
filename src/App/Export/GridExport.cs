using System.Globalization;
using System.Text;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Results;

namespace Eunomia.App.Export;

/// <summary>Os 16 formatos do "Copy as" e do "Export grid rows", pela ordem e com os nomes do HeidiSQL (anexo B §1.1).</summary>
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

/// <summary>Uma coluna exportada: nome do header, tipo do resultado e, quando se sabe, a definição na tabela.</summary>
public sealed record GridExportColumn(string Name, ColumnInfo Info, ColumnDefinition? Definition = null)
{
    public bool IsNumeric => Info.IsNumeric;

    public bool IsBinary => Info.Storage == StorageKind.Binary || Info.Category is TypeCategory.Binary or TypeCategory.Spatial;

    public bool IsBit => Definition?.Type.Kind == LogicalTypeKind.Bit || Info.NativeType.StartsWith("BIT", StringComparison.OrdinalIgnoreCase);

    public bool IsGenerated => Definition?.IsGenerated == true;

    public bool IsAutoIncrement => Definition?.IsAutoIncrement == true;
}

/// <summary>
/// Linhas a exportar, já como texto bruto: números sem separadores, datas como o servidor as dá, binário em hex
/// minúsculo (<c>0x…</c>), <c>null</c> para NULL. A ordem é a do grid (anexo B §1.2).
/// </summary>
public sealed record GridExportData(
    IReadOnlyList<GridExportColumn> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows,
    string TableName,
    IReadOnlyList<string> KeyColumns);

/// <summary>Opções do diálogo (ou as do clipboard, <c>GridExportClp*</c>, no Copy as). Defaults do HeidiSQL (anexo B §1.4).</summary>
public sealed record GridExportOptions
{
    public bool IncludeColumnNames { get; init; } = true;

    public bool IncludeAutoIncrement { get; init; } = true;

    public bool RemoveLinebreaks { get; init; }

    /// <summary>Delimited text: separador, encloser e terminador com <c>\t \n \r \0 \b \Z \\</c>.</summary>
    public string Separator { get; init; } = ";";

    public string Encloser { get; init; } = string.Empty;

    public string Terminator { get; init; } = @"\r\n";

    public string NullValue { get; init; } = @"\N";

    /// <summary>Saída para o clipboard (Excel CSV usa TAB; PHP sem <c>&lt;?php</c>).</summary>
    public bool ToClipboard { get; init; } = true;

    /// <summary>Query incluída (HTML, XML, Markdown, JSON).</summary>
    public string? Query { get; init; }

    /// <summary>Separador decimal dos números em Excel CSV, HTML e Markdown.</summary>
    public CultureInfo Culture { get; init; } = CultureInfo.CurrentCulture;

    /// <summary>Nome e versão no HTML ("generated … by …").</summary>
    public string Generator { get; init; } = "Eunomia";

    public DateTime Now { get; init; } = DateTime.Now;

    /// <summary>Indentação com espaços em vez de tabs ("Tabs to spaces").</summary>
    public string Indent { get; init; } = "\t";
}

/// <summary>Valores de células para exportar (anexo B §1.2).</summary>
public static class GridExportValues
{
    /// <summary>Texto bruto de uma célula: binário em hex minúsculo (<c>asLowercaseHex</c>), NUL trocados por espaços.</summary>
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
        // Sem perdas: DOUBLE com todos os dígitos, BIT em bits, texto sem o sufixo de cortado.
        var text = DisplayFormatter.FormatRaw(block, row, column) ?? string.Empty;
        return text.Replace('\0', ' ');
    }
}
