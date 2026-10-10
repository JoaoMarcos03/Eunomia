using Eunomia.Engine.Sql.Lexing;

namespace Eunomia.Engine.Sql.Tests;

public sealed class MySqlLanguageTests
{
    private static readonly ISqlLanguage Language = SqlLanguages.MySql;

    [Theory]
    [InlineData("prd_products", "prd_products")]
    [InlineData("tabela$1", "tabela$1")]
    [InlineData("order", "`order`")]
    [InlineData("Select", "`Select`")]
    [InlineData("date", "`date`")]
    [InlineData("my table", "`my table`")]
    [InlineData("ação", "`ação`")]
    [InlineData("123", "`123`")]
    [InlineData("1abc", "1abc")]
    [InlineData("a`b", "`a``b`")]
    [InlineData("", "``")]
    public void CitaSoQuandoEhPreciso(string name, string expected)
    {
        Assert.Equal(expected, Language.EditorIdentifier(name));
    }

    [Fact]
    public void QualificaComABase()
    {
        Assert.Equal("elmwood.prd_products", Language.EditorQualifiedName("elmwood", "prd_products"));
        Assert.Equal("`my db`.`order`", Language.EditorQualifiedName("my db", "order"));
    }

    [Theory]
    [InlineData("people", "`people`")]
    [InlineData("a`b", "`a``b`")]
    [InlineData("", "``")]
    public void QuoteIdentifierCitaSempre(string name, string expected)
    {
        Assert.Equal(expected, Language.QuoteIdentifier(name));
    }

    [Fact]
    public void ListasELexerSaoOsDoMySql()
    {
        Assert.Same(MySqlKeywords.Keywords, Language.Keywords);
        Assert.Same(MySqlKeywords.DataTypes, Language.DataTypes);
        Assert.Same(MySqlKeywords.Functions, Language.Functions);
        var tokens = new List<SqlToken>();
        var state = Language.TokenizeLine("SELECT `a", LexerState.Normal, tokens);
        Assert.Equal(LexerState.Backtick, state);
        Assert.Equal(SqlTokenKind.Keyword, tokens[0].Kind);
        Assert.Same(SqlLanguages.MySql, SqlLanguages.Default);
    }
}
