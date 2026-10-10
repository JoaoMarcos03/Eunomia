using Eunomia.Drivers.MySql;

namespace Eunomia.Drivers.MySql.Tests;

/// <summary>MySQL 8 <c>information_schema</c> expressions, escaped by <c>String::print</c> (MySQL bug #100607).</summary>
public sealed class DictionaryExpressionTests
{
    [Theory]
    // The bug's example: concat with a literal.
    [InlineData(@"concat(`a`,_utf8mb4\'-\')", "concat(`a`,_utf8mb4'-')")]
    // A quote and a backslash inside the literal, as the server writes them (it's \ ok).
    [InlineData(@"concat(`a`,_utf8mb4\'it\\\'s \\\\ ok\')", @"concat(`a`,_utf8mb4'it\'s \\ ok')")]
    [InlineData(@"(`s` <> _utf8mb4\'x\')", "(`s` <> _utf8mb4'x')")]
    [InlineData(@"\0\n\r\Z", "\0\n\r\u001A")]
    [InlineData("(`price` * 2)", "(`price` * 2)")]
    [InlineData("concat(`nome`,_utf8mb4\\'çã😀\\')", "concat(`nome`,_utf8mb4'çã😀')")]
    [InlineData("", "")]
    public void DesfazOEscapeDoServidor(string escaped, string expected) =>
        Assert.Equal(expected, MySqlMetadataProvider.UnescapeDictionaryText(escaped));

    [Theory]
    // A quote without a backslash: the server no longer escapes (bug fixed); the text stays as it is.
    [InlineData("concat(`a`,_utf8mb4'-')")]
    [InlineData(@"concat(`a`,_utf8mb4'it\'s')")]
    // A backslash followed by something String::print never writes, or at the end.
    [InlineData(@"`a\b`")]
    [InlineData(@"`a`\")]
    public void TextoQueNaoVemEscapadoFicaComoEsta(string text) =>
        Assert.Equal(text, MySqlMetadataProvider.UnescapeDictionaryText(text));

    [Theory]
    [InlineData("8.0.36", "MySQL Community Server - GPL", true)]
    [InlineData("8.4.3", "MySQL Community Server - GPL", true)]
    [InlineData("5.7.44", "MySQL Community Server (GPL)", false)]
    [InlineData("11.4.3-MariaDB", "mariadb.org binary distribution", false)]
    public void SoOMySql8EscapaAsExpressoes(string version, string comment, bool escapes) =>
        Assert.Equal(escapes, new MySqlCapabilities(MySqlConnectionFactory.ParseIdentity(version, comment)).EscapesDictionaryExpressions);
}
