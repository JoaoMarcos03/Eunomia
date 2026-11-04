using Eunomia.Engine.Sql.Formatting;

namespace Eunomia.Engine.Sql.Tests;

/// <summary>Cell editor: highlighting detection and Format (JSON, XML, SQL) only when asked, without losing values.</summary>
public sealed class TextSyntaxesTests
{
    [Theory]
    [InlineData("{\"a\": 1}", null, TextSyntax.Json)]
    [InlineData("  [1, 2, {\"b\": null}]\n", null, TextSyntax.Json)]
    [InlineData("anything", "json", TextSyntax.Json)]
    [InlineData("[INFO] started]", null, TextSyntax.Text)]
    [InlineData("{not json}", null, TextSyntax.Text)]
    [InlineData("<root><a x=\"1\"/></root>", null, TextSyntax.Xml)]
    [InlineData("<?xml version=\"1.0\"?><r/>", "LONGTEXT", TextSyntax.Xml)]
    [InlineData("<!DOCTYPE html><html><body>x</body></html>", null, TextSyntax.Html)]
    [InlineData("<HTML><p>x</p></HTML>", null, TextSyntax.Html)]
    [InlineData("SELECT 1", null, TextSyntax.Text)]
    [InlineData("", null, TextSyntax.Text)]
    [InlineData(null, null, TextSyntax.Text)]
    [InlineData("{", null, TextSyntax.Text)]
    public void DetectsByTypeAndByValue(string? text, string? nativeType, TextSyntax expected) =>
        Assert.Equal(expected, TextSyntaxes.Detect(text, nativeType));

    [Fact]
    public void FormattedJsonKeepsNumbersRepeatedKeysAndAccents()
    {
        const string json = "{\"price\":12.50,\"big\":123456789012345678901234567890,\"e\":1E+3,\"a\":1,\"a\":2,\"name\":\"Ação & <tag>\",\"list\":[],\"o\":{}}";
        Assert.True(TextSyntaxes.TryFormat(json, TextSyntax.Json, out var formatted, out var error));
        Assert.Null(error);
        Assert.Equal(
            "{\n  \"price\": 12.50,\n  \"big\": 123456789012345678901234567890,\n  \"e\": 1E+3,\n  \"a\": 1,\n  \"a\": 2,\n  \"name\": \"Ação & <tag>\",\n  \"list\": [],\n  \"o\": {}\n}",
            formatted);
    }

    [Fact]
    public void FormattedJsonUsesTheTextsLineBreak()
    {
        Assert.True(TextSyntaxes.TryFormat("{\"a\":\r\n[1]}", TextSyntax.Json, out var formatted, out _));
        Assert.Equal("{\r\n  \"a\": [\r\n    1\r\n  ]\r\n}", formatted);
    }

    [Fact]
    public void JsonUnicodeEscapesBecomeTheSameValue()
    {
        var escaped = "{\"s\":\"caf\\" + "u00e9 \\\"q\\\" \\\\ \\t\"}";
        Assert.True(TextSyntaxes.TryFormat(escaped, TextSyntax.Json, out var formatted, out _));
        Assert.Equal("{\n  \"s\": \"café \\\"q\\\" \\\\ \\t\"\n}", formatted);
    }

    [Theory]
    [InlineData("{\"a\": 1,}")]
    [InlineData("{\"a\": 1} trailing")]
    [InlineData("// comment\n{\"a\": 1}")]
    [InlineData("not json")]
    public void InvalidJsonStaysTheSameAndSaysWhy(string text)
    {
        Assert.False(TextSyntaxes.TryFormat(text, TextSyntax.Json, out var formatted, out var error));
        Assert.Same(text, formatted);
        Assert.StartsWith("The text is not valid JSON", error, StringComparison.Ordinal);
    }

    [Fact]
    public void FormattedXmlKeepsTheDeclarationAndIndents()
    {
        Assert.True(TextSyntaxes.TryFormat("<?xml version=\"1.0\" encoding=\"utf-8\"?><r a=\"1\"><b>x &amp; y</b><!-- c --></r>", TextSyntax.Xml, out var formatted, out _));
        Assert.Equal("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<r a=\"1\">\n  <b>x &amp; y</b>\n  <!-- c -->\n</r>", formatted);
    }

    [Fact]
    public void XmlWithDtdIsNotProcessed()
    {
        const string xml = "<!DOCTYPE r [<!ENTITY x \"boom\">]><r>&x;</r>";
        Assert.False(TextSyntaxes.TryFormat(xml, TextSyntax.Xml, out var formatted, out var error));
        Assert.Same(xml, formatted);
        Assert.StartsWith("The text is not valid XML", error, StringComparison.Ordinal);
    }

    [Fact]
    public void SqlGoesThroughTheFormatterAndTextAndHtmlHaveNone()
    {
        Assert.True(TextSyntaxes.TryFormat("select a from t where b=1", TextSyntax.Sql, out var sql, out _));
        Assert.Equal(SqlFormatter.Format("select a from t where b=1"), sql);
        Assert.False(TextSyntaxes.CanFormat(TextSyntax.Text));
        Assert.False(TextSyntaxes.CanFormat(TextSyntax.Html));
        Assert.False(TextSyntaxes.TryFormat("<p>x", TextSyntax.Html, out var html, out var error));
        Assert.Equal("<p>x", html);
        Assert.Equal("No formatter for HTML.", error);
    }

    [Fact]
    public void CaptionsRoundTripToTheSyntax()
    {
        foreach (var (syntax, caption) in TextSyntaxes.All)
        {
            Assert.True(TextSyntaxes.TryParse(caption, out var parsed));
            Assert.Equal(syntax, parsed);
            Assert.Equal(caption, TextSyntaxes.Caption(syntax));
        }
        Assert.False(TextSyntaxes.TryParse("Pascal", out _));
        Assert.False(TextSyntaxes.TryParse(null, out _));
    }
}
