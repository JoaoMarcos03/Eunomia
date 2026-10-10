using Eunomia.App.Query;
using Eunomia.Engine.Sql.Splitting;

namespace Eunomia.App.Tests;

/// <summary>Regras de texto das query tabs (anexo C §4.4; FAM-Q03, Q04, Q08, Q11).</summary>
public sealed class QueryTextTests
{
    [Theory]
    [InlineData("UPDATE t SET a = 1", true)]
    [InlineData("delete from t", true)]
    [InlineData("  -- comentário\n DELETE FROM t", true)]
    [InlineData("UPDATE t SET a = 1 WHERE id = 2", false)]
    [InlineData("DELETE FROM t\nWHERE id = 2", false)]
    [InlineData("SELECT * FROM t", false)]
    [InlineData("UPDATE t SET a = 'WHERE'", true)]
    public void UnsafeSemWhere(string sql, bool unsafeQuery)
    {
        Assert.Equal(unsafeQuery, QueryText.IsUnsafe(sql));
    }

    [Fact]
    public void NomesDosResultTabs()
    {
        Assert.Equal(["a", "b c"], QueryText.ResultNames("-- names: a, b c\nSELECT 1; SELECT 2"));
        Assert.Empty(QueryText.ResultNames(new string(' ', 1100) + "-- names: x"));
        Assert.Equal("meu", QueryText.ResultName("-- name: meu\nSELECT 1"));
        Assert.Null(QueryText.ResultName("SELECT 1"));
        Assert.Equal("t (3r × 2c)", QueryText.ResultCaption(null, "t", 0, 3, 2));
        Assert.Equal("Result #2 (0r × 1c)", QueryText.ResultCaption(null, null, 1, 0, 1));
    }

    [Fact]
    public void LinhaDoErro()
    {
        var statement = SqlSplitter.Split("SELECT 1;\nSELECT\n  1 FORM x;")[1];
        Assert.Equal(3, QueryText.ErrorLineOf(statement, "You have an error in your SQL syntax; check the manual ... near 'x' at line 2"));
        Assert.Equal(2, QueryText.ErrorLineOf(statement, "Table 'x' doesn't exist"));
    }

    [Fact]
    public void ResumoNoFormatoDoHeidiSql()
    {
        Assert.Equal("Affected rows: 1  Found rows: 2  Warnings: 0  Duration for 1 query: 0.012 sec.",
            QueryText.Summary(1, 2, 0, 1, 1, TimeSpan.FromMilliseconds(12), TimeSpan.Zero));
        Assert.Equal("Affected rows: 0  Found rows: 1,234  Warnings: 1  Duration for 2 of 3 queries: 1.500 sec. (+ 0.250 sec. network)",
            QueryText.Summary(0, 1234, 1, 2, 3, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(0.25)));
        Assert.Contains("Duration for 3 queries:", QueryText.Summary(0, 0, 0, 3, 3, TimeSpan.Zero, TimeSpan.Zero), StringComparison.Ordinal);
    }
}
