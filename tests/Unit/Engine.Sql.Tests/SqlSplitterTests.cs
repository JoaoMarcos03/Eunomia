using Eunomia.Engine.Sql.Splitting;

namespace Eunomia.Engine.Sql.Tests;

/// <summary>Splitter (FAM-Q02). A suíte de compatibilidade com 100+ scripts do HeidiSQL fica pendente (golden da Fase 0).</summary>
public sealed class SqlSplitterTests
{
    private static string[] Texts(string sql, string delimiter = ";") => [.. SqlSplitter.Split(sql, delimiter).Select(s => s.Text)];

    [Fact]
    public void SeparaPeloDelimitador()
    {
        Assert.Equal(["SELECT 1", "SELECT 2"], Texts("SELECT 1; SELECT 2"));
        Assert.Equal(["SELECT 1"], Texts("SELECT 1;;;"));
    }

    [Fact]
    public void DelimitadorDentroDeStringsNaoConta()
    {
        var sql = "SELECT 'a;b', \"c;d\", `e;f`, 'it\\'s;', 'x''y;'; SELECT 3";

        Assert.Equal(["SELECT 'a;b', \"c;d\", `e;f`, 'it\\'s;', 'x''y;'", "SELECT 3"], Texts(sql));
    }

    [Fact]
    public void DelimitadorDentroDeComentariosNaoConta()
    {
        var sql = "SELECT 1 -- c; c\n; SELECT 2 # x;y\n; /* ; */ SELECT 3";

        Assert.Equal(["SELECT 1 -- c; c", "SELECT 2 # x;y", "/* ; */ SELECT 3"], Texts(sql));
    }

    [Fact]
    public void DoisHifensSemEspacoNaoSaoComentario()
    {
        Assert.Equal(["SELECT 1--2", "SELECT 3"], Texts("SELECT 1--2; SELECT 3"));
    }

    [Fact]
    public void DelimiterMudaODelimitadorENaoEhEnviado()
    {
        var sql = "DELIMITER //\nCREATE PROCEDURE p() BEGIN SELECT 1; SELECT 2; END //\ndelimiter ;\nSELECT 3;";

        var statements = SqlSplitter.Split(sql);

        Assert.Equal(["CREATE PROCEDURE p() BEGIN SELECT 1; SELECT 2; END", "SELECT 3"], statements.Select(s => s.Text));
        Assert.Equal(["//", ";"], statements.Select(s => s.Delimiter));
        Assert.Equal([2, 4], statements.Select(s => s.Line));
    }

    [Fact]
    public void ApenasComentariosNaoSaoEnviadosMasCondicionaisSim()
    {
        Assert.Equal(["SELECT 1"], Texts("SELECT 1;\n-- fim\n/* nada */\n"));
        Assert.Equal(["/*!40101 SET NAMES utf8 */", "/*M!100100 SET x = 1 */"], Texts("/*!40101 SET NAMES utf8 */;\n/*M!100100 SET x = 1 */;"));
    }

    [Fact]
    public void PosicoesELinhas()
    {
        var sql = "\n\n  SELECT 1;\nSELECT\n2;";

        var statements = SqlSplitter.Split(sql);

        Assert.Equal((4, 12, 3), (statements[0].Start, statements[0].End, statements[0].Line));
        Assert.Equal("SELECT\n2", statements[1].Text);
        Assert.Equal(4, statements[1].Line);
        Assert.Equal("SELECT 1", sql[statements[0].Start..statements[0].End]);
    }

    [Fact]
    public void QuebrasDeLinhaEmStringsContamParaAsLinhas()
    {
        var statements = SqlSplitter.Split("SELECT 'a\nb';\nSELECT 2;");

        Assert.Equal(3, statements[1].Line);
    }

    [Fact]
    public void InstrucaoNoCaretOuAAnterior()
    {
        var sql = "SELECT 1;\n\nSELECT 2;\n";
        var statements = SqlSplitter.Split(sql);

        Assert.Equal("SELECT 1", SqlSplitter.StatementAt(statements, 3)!.Text);
        Assert.Equal("SELECT 2", SqlSplitter.StatementAt(statements, sql.IndexOf("2", StringComparison.Ordinal))!.Text);
        Assert.Equal("SELECT 1", SqlSplitter.StatementAt(statements, 10)!.Text);
        Assert.Equal("SELECT 2", SqlSplitter.StatementAt(statements, sql.Length)!.Text);
        Assert.Null(SqlSplitter.StatementAt([], 0));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("/*")]
    [InlineData("--")]
    [InlineData("#")]
    [InlineData("'")]
    [InlineData("a b")]
    public void DelimitadoresInvalidos(string delimiter)
    {
        Assert.False(SqlSplitter.IsValidDelimiter(delimiter));
        Assert.Throws<ArgumentException>(() => SqlSplitter.Split("SELECT 1", delimiter));
    }

    [Fact]
    public void StringSemFimFicaNumaInstrucao()
    {
        Assert.Equal(["SELECT 'abc; SELECT 2"], Texts("SELECT 'abc; SELECT 2"));
    }

    [Fact]
    public void DelimitadorDeVariosCaracteres()
    {
        Assert.Equal(["SELECT 1", "SELECT 2"], Texts("SELECT 1 $$ SELECT 2 $$", "$$"));
    }
}
