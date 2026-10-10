using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Eunomia.Drivers.Abstractions;

namespace Eunomia.App.Export;

/// <summary>
/// Escreve as linhas do grid num dos 16 formatos (anexo B §1.3), com as mesmas estruturas, escaping e defaults do
/// HeidiSQL. Divergências deliberadas (D6): JSON válido com 0 linhas e com caracteres de controlo, fins de linha CRLF
/// consistentes no JSON, charset <c>utf-8</c> no HTML, barra invertida e aspa escapadas no PHP.
/// </summary>
public static partial class GridExporter
{
    private const string Crlf = "\r\n";

    public static string Write(GridExportFormat format, GridExportData data, GridExportOptions options, IDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dialect);
        var columns = Enumerable.Range(0, data.Columns.Count)
            .Where(i => options.IncludeAutoIncrement || !data.Columns[i].IsAutoIncrement)
            .ToList();
        var rows = data.Rows.Select(r => (IReadOnlyList<string?>)columns.Select(i => Clean(r[i], options)).ToList()).ToList();
        var cols = columns.Select(i => data.Columns[i]).ToList();
        var view = data with { Columns = cols, Rows = rows };
        var sb = new StringBuilder();
        switch (format)
        {
            case GridExportFormat.ExcelCsv:
                WriteExcel(sb, view, options);
                break;
            case GridExportFormat.DelimitedText:
                WriteDelimited(sb, view, options);
                break;
            case GridExportFormat.Html:
                WriteHtml(sb, view, options);
                break;
            case GridExportFormat.Xml:
                WriteXml(sb, view, options);
                break;
            case GridExportFormat.SqlInserts or GridExportFormat.SqlInsertIgnores or GridExportFormat.SqlReplaces
                or GridExportFormat.SqlDeletesInserts or GridExportFormat.SqlUpdates:
                WriteSql(sb, format, view, options, dialect);
                break;
            case GridExportFormat.Latex:
                WriteLatex(sb, view, options);
                break;
            case GridExportFormat.Textile or GridExportFormat.JiraTextile:
                WriteTextile(sb, view, options, format == GridExportFormat.JiraTextile);
                break;
            case GridExportFormat.PhpArray:
                WritePhp(sb, view, options);
                break;
            case GridExportFormat.Markdown:
                WriteMarkdown(sb, view, options);
                break;
            case GridExportFormat.Json:
                WriteJson(sb, view, options);
                break;
            default:
                WriteJsonLines(sb, view, options);
                break;
        }
        return sb.ToString();
    }

    /// <summary>Extensão do ficheiro de cada formato.</summary>
    public static string Extension(GridExportFormat format) => format switch
    {
        GridExportFormat.ExcelCsv or GridExportFormat.DelimitedText => "csv",
        GridExportFormat.Html => "html",
        GridExportFormat.Xml => "xml",
        GridExportFormat.Latex => "LaTeX",
        GridExportFormat.Textile => "textile",
        GridExportFormat.JiraTextile => "jira-textile",
        GridExportFormat.PhpArray => "php",
        GridExportFormat.Markdown => "md",
        GridExportFormat.Json => "json",
        GridExportFormat.JsonLines => "jsonl",
        _ => "sql",
    };

    private static string Ind(string indent, int count) => string.Concat(Enumerable.Repeat(indent, count));

    /// <summary>"Remove linebreaks from contents": CRLF, CR e LF passam a espaço.</summary>
    private static string? Clean(string? value, GridExportOptions options) =>
        value is null || !options.RemoveLinebreaks ? value : value.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ');

    /// <summary><c>UnescapeString</c>: <c>\t \n \r \0 \b \Z \\</c>.</summary>
    public static string Unescape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                var next = value[i + 1] switch
                {
                    't' => "\t",
                    'n' => "\n",
                    'r' => "\r",
                    '0' => "\0",
                    'b' => "\b",
                    'Z' => "\x1A",
                    '\\' => "\\",
                    _ => null,
                };
                if (next is not null)
                {
                    sb.Append(next);
                    i++;
                    continue;
                }
            }
            sb.Append(value[i]);
        }
        return sb.ToString();
    }

    /// <summary><c>FormatNumber(Data, False)</c>: separador decimal do locale, sem separador de milhares.</summary>
    private static string LocalNumber(string value, GridExportOptions options) =>
        value.Replace(".", options.Culture.NumberFormat.NumberDecimalSeparator, StringComparison.Ordinal);

    // ---------- Excel CSV e Delimited text ----------

    [GeneratedRegex(@"\.(\d+)$")]
    private static partial Regex FractionalSeconds();

    private static void WriteExcel(StringBuilder sb, GridExportData data, GridExportOptions options)
    {
        var separator = options.ToClipboard ? "\t" : options.Culture.NumberFormat.NumberDecimalSeparator == "," ? ";" : ",";
        if (options.IncludeColumnNames)
        {
            // Nomes entre aspas, sem duplicar aspas; colunas binárias como HEX(nome).
            sb.Append(string.Join(separator, data.Columns.Select(c => "\"" + (c.IsBinary ? $"HEX({c.Name})" : c.Name) + "\""))).Append(Crlf);
        }
        foreach (var row in data.Rows)
        {
            var cells = new List<string>(row.Count);
            for (var c = 0; c < row.Count; c++)
            {
                var value = row[c];
                if (value is null)
                {
                    cells.Add(options.NullValue);
                    continue;
                }
                var column = data.Columns[c];
                if (column.IsNumeric)
                {
                    value = LocalNumber(value, options);
                }
                else if (column.Info.Category == Engine.Results.TypeCategory.Temporal)
                {
                    value = FractionalSeconds().Replace(value, string.Empty);
                }
                cells.Add("\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"");
            }
            sb.Append(string.Join(separator, cells)).Append(Crlf);
        }
    }

    private static void WriteDelimited(StringBuilder sb, GridExportData data, GridExportOptions options)
    {
        var separator = Unescape(options.Separator);
        var encloser = Unescape(options.Encloser);
        var terminator = Unescape(options.Terminator);
        string Enclose(string value) =>
            encloser.Length == 0 ? value : encloser + value.Replace(encloser, encloser + encloser, StringComparison.Ordinal) + encloser;
        if (options.IncludeColumnNames)
        {
            sb.Append(string.Join(separator, data.Columns.Select(c => Enclose(c.Name)))).Append(terminator);
        }
        foreach (var row in data.Rows)
        {
            sb.Append(string.Join(separator, row.Select(v => v is null ? options.NullValue : Enclose(v)))).Append(terminator);
        }
    }

    // ---------- HTML e XML ----------

    private static string HtmlSpecialChars(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    /// <summary>
    /// Valor de um atributo entre aspas: também a aspa, senão um nome de tabela ou de coluna vindo do servidor (com
    /// <c>"</c>) saía do atributo e podia acrescentar outros (<c>onmouseover</c> num HTML aberto no browser).
    /// </summary>
    private static string HtmlAttribute(string value) => HtmlSpecialChars(value).Replace("\"", "&quot;", StringComparison.Ordinal);

    private static void WriteHtml(StringBuilder sb, GridExportData data, GridExportOptions options)
    {
        var t = options.Indent;
        sb.Append("<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.0 Transitional//EN\" ").Append(Crlf);
        sb.Append(t).Append("\"http://www.w3.org/TR/xhtml1/DTD/xhtml1-transitional.dtd\">").Append(Crlf).Append(Crlf);
        sb.Append("<html>").Append(Crlf);
        sb.Append(t).Append("<head>").Append(Crlf);
        sb.Append(Ind(t, 2)).Append("<title>").Append(HtmlSpecialChars(data.TableName)).Append("</title>").Append(Crlf);
        sb.Append(Ind(t, 2)).Append("<meta name=\"GENERATOR\" content=\"").Append(options.Generator).Append("\">").Append(Crlf);
        sb.Append(Ind(t, 2)).Append("<meta http-equiv=\"Content-Type\" content=\"text/html; charset=utf-8\" />").Append(Crlf);
        sb.Append(Ind(t, 2)).Append("<meta name=\"color-scheme\" content=\"light dark\">").Append(Crlf);
        sb.Append(Ind(t, 2)).Append("<style type=\"text/css\">").Append(Crlf);
        sb.Append(Ind(t, 3)).Append("th, td { vertical-align:top; border:1px solid currentColor; padding:0.25rem 0.5rem; }").Append(Crlf);
        sb.Append(Ind(t, 3)).Append("table { border-collapse:collapse; }").Append(Crlf);
        for (var c = 0; c < data.Columns.Count; c++)
        {
            if (data.Columns[c].IsNumeric)
            {
                sb.Append(Ind(t, 3)).Append(CultureInfo.InvariantCulture, $".col{c + 1} {{ text-align:right; }}").Append(Crlf);
            }
        }
        sb.Append(Ind(t, 2)).Append("</style>").Append(Crlf);
        sb.Append(t).Append("</head>").Append(Crlf).Append(Crlf);
        sb.Append(t).Append("<body>").Append(Crlf);
        if (options.Query is { } query)
        {
            // Com escaping (o HeidiSQL punha a query tal como está: um "a < b" estragava a página e um texto com HTML
            // entrava nela).
            sb.Append(Ind(t, 2)).Append("<p style=\"font-family:monospace; white-space:pre;\">").Append(HtmlSpecialChars(query)).Append("</p>").Append(Crlf);
        }
        sb.Append(Ind(t, 2)).Append(CultureInfo.InvariantCulture, $"<table caption=\"{HtmlAttribute(data.TableName)} ({data.Rows.Count} rows)\">").Append(Crlf);
        if (options.IncludeColumnNames)
        {
            sb.Append(Ind(t, 3)).Append("<thead>").Append(Crlf);
            sb.Append(Ind(t, 4)).Append("<tr>").Append(Crlf);
            for (var c = 0; c < data.Columns.Count; c++)
            {
                sb.Append(Ind(t, 5)).Append(CultureInfo.InvariantCulture, $"<th class=\"col{c + 1}\">").Append(HtmlSpecialChars(data.Columns[c].Name)).Append("</th>").Append(Crlf);
            }
            sb.Append(Ind(t, 4)).Append("</tr>").Append(Crlf);
            sb.Append(Ind(t, 3)).Append("</thead>").Append(Crlf);
        }
        sb.Append(Ind(t, 3)).Append("<tbody>").Append(Crlf);
        foreach (var row in data.Rows)
        {
            sb.Append(Ind(t, 4)).Append("<tr>").Append(Crlf);
            for (var c = 0; c < row.Count; c++)
            {
                var value = row[c] is { } v ? data.Columns[c].IsNumeric ? LocalNumber(v, options) : v : string.Empty;
                sb.Append(Ind(t, 5)).Append(CultureInfo.InvariantCulture, $"<td class=\"col{c + 1}\">").Append(HtmlSpecialChars(value)).Append("</td>").Append(Crlf);
            }
            sb.Append(Ind(t, 4)).Append("</tr>").Append(Crlf);
        }
        sb.Append(Ind(t, 3)).Append("</tbody>").Append(Crlf);
        sb.Append(Ind(t, 2)).Append("</table>").Append(Crlf);
        sb.Append(Ind(t, 2)).Append("<p><em>generated ").Append(options.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
            .Append(" by ").Append(options.Generator).Append("</em></p>").Append(Crlf);
        sb.Append(t).Append("</body>").Append(Crlf);
        sb.Append("</html>").Append(Crlf);
    }

    private static void WriteXml(StringBuilder sb, GridExportData data, GridExportOptions options)
    {
        var t = options.Indent;
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>").Append(Crlf).Append(Crlf);
        var root = options.Query is { } query
            ? $"<resultset statement=\"{HtmlAttribute(query)}\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">"
            : $"<table_data name=\"{HtmlAttribute(data.TableName)}\">";
        sb.Append(root).Append(Crlf);
        foreach (var row in data.Rows)
        {
            sb.Append(t).Append("<row>").Append(Crlf);
            for (var c = 0; c < row.Count; c++)
            {
                var column = data.Columns[c];
                var name = options.IncludeColumnNames ? $" name=\"{HtmlAttribute(column.Name)}\"" : string.Empty;
                sb.Append(Ind(t, 2));
                if (row[c] is not { } value)
                {
                    sb.Append("<field").Append(name).Append(" xsi:nil=\"true\" />");
                }
                else
                {
                    sb.Append("<field").Append(name).Append(column.IsBinary ? " format=\"hex\"" : string.Empty).Append('>')
                        .Append(HtmlSpecialChars(value)).Append("</field>");
                }
                sb.Append(Crlf);
            }
            sb.Append(t).Append("</row>").Append(Crlf);
        }
        sb.Append(options.Query is null ? "</table_data>" : "</resultset>").Append(Crlf);
    }

    // ---------- SQL ----------

    /// <summary>Literal SQL de um valor exportado: NULL, BIT como <c>b'…'</c>, binário em hex, números sem aspas, texto escapado.</summary>
    private static string SqlValue(GridExportColumn column, string? value, IDialect dialect)
    {
        if (value is null)
        {
            return "NULL";
        }
        if (column.IsBit)
        {
            // O texto bruto de um BIT já vem em dígitos binários (DisplayFormatter.FormatRaw).
            return value.Length > 0 && value.All(c => c is '0' or '1') ? "b'" + value + "'"
                : "b'" + (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bits) ? Convert.ToString(bits, 2) : value) + "'";
        }
        // Só hex e números vão sem aspas; o resto (binário mostrado como texto, por exemplo) é sempre um literal citado,
        // para um valor nunca poder sair do INSERT/UPDATE/DELETE gerado.
        if (column.IsBinary && HexValue().IsMatch(value))
        {
            return value.Length == 2 ? "''" : value;
        }
        if (column.IsBinary && value.Length == 0)
        {
            return "''";
        }
        if (column.IsNumeric && NumberValue().IsMatch(value))
        {
            return value;
        }
        return dialect.StringLiteral(value);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^0x[0-9a-fA-F]*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex HexValue();

    [System.Text.RegularExpressions.GeneratedRegex(@"^[+-]?(\d+(\.\d*)?|\.\d+)([eE][+-]?\d+)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex NumberValue();

    private static void WriteSql(StringBuilder sb, GridExportFormat format, GridExportData data, GridExportOptions options, IDialect dialect)
    {
        var table = dialect.QuoteIdentifier(data.TableName);
        // Colunas geradas ficam de fora da lista e dos valores.
        var exported = Enumerable.Range(0, data.Columns.Count).Where(i => !data.Columns[i].IsGenerated).ToList();
        var columnList = options.IncludeColumnNames
            ? " (" + string.Join(", ", exported.Select(i => dialect.QuoteIdentifier(data.Columns[i].Name))) + ")"
            : string.Empty;
        var keys = data.KeyColumns.Count > 0 ? data.KeyColumns : data.Columns.Select(c => c.Name).ToList();
        foreach (var row in data.Rows)
        {
            var values = string.Join(", ", exported.Select(i => SqlValue(data.Columns[i], row[i], dialect)));
            switch (format)
            {
                case GridExportFormat.SqlInserts:
                    sb.Append("INSERT INTO ").Append(table).Append(columnList).Append(" VALUES (").Append(values).Append(");").Append(Crlf);
                    break;
                case GridExportFormat.SqlInsertIgnores:
                    sb.Append("INSERT IGNORE INTO ").Append(table).Append(columnList).Append(" VALUES (").Append(values).Append(");").Append(Crlf);
                    break;
                case GridExportFormat.SqlReplaces:
                    sb.Append("REPLACE INTO ").Append(table).Append(columnList).Append(" VALUES (").Append(values).Append(");").Append(Crlf);
                    break;
                case GridExportFormat.SqlDeletesInserts:
                    sb.Append("DELETE FROM ").Append(table).Append(" WHERE ").Append(Where(data, row, keys, dialect)).Append(';').Append(Crlf);
                    sb.Append("INSERT INTO ").Append(table).Append(columnList).Append(" VALUES (").Append(values).Append(");").Append(Crlf);
                    break;
                default:
                    // O SET leva todas as colunas exportadas, chave incluída.
                    var set = string.Join(", ", exported.Select(i => dialect.QuoteIdentifier(data.Columns[i].Name) + "=" + SqlValue(data.Columns[i], row[i], dialect)));
                    sb.Append("UPDATE ").Append(table).Append(" SET ").Append(set).Append(" WHERE ").Append(Where(data, row, keys, dialect)).Append(';').Append(Crlf);
                    break;
            }
        }
    }

    /// <summary>
    /// WHERE das linhas (anexo B §1.3): chave primária, UNIQUE sem NULLs ou todas as colunas; as colunas de data, hora e
    /// JSON passam por <c>CastAsText</c>; NULL dá <c>IS NULL</c>; número vazio passa a 0.
    /// </summary>
    private static string Where(GridExportData data, IReadOnlyList<string?> row, IReadOnlyList<string> keys, IDialect dialect)
    {
        var parts = new List<string>();
        foreach (var key in keys)
        {
            var index = Enumerable.Range(0, data.Columns.Count).FirstOrDefault(i => string.Equals(data.Columns[i].Name, key, StringComparison.OrdinalIgnoreCase), -1);
            if (index < 0)
            {
                throw new InvalidOperationException("Cannot compose WHERE clause - column missing: " + key);
            }
            var column = data.Columns[index];
            var name = column.Definition is { } definition ? dialect.CastAsText(definition) : dialect.QuoteIdentifier(column.Name);
            var value = row[index];
            if (value is null)
            {
                parts.Add(name + " IS NULL");
                continue;
            }
            if (column.IsNumeric && value.Length == 0)
            {
                value = "0";
            }
            parts.Add(name + "=" + SqlValue(column, value, dialect));
        }
        return string.Join(" AND ", parts);
    }

    // ---------- LaTeX, Textile, Jira ----------

    /// <summary>
    /// Texto literal em LaTeX: os dez caracteres especiais. O HeidiSQL só tratava <c>_ $ % &amp;</c>; com a barra
    /// invertida e as chavetas livres, um valor como <c>\input{…}</c> ou <c>\write18{…}</c> corria ao compilar.
    /// </summary>
    private static string LatexEscape(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            sb.Append(ch switch
            {
                '\\' => "\\textbackslash{}",
                '{' => "\\{",
                '}' => "\\}",
                '_' => "\\_",
                '$' => "\\$",
                '%' => "\\%",
                '&' => "\\&",
                '#' => "\\#",
                '^' => "\\textasciicircum{}",
                '~' => "\\textasciitilde{}",
                _ => ch.ToString(),
            });
        }
        return sb.ToString();
    }

    private static void WriteLatex(StringBuilder sb, GridExportData data, GridExportOptions options)
    {
        sb.Append("\\begin{tabular}{ ").Append(string.Join("  ", data.Columns.Select(_ => "c"))).Append(" }").Append(Crlf);
        if (options.IncludeColumnNames)
        {
            sb.Append(string.Join(" & ", data.Columns.Select(c => LatexEscape(c.Name)))).Append("\\\\ ").Append(Crlf);
        }
        foreach (var row in data.Rows)
        {
            var cells = row.Select((v, c) => v is null ? string.Empty : data.Columns[c].IsNumeric ? "$" + v + "$" : LatexEscape(v));
            sb.Append(string.Join(" & ", cells)).Append("\\\\ ").Append(Crlf);
        }
        sb.Append("\\end{tabular}").Append(Crlf);
    }

    private static void WriteTextile(StringBuilder sb, GridExportData data, GridExportOptions options, bool jira)
    {
        if (options.IncludeColumnNames)
        {
            sb.Append(jira
                ? "|| " + string.Join(" || ", data.Columns.Select(c => c.Name)) + " ||"
                : "|_. " + string.Join(" |_. ", data.Columns.Select(c => c.Name)) + " |").Append(Crlf);
        }
        foreach (var row in data.Rows)
        {
            sb.Append("| ").Append(string.Join(" | ", row.Select(v => v ?? string.Empty))).Append(" |").Append(Crlf);
        }
    }

    // ---------- PHP, Markdown ----------

    /// <summary>Números sem zeros à esquerda (o PHP leria octal).</summary>
    private static string PhpNumber(string value)
    {
        var negative = value.StartsWith('-');
        var digits = negative ? value[1..] : value;
        var trimmed = digits.TrimStart('0');
        if (trimmed.Length == 0 || trimmed[0] == '.')
        {
            trimmed = "0" + trimmed;
        }
        return negative ? "-" + trimmed : trimmed;
    }

    /// <summary>
    /// <c>FormatPhp</c>: aspas duplas com escapes quando há caracteres de controlo; senão aspas simples (D6: com
    /// <c>\\</c> e <c>\'</c> escapados, que o HeidiSQL perdia).
    /// </summary>
    private static string PhpString(string value)
    {
        if (value.Any(char.IsControl))
        {
            var sb = new StringBuilder("\"");
            foreach (var ch in value)
            {
                sb.Append(ch switch
                {
                    '\n' => "\\n",
                    '\r' => "\\r",
                    '\t' => "\\t",
                    '\v' => "\\v",
                    '\x1B' => "\\e",
                    '\f' => "\\f",
                    '$' => "\\$",
                    '"' => "\\\"",
                    '\\' => "\\\\",
                    _ => ch.ToString(),
                });
            }
            return sb.Append('"').ToString();
        }
        return "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";
    }

    /// <summary>
    /// Nome de variável PHP a partir do nome da tabela: letras, dígitos, <c>_</c> e caracteres não ASCII; o resto passa a
    /// <c>_</c>. Um nome de tabela vindo do servidor (<c>x = system('…'); $y</c>) não pode virar código no ficheiro.
    /// </summary>
    private static string PhpVariable(string name)
    {
        var sb = new StringBuilder(name.Length + 1);
        foreach (var ch in name)
        {
            sb.Append(char.IsAsciiLetterOrDigit(ch) || ch == '_' || ch >= 0x80 ? ch : '_');
        }
        if (sb.Length == 0 || char.IsAsciiDigit(sb[0]))
        {
            sb.Insert(0, '_');
        }
        return sb.ToString();
    }

    private static void WritePhp(StringBuilder sb, GridExportData data, GridExportOptions options)
    {
        var t = options.Indent;
        if (!options.ToClipboard)
        {
            sb.Append("<?php").Append(Crlf);
        }
        sb.Append('$').Append(PhpVariable(data.TableName)).Append(" = [").Append(Crlf);
        foreach (var row in data.Rows)
        {
            sb.Append(t).Append('[').Append(Crlf);
            for (var c = 0; c < row.Count; c++)
            {
                var column = data.Columns[c];
                var value = row[c] is not { } v ? "null" : column.IsNumeric && v.Length > 0 ? PhpNumber(v) : PhpString(v);
                sb.Append(Ind(t, 2));
                if (options.IncludeColumnNames)
                {
                    sb.Append(PhpString(column.Name)).Append(" => ");
                }
                sb.Append(value).Append(',').Append(Crlf);
            }
            sb.Append(t).Append("],").Append(Crlf);
        }
        sb.Append("];").Append(Crlf);
    }

    private static void WriteMarkdown(StringBuilder sb, GridExportData data, GridExportOptions options)
    {
        sb.Append(data.TableName).Append(Crlf).Append("---").Append(Crlf);
        if (options.Query is { } query)
        {
            sb.Append("```sql").Append(Crlf).Append(query).Append(Crlf).Append("```").Append(Crlf);
        }
        // A linha de cabeçalho vai sempre, com células vazias sem nomes.
        sb.Append("| ").Append(string.Join(" | ", data.Columns.Select(c => options.IncludeColumnNames ? MarkdownCell(c.Name) : string.Empty))).Append(" | ").Append(Crlf);
        sb.Append("| ").Append(string.Join(" | ", data.Columns.Select(c => c.IsNumeric ? "---:" : "---"))).Append(" | ").Append(Crlf);
        foreach (var row in data.Rows)
        {
            var cells = row.Select((v, c) => v is null ? options.NullValue : data.Columns[c].IsNumeric ? LocalNumber(v, options) : MarkdownCell(v));
            sb.Append("| ").Append(string.Join(" | ", cells)).Append(" | ").Append(Crlf);
        }
    }

    /// <summary>Célula de uma tabela Markdown: <c>|</c> escapado e quebras de linha como <c>&lt;br&gt;</c> (senão partiam a tabela).</summary>
    private static string MarkdownCell(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r\n", "<br>", StringComparison.Ordinal)
            .Replace("\n", "<br>", StringComparison.Ordinal).Replace("\r", "<br>", StringComparison.Ordinal);

    // ---------- JSON ----------

    /// <summary><c>FormatJson</c> com <c>\ CR LF TAB "</c>; D6: os outros caracteres de controlo como <c>\u00XX</c>.</summary>
    private static string JsonString(string value)
    {
        var sb = new StringBuilder("\"");
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                case < ' ':
                    sb.Append(CultureInfo.InvariantCulture, $"\\u{(int)ch:x4}");
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    private static string JsonValue(GridExportColumn column, string? value) =>
        value is null ? "null" : column.IsNumeric && value.Length > 0 ? value : JsonString(value);

    private static void WriteJson(StringBuilder sb, GridExportData data, GridExportOptions options)
    {
        var t = options.Indent;
        sb.Append('{').Append(Crlf);
        sb.Append(t).Append(options.Query is { } query ? "\"query\": " + JsonString(query) : "\"table\": " + JsonString(data.TableName)).Append(',').Append(Crlf);
        sb.Append(t).Append("\"rows\":").Append(Crlf);
        sb.Append(t).Append('[').Append(Crlf);
        for (var r = 0; r < data.Rows.Count; r++)
        {
            var row = data.Rows[r];
            sb.Append(Ind(t, 2)).Append(options.IncludeColumnNames ? '{' : '[').Append(Crlf);
            for (var c = 0; c < row.Count; c++)
            {
                sb.Append(Ind(t, 3));
                if (options.IncludeColumnNames)
                {
                    sb.Append(JsonString(data.Columns[c].Name)).Append(": ");
                }
                sb.Append(JsonValue(data.Columns[c], row[c])).Append(c < row.Count - 1 ? "," : string.Empty).Append(Crlf);
            }
            sb.Append(Ind(t, 2)).Append(options.IncludeColumnNames ? '}' : ']').Append(r < data.Rows.Count - 1 ? "," : string.Empty).Append(Crlf);
        }
        sb.Append(t).Append(']').Append(Crlf);
        sb.Append('}').Append(Crlf);
    }

    private static void WriteJsonLines(StringBuilder sb, GridExportData data, GridExportOptions options)
    {
        foreach (var row in data.Rows)
        {
            var cells = row.Select((v, c) => (options.IncludeColumnNames ? JsonString(data.Columns[c].Name) + ": " : string.Empty) + JsonValue(data.Columns[c], v));
            sb.Append(options.IncludeColumnNames ? '{' : '[').Append(string.Join(", ", cells)).Append(options.IncludeColumnNames ? '}' : ']').Append('\n');
        }
    }
}
