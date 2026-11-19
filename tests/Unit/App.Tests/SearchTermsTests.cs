using Eunomia.App.Data;
using Eunomia.Drivers.Abstractions;
using Eunomia.Drivers.MySql;

namespace Eunomia.App.Tests;

/// <summary>Text of the filter panel (appendix D B11): terms separated by <c>;</c>, plain text or regex, in memory and on the server.</summary>
public sealed class SearchTermsTests
{
    private static readonly MySqlDialect Dialect = new(MySqlConnectionFactory.ParseIdentity("10.4.32-MariaDB", null));

    private static readonly TableDefinition Table = new(
        new DbObjectRef("shop", "shop", "people", DbObjectKind.Table),
        [Column("id", LogicalTypeKind.Integer), Column("name", LogicalTypeKind.Text)],
        [], [], [], new Dictionary<string, string>());

    private static ColumnDefinition Column(string name, LogicalTypeKind kind) =>
        new(name, kind.ToString().ToLowerInvariant(), new LogicalType(kind), true, null, false, false, false, null);

    [Theory]
    [InlineData("ana", new[] { "ana" })]
    [InlineData("ana;rui", new[] { "ana", "rui" })]
    [InlineData(@"a\;b", new[] { "a;b" })]
    [InlineData(@"a\;b;c", new[] { "a;b", "c" })]
    [InlineData("ana ; rui", new[] { "ana", "rui" })]
    [InlineData(" ;ana;; ", new[] { "ana" })]
    [InlineData(@"C:\temp", new[] { @"C:\temp" })]
    [InlineData("50%", new[] { "50%" })]
    [InlineData("(x)", new[] { "(x)" })]
    [InlineData(@"ends in \", new[] { @"ends in \" })]
    public void SemicolonsSeparateTerms(string text, string[] expected) =>
        Assert.Equal(expected, SearchTerms.Parse(text, false)!.Terms);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(";")]
    [InlineData(";;")]
    [InlineData(" ; ; ")]
    public void WithoutTermsThereIsNoFilter(string? text) => Assert.Null(SearchTerms.Parse(text, false));

    [Fact]
    public void InARegexTwoBackslashesAndASemicolonEndTheTerm()
    {
        var terms = SearchTerms.Parse(@"a\\;\d", true)!;

        Assert.True(terms.IsRegex);
        Assert.Equal([@"a\\", @"\d"], terms.Terms);
    }

    [Fact]
    public void EqualWhenTermsAndModeAreTheSame()
    {
        Assert.Equal(SearchTerms.Parse("ana;rui", false), SearchTerms.Parse(" ana ; rui ;", false));
        Assert.Equal(SearchTerms.Parse("ana;rui", false)!.GetHashCode(), SearchTerms.Parse("ana ;rui", false)!.GetHashCode());
        Assert.NotEqual(SearchTerms.Parse("ana", false), SearchTerms.Parse("ana", true));
        Assert.NotEqual(SearchTerms.Parse("ana;rui", false), SearchTerms.Parse("rui;ana", false));
    }

    [Fact]
    public void PlainTextIsMatchedAsItIsWithoutCase()
    {
        var regex = SearchTerms.Parse(@"a.b;(x);C:\temp", false)!.LocalRegex();

        Assert.Matches(regex, "xxA.Bxx");
        Assert.DoesNotMatch(regex, "aXb");
        Assert.Matches(regex, "f(x)");
        Assert.Matches(regex, @"c:\TEMP\file");
    }

    [Fact]
    public void RegexTermsAreAlternatives()
    {
        var regex = SearchTerms.Parse(@"^ana$;\d{4}", true)!.LocalRegex();

        Assert.Matches(regex, "ANA");
        Assert.Matches(regex, "year 2024");
        Assert.DoesNotMatch(regex, "banana");
        Assert.ThrowsAny<ArgumentException>(() => SearchTerms.Parse("(", true)!.LocalRegex());
    }

    [Fact]
    public void OnTheServerPlainTextIsLikeWithoutWildcards()
    {
        // Any term in any column; the text in lower case, % and _ escaped with '!'.
        Assert.Equal(
            "LOWER(CONVERT(`id` USING utf8mb4)) LIKE '%50!%%' ESCAPE '!' OR LOWER(CONVERT(`name` USING utf8mb4)) LIKE '%50!%%' ESCAPE '!' OR " +
            "LOWER(CONVERT(`id` USING utf8mb4)) LIKE '%a''b!_c%' ESCAPE '!' OR LOWER(CONVERT(`name` USING utf8mb4)) LIKE '%a''b!_c%' ESCAPE '!'",
            SearchTerms.Parse("50%;A'b_C", false)!.Where(Dialect, Table, []));
    }

    [Fact]
    public void OnTheServerARegexIsRegexpWithoutTheHiddenColumns()
    {
        Assert.Equal(
            "CONVERT(`name` USING utf8mb4) REGEXP '^ana$' OR CONVERT(`name` USING utf8mb4) REGEXP 'a;b'",
            SearchTerms.Parse(@"^ana$;a\;b", true)!.Where(Dialect, Table, ["ID"]));
        Assert.Null(SearchTerms.Parse("ana", false)!.Where(Dialect, Table, ["id", "name"]));
        Assert.Null(SearchTerms.Parse("ana", true)!.Where(Dialect, Table, ["id", "name"]));
    }
}
