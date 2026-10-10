namespace Eunomia.Drivers.MySql.Tests;

/// <summary>Forma das instruções para juntar o SHOW WARNINGS (PR-10) e limpeza das mensagens de erro.</summary>
public sealed class StatementShapeTests
{
    [Theory]
    [InlineData("SELECT 1", true)]
    [InlineData("  -- x\n/* y */ (SELECT 1) UNION (SELECT 2)", true)]
    [InlineData("show tables", true)]
    [InlineData("WITH a AS (SELECT 1) SELECT * FROM a", true)]
    [InlineData("CALL p()", true)]
    [InlineData("UPDATE t SET a = 1", false)]
    [InlineData("INSERT INTO t SELECT 1", false)]
    [InlineData("/*!40101 SET NAMES utf8 */", false)]
    [InlineData("# só comentário", false)]
    public void PodeDevolverLinhas(string sql, bool rows)
    {
        Assert.Equal(rows, MySqlStatementShape.MayReturnRows(sql));
    }

    [Theory]
    [InlineData("SELECT 'a''b', \"c\\\"d\", `e``f`", true)]
    [InlineData("SELECT 1 -- fim", true)]
    [InlineData("SELECT 1 /* fechado */", true)]
    [InlineData("SELECT 'aberta", false)]
    [InlineData("SELECT 1 /* aberto", false)]
    [InlineData("SELECT `aberto", false)]
    public void AcabaForaDeStringsEComentarios(string sql, bool code)
    {
        Assert.Equal(code, MySqlStatementShape.EndsInCode(sql));
    }

    [Fact]
    public void MensagemDeErroSemOSufixo()
    {
        const string prefix = "You have an error in your SQL syntax; check the manual that corresponds to your MariaDB server version for the right syntax to use near ";
        Assert.Equal(prefix + "'x' at line 2", MySqlQueryExecutor.StripWarningsSuffix(prefix + "'x\n;SHOW WARNINGS' at line 2"));
        Assert.Equal(prefix + "'xxxx' at line 1", MySqlQueryExecutor.StripWarningsSuffix(prefix + "'xxxx\n;SHOW' at line 1"));
        Assert.Equal("Table 'a.b' doesn't exist", MySqlQueryExecutor.StripWarningsSuffix("Table 'a.b' doesn't exist"));
        Assert.Equal("' at line 1", MySqlQueryExecutor.StripWarningsSuffix("' at line 1"));
    }

    [Theory]
    [InlineData("VIRTUAL GENERATED", true)]
    [InlineData("STORED GENERATED", true)]
    [InlineData("PERSISTENT", true)]
    [InlineData("DEFAULT_GENERATED on update CURRENT_TIMESTAMP", false)]
    [InlineData("DEFAULT_GENERATED", false)]
    [InlineData("auto_increment", false)]
    public void ColunaGeradaPeloExtra(string extra, bool generated)
    {
        Assert.Equal(generated, MySqlMetadataProvider.IsGeneratedExtra(extra));
    }
}
