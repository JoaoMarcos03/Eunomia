using Eunomia.Engine.Sql.Completion;

namespace Eunomia.Engine.Sql.Tests;

/// <summary>Context of autocomplete A (appendix C §4.2; FAM-Q05).</summary>
public sealed class CompletionContextTests
{
    /// <summary>Analyses with the cursor in place of <c>|</c>.</summary>
    private static CompletionContext At(string textWithCaret)
    {
        var caret = textWithCaret.IndexOf('|', StringComparison.Ordinal);
        return CompletionContext.Analyze(textWithCaret.Remove(caret, 1), caret);
    }

    [Theory]
    [InlineData("SELECT 'ab|c'")]
    [InlineData("SELECT \"x|")]
    [InlineData("SELECT 1 -- com|entário")]
    [InlineData("SELECT 1 # x|")]
    [InlineData("SELECT /* a\n b| */ 1")]
    public void SuprimidoEmStringsEComentarios(string text)
    {
        Assert.Equal(CompletionScope.Suppressed, At(text).Scope);
    }

    [Fact]
    public void ForaDeStringsEComentariosFechados()
    {
        Assert.Equal(CompletionScope.General, At("SELECT 'a' |").Scope);
        Assert.Equal(CompletionScope.General, At("SELECT /* x */ na|").Scope);
        Assert.Equal(CompletionScope.General, At("SELECT 1 -- x\nSELECT na|").Scope);
    }

    [Fact]
    public void PalavraAtualEQualificadores()
    {
        var general = At("SELECT na| FROM t");
        Assert.Equal(CompletionScope.General, general.Scope);
        Assert.Equal("na", general.Typed);
        Assert.Equal(7, general.ReplaceStart);
        Assert.Empty(general.Qualifiers);

        var alias = At("SELECT p.| FROM people p");
        Assert.Equal(CompletionScope.Qualified, alias.Scope);
        Assert.Equal(["p"], alias.Qualifiers);
        Assert.Equal(string.Empty, alias.Typed);

        var full = At("SELECT * FROM shop.`my table`.na|");
        Assert.Equal(["shop", "my table"], full.Qualifiers);
        Assert.Equal("na", full.Typed);

        var quoted = At("SELECT `na|");
        Assert.True(quoted.Quoted);
        Assert.Equal("na", quoted.Typed);
        Assert.Equal(7, quoted.ReplaceStart);
    }

    [Theory]
    [InlineData("SELECT @@SESSION.max|", false)]
    [InlineData("SELECT @@global.|", true)]
    public void VariaveisDoServidor(string text, bool global)
    {
        var context = At(text);
        Assert.Equal(CompletionScope.Variables, context.Scope);
        Assert.Equal(global, context.GlobalVariables);
    }

    [Fact]
    public void TabelasEAliasesDaInstrucaoAtual()
    {
        var context = At("SELECT 1;\nSELECT p.| FROM people p LEFT JOIN shop.orders AS o ON o.person = p.id, `x y` WHERE 1;\nSELECT * FROM other");
        Assert.Equal(
            [new TableReference(null, "people", "p"), new TableReference("shop", "orders", "o"), new TableReference(null, "x y", null)],
            context.Tables);
    }

    [Theory]
    [InlineData("UPDATE people SET name = 1 WHERE id = 2", "people", null)]
    [InlineData("INSERT INTO people (id) VALUES (1)", "people", null)]
    [InlineData("DELETE FROM people WHERE 1", "people", null)]
    [InlineData("SELECT a, b FROM user u ORDER BY a", "user", "u")]
    [InlineData("SELECT * FROM t1 STRAIGHT_JOIN t2", "t1", null)]
    public void ListasDeTabelas(string sql, string first, string? alias)
    {
        var tables = CompletionContext.TablesOf(sql);
        Assert.Equal(first, tables[0].Table);
        Assert.Equal(alias, tables[0].Alias);
    }

    [Fact]
    public void BaseAindaASerEscritaNaoEhTabela()
    {
        var context = At("SELECT * FROM shop.|");
        Assert.Equal(["shop"], context.Qualifiers);
        Assert.Empty(context.Tables);
    }

    [Fact]
    public void JoinsComoVirgulas()
    {
        Assert.Equal(["a", "b", "c"], CompletionContext.TablesOf("SELECT * FROM a INNER JOIN b USING (id), c WHERE 1").Select(t => t.Table));
        Assert.Equal(["t1", "t2"], CompletionContext.TablesOf("SELECT * FROM t1 STRAIGHT_JOIN t2").Select(t => t.Table));
    }
}
