using Eunomia.Drivers.MySql;

namespace Eunomia.Drivers.MySql.Tests;

/// <summary>Expressões do <c>information_schema</c> do MySQL 8, escapadas por <c>String::print</c> (bug #100607 do MySQL).</summary>
public sealed class DictionaryExpressionTests
{
    [Theory]
    // O exemplo do bug: concat com um literal.
    [InlineData(@"concat(`a`,_utf8mb4\'-\')", "concat(`a`,_utf8mb4'-')")]
    // Plica e barra dentro do literal, como o servidor as escreve (it's \ ok).
    [InlineData(@"concat(`a`,_utf8mb4\'it\\\'s \\\\ ok\')", @"concat(`a`,_utf8mb4'it\'s \\ ok')")]
    [InlineData(@"(`s` <> _utf8mb4\'x\')", "(`s` <> _utf8mb4'x')")]
    [InlineData(@"\0\n\r\Z", "\0\n\r\u001A")]
    [InlineData("(`price` * 2)", "(`price` * 2)")]
    [InlineData("concat(`nome`,_utf8mb4\\'çã😀\\')", "concat(`nome`,_utf8mb4'çã😀')")]
    [InlineData("", "")]
    public void DesfazOEscapeDoServidor(string escaped, string expected) =>
        Assert.Equal(expected, MySqlMetadataProvider.UnescapeDictionaryText(escaped));

    [Theory]
    // Uma plica sem barra: o servidor já não escapa (bug corrigido); o texto fica como está.
    [InlineData("concat(`a`,_utf8mb4'-')")]
    [InlineData(@"concat(`a`,_utf8mb4'it\'s')")]
    // Barra seguida de algo que o String::print nunca escreve, ou no fim.
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
