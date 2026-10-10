using Eunomia.App.Export;

namespace Eunomia.App.Tests;

/// <summary>Name and encoding of the Export grid rows file (appendix B §1.4; FAM-C05).</summary>
public sealed class ExportFileTests
{
    [Fact]
    public void Placeholders()
    {
        var now = new DateTime(2026, 10, 6, 9, 5, 7);
        Assert.Equal(@"C:\out\prod_shop_root_2026-10-06_06-10-2026_09-05-07.csv",
            ExportFile.ExpandPlaceholders(@"C:\out\%session_%db_%u_%date_%d-%m-%y_%h-%i-%s.csv", "prod", "db.local", "root", "shop", now));
        Assert.Equal("a_b.sql", ExportFile.ExpandPlaceholders("%session.sql", "a/b", "h", "u", "d", now));
        Assert.Equal("db.local.json", ExportFile.ExpandPlaceholders("%host.json", "s", "db.local", "u", "d", now));
    }

    [Fact]
    public void CodificacoesEBom()
    {
        Assert.Equal([0x61], ExportFile.Encode("a", "UTF-8"));
        Assert.Equal([0xEF, 0xBB, 0xBF, 0x61], ExportFile.Encode("a", "UTF-8-BOM"));
        Assert.Equal([0xFF, 0xFE, 0x61, 0x00], ExportFile.Encode("a", "Unicode"));
        Assert.Equal([0xFE, 0xFF, 0x00, 0x61], ExportFile.Encode("a", "Unicode Big Endian"));
        Assert.Equal([0x61], ExportFile.Encode("a", "ANSI"));
        Assert.Equal(6, ExportFile.Encodings.Count);
    }

    [Fact]
    public void ExtensaoSegueOFormatoEOFormatoSegueAExtensao()
    {
        Assert.Equal(@"C:\x\dados.json", ExportFile.WithExtension(@"C:\x\dados.csv", GridExportFormat.Json));
        Assert.Equal(GridExportFormat.Markdown, ExportFile.FormatOf("x.md"));
        Assert.Equal(GridExportFormat.JiraTextile, ExportFile.FormatOf("x.jira-textile"));
        Assert.Null(ExportFile.FormatOf("x.zzz"));
        var recent = ExportFile.AddRecent(Enumerable.Range(0, 12).Select(i => $"f{i}.csv"), "f3.csv");
        Assert.Equal(10, recent.Count);
        Assert.Equal("f3.csv", recent[0]);
        Assert.Single(recent, r => r == "f3.csv");
    }
}
