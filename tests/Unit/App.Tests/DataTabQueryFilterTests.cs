using Eunomia.App.Data;
using Eunomia.App.Objects;

namespace Eunomia.App.Tests;

/// <summary>Fixes from the phase 14 audit: filters with a single condition and names with commas in the table editor.</summary>
public sealed class DataTabQueryFilterTests
{
    [Theory]
    [InlineData("id = 1", "id = 1")]
    [InlineData("WHERE id = 1;", "id = 1")]
    [InlineData("name = 'a;b' -- c;", "name = 'a;b' -- c;")]
    public void SingleConditionFilter(string text, string expected)
    {
        Assert.Null(DataTabQuery.FilterError(text));
        Assert.Equal(expected, DataTabQuery.WhereClause(text));
    }

    [Theory]
    [InlineData("1=1; DELETE FROM t")]
    [InlineData("id = 1; DROP TABLE x;")]
    public void FilterWithSeveralStatementsIsRefused(string text)
    {
        Assert.NotNull(DataTabQuery.FilterError(text));
        Assert.Throws<InvalidOperationException>(() => DataTabQuery.WhereClause(text));
    }

    [Fact]
    public void NamesWithCommasAndBackticksRoundTrip()
    {
        string[] names = ["a,b", "c(10)", "`d`", " e", "plain"];
        var text = string.Join(", ", names.Select(TableDesign.FormatName)) + ", f(20)";
        Assert.Equal("`a,b`, `c(10)`, ```d```, ` e`, plain, f(20)", text);
        Assert.Equal([("a,b", (int?)null), ("c(10)", null), ("`d`", null), (" e", null), ("plain", null), ("f", 20)], TableDesign.SplitParts(text));
        Assert.Equal(["a,b", "c(10)", "`d`", " e", "plain", "f"], TableDesign.SplitList(text));
    }
}
