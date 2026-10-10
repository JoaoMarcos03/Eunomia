using Eunomia.Engine.Sql.Completion;
using Eunomia.Engine.Sql.Lexing;

namespace Eunomia.Engine.Sql.Tests;

public sealed class MySqlLexerTests
{
    private static List<(string Text, SqlTokenKind Kind)> Lex(string text)
    {
        var lines = text.Split('\n');
        return MySqlLexer.Tokenize(text)
            .Where(t => t.Token.Kind != SqlTokenKind.Whitespace)
            .Select(t => (lines[t.Line].Substring(t.Token.Start, t.Token.Length), t.Token.Kind))
            .ToList();
    }

    [Fact]
    public void KeywordsTypesFunctionsAndIdentifiers()
    {
        var tokens = Lex("select COUNT(id) from prd_products where x is not null");

        Assert.Equal(
        [
            ("select", SqlTokenKind.Keyword), ("COUNT", SqlTokenKind.Function), ("(", SqlTokenKind.Operator),
            ("id", SqlTokenKind.Identifier), (")", SqlTokenKind.Operator), ("from", SqlTokenKind.Keyword),
            ("prd_products", SqlTokenKind.Identifier), ("where", SqlTokenKind.Keyword), ("x", SqlTokenKind.Identifier),
            ("is", SqlTokenKind.Keyword), ("not", SqlTokenKind.Keyword), ("null", SqlTokenKind.Keyword),
        ], tokens);
        Assert.Equal(SqlTokenKind.DataType, Lex("VARCHAR")[0].Kind);
    }

    [Theory]
    [InlineData("'abc'")]
    [InlineData("'it''s'")]
    [InlineData(@"'a\'b'")]
    [InlineData("\"dq\"")]
    [InlineData(@"'\\'")]
    public void Strings(string sql)
    {
        Assert.Equal([(sql, SqlTokenKind.String)], Lex(sql));
    }

    [Fact]
    public void BackticksWithADoubledBacktick()
    {
        Assert.Equal([("`my``table`", SqlTokenKind.QuotedIdentifier), (".", SqlTokenKind.Operator), ("`c`", SqlTokenKind.QuotedIdentifier)], Lex("`my``table`.`c`"));
    }

    [Theory]
    [InlineData("42")]
    [InlineData("3.14")]
    [InlineData(".5")]
    [InlineData("1e10")]
    [InlineData("2.5E-3")]
    [InlineData("0x1F")]
    public void Numbers(string sql)
    {
        Assert.Equal([(sql, SqlTokenKind.Number)], Lex(sql));
    }

    [Fact]
    public void NumberGluedToLettersIsAnIdentifier()
    {
        Assert.Equal([("1abc", SqlTokenKind.Identifier)], Lex("1abc"));
    }

    [Fact]
    public void LineComments()
    {
        Assert.Equal([("SELECT", SqlTokenKind.Keyword), ("-- note", SqlTokenKind.Comment)], Lex("SELECT -- note"));
        Assert.Equal([("# other", SqlTokenKind.Comment)], Lex("# other"));
        Assert.Equal([("--", SqlTokenKind.Comment)], Lex("--"));
    }

    [Fact]
    public void DashDashWithoutSpaceIsNotAComment()
    {
        // In MySQL "--" only starts a comment when followed by a space: 1--1 is 1 - (-1).
        Assert.Equal(
            [("1", SqlTokenKind.Number), ("-", SqlTokenKind.Operator), ("-", SqlTokenKind.Operator), ("1", SqlTokenKind.Number)],
            Lex("1--1"));
    }

    [Fact]
    public void BlockCommentOverSeveralLines()
    {
        var tokens = Lex("SELECT /* a\nb */ 1\n/*! 40101 x */");

        Assert.Equal(
        [
            ("SELECT", SqlTokenKind.Keyword), ("/* a", SqlTokenKind.Comment), ("b */", SqlTokenKind.Comment),
            ("1", SqlTokenKind.Number), ("/*! 40101 x */", SqlTokenKind.Comment),
        ], tokens);
    }

    [Fact]
    public void StateCarriesFromLineToLine()
    {
        var tokens = new List<SqlToken>();

        Assert.Equal(LexerState.SingleQuoted, MySqlLexer.TokenizeLine("SELECT 'abc", LexerState.Normal, tokens));
        tokens.Clear();
        Assert.Equal(LexerState.Normal, MySqlLexer.TokenizeLine("def' AS x", LexerState.SingleQuoted, tokens));
        Assert.Equal(new SqlToken(0, 4, SqlTokenKind.String), tokens[0]);
        tokens.Clear();
        Assert.Equal(LexerState.Backtick, MySqlLexer.TokenizeLine("`a", LexerState.Normal, tokens));
        tokens.Clear();
        Assert.Equal(LexerState.BlockComment, MySqlLexer.TokenizeLine("", LexerState.BlockComment, tokens));
        Assert.Empty(tokens);
    }

    [Fact]
    public void VariablesAndDelimiter()
    {
        Assert.Equal(
            [("@@SESSION.sql_mode", SqlTokenKind.Variable), (";", SqlTokenKind.Delimiter), ("@x", SqlTokenKind.Variable)],
            Lex("@@SESSION.sql_mode;@x"));
    }

    [Fact]
    public void TokensCoverTheWholeLine()
    {
        const string line = "SELECT `a`, 'b' /* c */ FROM t WHERE x>=1.5 -- end";
        var tokens = new List<SqlToken>();
        MySqlLexer.TokenizeLine(line, LexerState.Normal, tokens);

        var position = 0;
        foreach (var t in tokens)
        {
            Assert.Equal(position, t.Start);
            position += t.Length;
        }
        Assert.Equal(line.Length, position);
    }

    [Fact]
    public void CompletionMatchesThePrefixThenTheMiddle()
    {
        var catalog = CompletionCatalog.CreateMySql(["prd_products", "ecm_products_discount"], ["product_id"]);

        var found = catalog.Find("product").Select(i => i.Text).ToList();

        Assert.Equal(["product_id", "ecm_products_discount", "prd_products"], found);
        Assert.Contains(catalog.Find("sel"), i => i.Text == "SELECT" && i.Kind == CompletionKind.Keyword);
        Assert.Equal(CompletionKind.Function, catalog.Find("group_con").Single().Kind);
    }
}
