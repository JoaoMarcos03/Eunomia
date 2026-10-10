using System.Globalization;
using Eunomia.App.Export;
using Eunomia.Drivers.Abstractions;
using Eunomia.Drivers.MySql;
using Eunomia.Engine.Results;

namespace Eunomia.App.Tests;

/// <summary>
/// The 16 Copy as and Export grid rows formats against the examples in appendix B §1.3 (FAM-C02, C03): table <c>t</c>
/// with <c>id INT PK AUTO_INCREMENT</c>, <c>name VARCHAR</c>, <c>note TEXT NULL</c> and the row <c>(1, 'a"b', NULL)</c>.
/// HeidiSQL's golden files are left for the manual check.
/// </summary>
public sealed class GridExporterTests
{
    private static readonly MySqlDialect Dialect = new(MySqlConnectionFactory.ParseIdentity("10.4.32-MariaDB", null));

    private static GridExportData Sample(params IReadOnlyList<string?>[] rows)
    {
        var id = new ColumnDefinition("id", "int(11)", new LogicalType(LogicalTypeKind.Integer), false, null, IsAutoIncrement: true, false, false, null);
        GridExportColumn[] columns =
        [
            new("id", new ColumnInfo("id", "INT", TypeCategory.Integer, StorageKind.Int64, true, false), id),
            new("name", new ColumnInfo("name", "VARCHAR", TypeCategory.Text, StorageKind.String, false, true)),
            new("note", new ColumnInfo("note", "TEXT", TypeCategory.Text, StorageKind.String, false, true)),
        ];
        return new GridExportData(columns, rows.Length == 0 ? [["1", "a\"b", null]] : rows, "t", ["id"]);
    }

    private static string Export(GridExportFormat format, GridExportOptions? options = null, GridExportData? data = null) =>
        GridExporter.Write(format, data ?? Sample(), options ?? new GridExportOptions { ToClipboard = false, Culture = CultureInfo.InvariantCulture }, Dialect);

    [Fact]
    public void ExcelCsv()
    {
        Assert.Equal("\"id\",\"name\",\"note\"\r\n\"1\",\"a\"\"b\",\\N\r\n", Export(GridExportFormat.ExcelCsv));
        // Clipboard: TAB; locale with a decimal comma: semicolon.
        Assert.Equal("\"id\"\t\"name\"\t\"note\"\r\n\"1\"\t\"a\"\"b\"\t\\N\r\n", Export(GridExportFormat.ExcelCsv, new GridExportOptions { ToClipboard = true }));
        var pt = Export(GridExportFormat.ExcelCsv, new GridExportOptions { ToClipboard = false, Culture = CultureInfo.GetCultureInfo("pt-PT") });
        Assert.StartsWith("\"id\";\"name\";\"note\"", pt, StringComparison.Ordinal);
    }

    [Fact]
    public void DelimitedText()
    {
        Assert.Equal("id;name;note\r\n1;a\"b;\\N\r\n", Export(GridExportFormat.DelimitedText));
        var custom = Export(GridExportFormat.DelimitedText, new GridExportOptions { Separator = @"\t", Encloser = "\"", Terminator = @"\n", ToClipboard = false });
        Assert.Equal("\"id\"\t\"name\"\t\"note\"\n\"1\"\t\"a\"\"b\"\t\\N\n", custom);
    }

    [Fact]
    public void Xml()
    {
        Assert.Equal(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n\r\n<table_data name=\"t\">\r\n\t<row>\r\n\t\t<field name=\"id\">1</field>\r\n" +
            "\t\t<field name=\"name\">a\"b</field>\r\n\t\t<field name=\"note\" xsi:nil=\"true\" />\r\n\t</row>\r\n</table_data>\r\n",
            Export(GridExportFormat.Xml));
    }

    [Fact]
    public void Sql()
    {
        Assert.Equal("INSERT INTO `t` (`id`, `name`, `note`) VALUES (1, 'a\"b', NULL);\r\n", Export(GridExportFormat.SqlInserts));
        Assert.Equal("INSERT IGNORE INTO `t` (`id`, `name`, `note`) VALUES (1, 'a\"b', NULL);\r\n", Export(GridExportFormat.SqlInsertIgnores));
        Assert.Equal("REPLACE INTO `t` (`id`, `name`, `note`) VALUES (1, 'a\"b', NULL);\r\n", Export(GridExportFormat.SqlReplaces));
        Assert.Equal("DELETE FROM `t` WHERE `id`=1;\r\nINSERT INTO `t` (`id`, `name`, `note`) VALUES (1, 'a\"b', NULL);\r\n", Export(GridExportFormat.SqlDeletesInserts));
        Assert.Equal("UPDATE `t` SET `id`=1, `name`='a\"b', `note`=NULL WHERE `id`=1;\r\n", Export(GridExportFormat.SqlUpdates));
    }

    [Fact]
    public void LatexTextileEJira()
    {
        Assert.Equal("\\begin{tabular}{ c  c  c }\r\nid & name & note\\\\ \r\n$1$ & a\"b & \\\\ \r\n\\end{tabular}\r\n", Export(GridExportFormat.Latex));
        Assert.Equal("|_. id |_. name |_. note |\r\n| 1 | a\"b |  |\r\n", Export(GridExportFormat.Textile));
        Assert.Equal("|| id || name || note ||\r\n| 1 | a\"b |  |\r\n", Export(GridExportFormat.JiraTextile));
    }

    [Fact]
    public void PhpArray()
    {
        Assert.Equal("$t = [\r\n\t[\r\n\t\t'id' => 1,\r\n\t\t'name' => 'a\"b',\r\n\t\t'note' => null,\r\n\t],\r\n];\r\n",
            Export(GridExportFormat.PhpArray, new GridExportOptions { ToClipboard = true }));
        Assert.StartsWith("<?php\r\n$t = [", Export(GridExportFormat.PhpArray), StringComparison.Ordinal);
        // D6: backslash and quote escaped; leading zeros removed.
        var tricky = Export(GridExportFormat.PhpArray, new GridExportOptions { ToClipboard = true }, Sample(["007", @"c:\x'y", "a\tb"]));
        Assert.Contains("'id' => 7,", tricky, StringComparison.Ordinal);
        Assert.Contains(@"'name' => 'c:\\x\'y',", tricky, StringComparison.Ordinal);
        Assert.Contains("'note' => \"a\\tb\",", tricky, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownHere()
    {
        Assert.Equal("t\r\n---\r\n| id | name | note | \r\n| ---: | --- | --- | \r\n| 1 | a\"b | \\N | \r\n", Export(GridExportFormat.Markdown));
    }

    [Fact]
    public void JsonEJsonLines()
    {
        Assert.Equal(
            "{\r\n\t\"table\": \"t\",\r\n\t\"rows\":\r\n\t[\r\n\t\t{\r\n\t\t\t\"id\": 1,\r\n\t\t\t\"name\": \"a\\\"b\",\r\n\t\t\t\"note\": null\r\n\t\t}\r\n\t]\r\n}\r\n",
            Export(GridExportFormat.Json));
        Assert.Equal("{\"id\": 1, \"name\": \"a\\\"b\", \"note\": null}\n", Export(GridExportFormat.JsonLines));
        // D6: with 0 rows the JSON is still valid; control characters as \u00XX.
        var empty = Export(GridExportFormat.Json, data: Sample() with { Rows = [] });
        Assert.Equal("{\r\n\t\"table\": \"t\",\r\n\t\"rows\":\r\n\t[\r\n\t]\r\n}\r\n", empty);
        Assert.Contains("\\u0001", Export(GridExportFormat.JsonLines, data: Sample(["1", "\u0001", null])), StringComparison.Ordinal);
    }

    [Fact]
    public void HtmlTable()
    {
        var html = Export(GridExportFormat.Html, new GridExportOptions { ToClipboard = false, Culture = CultureInfo.InvariantCulture, Generator = "Eunomia 1.0", Now = new DateTime(2026, 10, 6, 12, 0, 0) });

        Assert.StartsWith("<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.0 Transitional//EN\" \r\n\t\"http://www.w3.org/TR/xhtml1/DTD/xhtml1-transitional.dtd\">", html, StringComparison.Ordinal);
        Assert.Contains("<meta http-equiv=\"Content-Type\" content=\"text/html; charset=utf-8\" />", html, StringComparison.Ordinal);
        Assert.Contains("\t\t\t.col1 { text-align:right; }\r\n", html, StringComparison.Ordinal);
        Assert.Contains("<table caption=\"t (1 rows)\">", html, StringComparison.Ordinal);
        Assert.Contains("<td class=\"col2\">a\"b</td>", html, StringComparison.Ordinal);
        Assert.Contains("<td class=\"col3\"></td>", html, StringComparison.Ordinal);
        Assert.Contains("generated 2026-10-06 12:00:00 by Eunomia 1.0", html, StringComparison.Ordinal);
    }

    [Fact]
    public void NomesEValoresHostisNaoSaemDoFormato()
    {
        // Table and column names come from the server and can have any character.
        var columns = Sample().Columns.Select((c, i) => i == 1 ? c with { Name = "n\" onmouseover=\"alert(1)" } : c).ToList();
        var data = new GridExportData(columns, [["1", "x | y\nz", @"\input{/etc/passwd} #1 ^~"]], "t\" onload=\"alert(1)", ["id"]);
        var options = new GridExportOptions { ToClipboard = false, Culture = CultureInfo.InvariantCulture, Query = "SELECT * FROM t WHERE a < 5 AND b = '<script>alert(1)</script>'" };

        var html = Export(GridExportFormat.Html, options, data);
        Assert.Contains("<table caption=\"t&quot; onload=&quot;alert(1) (1 rows)\">", html, StringComparison.Ordinal);
        Assert.Contains("<th class=\"col2\">n\" onmouseover=\"alert(1)</th>", html, StringComparison.Ordinal);
        Assert.Contains("WHERE a &lt; 5 AND b = '&lt;script&gt;alert(1)&lt;/script&gt;'</p>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);

        var xml = Export(GridExportFormat.Xml, new GridExportOptions { ToClipboard = false }, data);
        Assert.Contains("<table_data name=\"t&quot; onload=&quot;alert(1)\">", xml, StringComparison.Ordinal);
        Assert.Contains("<field name=\"n&quot; onmouseover=&quot;alert(1)\">", xml, StringComparison.Ordinal);
        Assert.Contains("<resultset statement=\"SELECT * FROM t WHERE a &lt; 5 AND b = '&lt;script&gt;", Export(GridExportFormat.Xml, options, data), StringComparison.Ordinal);

        var php = Export(GridExportFormat.PhpArray, new GridExportOptions { ToClipboard = true }, data with { TableName = "x = system('id'); $y" });
        Assert.StartsWith("$x___system__id_____y = [", php, StringComparison.Ordinal);
        Assert.StartsWith("$_2024 = [", Export(GridExportFormat.PhpArray, new GridExportOptions { ToClipboard = true }, data with { TableName = "2024" }), StringComparison.Ordinal);
        Assert.StartsWith("$preço = [", Export(GridExportFormat.PhpArray, new GridExportOptions { ToClipboard = true }, data with { TableName = "preço" }), StringComparison.Ordinal);

        var latex = Export(GridExportFormat.Latex, new GridExportOptions { ToClipboard = false }, data);
        Assert.Contains(@"\textbackslash{}input\{/etc/passwd\} \#1 \textasciicircum{}\textasciitilde{}", latex, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\input{", latex, StringComparison.Ordinal);

        var markdown = Export(GridExportFormat.Markdown, new GridExportOptions { ToClipboard = false }, data);
        Assert.Contains(@"| 1 | x \| y<br>z | ", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void OpcoesDasColunasEDasQuebrasDeLinha()
    {
        var noAutoInc = Export(GridExportFormat.DelimitedText, new GridExportOptions { IncludeAutoIncrement = false, ToClipboard = false });
        Assert.Equal("name;note\r\na\"b;\\N\r\n", noAutoInc);
        var noNames = Export(GridExportFormat.SqlInserts, new GridExportOptions { IncludeColumnNames = false, ToClipboard = false });
        Assert.Equal("INSERT INTO `t` VALUES (1, 'a\"b', NULL);\r\n", noNames);
        var lines = Export(GridExportFormat.DelimitedText, new GridExportOptions { RemoveLinebreaks = true, ToClipboard = false }, Sample(["1", "a\r\nb\nc", null]));
        Assert.Contains("1;a b c;\\N", lines, StringComparison.Ordinal);
    }
}
