using System.Text;
using Eunomia.App.Export;
using Eunomia.App.Query;

namespace Eunomia.App.Tests;

/// <summary>SQL files of the query tabs (appendix C §4.3).</summary>
public sealed class SqlFileTests
{
    [Theory]
    [InlineData("UTF-8-BOM")]
    [InlineData("Unicode")]
    [InlineData("Unicode Big Endian")]
    [InlineData("UTF-8")]
    public void DetecaoAutomaticaVoltaAoTextoEACodificacao(string encoding)
    {
        const string text = "SELECT 'ação', 'ü';";
        var (decoded, detected) = SqlFile.Decode(ExportFile.Encode(text, encoding), SqlFile.AutoDetect);
        Assert.Equal(text, decoded);
        Assert.Equal(encoding, detected);
    }

    [Fact]
    public void BytesQueNaoSaoUtf8SaoAnsi()
    {
        var bytes = Encoding.Latin1.GetBytes("SELECT 'ação';");
        var (_, detected) = SqlFile.Decode(bytes, SqlFile.AutoDetect);
        Assert.Equal("ANSI", detected);
        Assert.Equal("SELECT 'x';", SqlFile.Decode(Encoding.ASCII.GetBytes("SELECT 'x';"), "ASCII").Text);
    }

    [Fact]
    public void VinteRecentes()
    {
        var recent = Enumerable.Range(0, 25).Select(i => $"f{i}.sql").ToList();
        var updated = SqlFile.AddRecent(recent, "F3.SQL");
        Assert.Equal(20, updated.Count);
        Assert.Equal("F3.SQL", updated[0]);
        Assert.DoesNotContain("f3.sql", updated);
        Assert.Equal(SqlFile.AutoDetect, SqlFile.LoadEncodings[0]);
        Assert.DoesNotContain("UTF-7", SqlFile.LoadEncodings);
    }
}
