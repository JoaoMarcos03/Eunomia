using Eunomia.App.Search;

namespace Eunomia.App.Tests;

/// <summary>Search and replace text (appendix C §4.7; FAM-Q10).</summary>
public sealed class TextSearchTests
{
    private const string Text = "SELECT name FROM people;\nselect Name, nameless FROM people WHERE name = 'x';";

    [Fact]
    public void ForwardWithCaseAndWholeWords()
    {
        var any = new SearchOptions { Find = "name" };
        Assert.Equal(7, TextSearch.FindNext(Text, 0, 0, Text.Length, any)!.Value.Start);
        Assert.Equal(32, TextSearch.FindNext(Text, 8, 0, Text.Length, any)!.Value.Start);

        var cased = any with { CaseSensitive = true };
        Assert.Equal(38, TextSearch.FindNext(Text, 8, 0, Text.Length, cased)!.Value.Start);

        var whole = any with { WholeWords = true };
        var afterName = TextSearch.FindNext(Text, 33, 0, Text.Length, whole)!.Value;
        Assert.Equal(Text.IndexOf("name =", StringComparison.Ordinal), afterName.Start);
    }

    [Fact]
    public void BackwardAndWithinTheSelection()
    {
        var back = new SearchOptions { Find = "people", Backward = true };
        Assert.Equal(17, TextSearch.FindNext(Text, 40, 0, Text.Length, back)!.Value.Start);
        Assert.Null(TextSearch.FindNext(Text, 17, 0, Text.Length, back));

        // Only on the second line.
        var line2 = Text.IndexOf('\n', StringComparison.Ordinal) + 1;
        var inSelection = new SearchOptions { Find = "people" };
        Assert.Equal(Text.LastIndexOf("people", StringComparison.Ordinal), TextSearch.FindNext(Text, line2, line2, Text.Length, inSelection)!.Value.Start);
        Assert.Null(TextSearch.FindNext(Text, 0, 0, 10, inSelection));
    }

    [Fact]
    public void RegexAndReplacements()
    {
        var regex = new SearchOptions { Find = @"^(\w+) (\w+)", RegularExpression = true, Replace = @"$2\t$1" };
        var match = TextSearch.FindNext(Text, 0, 0, Text.Length, regex)!.Value;
        Assert.Equal("name\tSELECT", match.Replacement);

        var (replaced, count) = TextSearch.ReplaceAll(Text, 0, Text.Length, new SearchOptions { Find = "people", Replace = "p\\nq" });
        Assert.Equal(2, count);
        Assert.Contains("FROM p\nq;", replaced, StringComparison.Ordinal);

        // Literal: "$1" stays as it is.
        var (literal, _) = TextSearch.ReplaceAll("a.b", 0, 3, new SearchOptions { Find = ".", Replace = "$1" });
        Assert.Equal("a$1b", literal);

        // Only within the range.
        var (scoped, n) = TextSearch.ReplaceAll("x x x", 2, 5, new SearchOptions { Find = "x", Replace = "y" });
        Assert.Equal(("x y y", 2), (scoped, n));
    }

    [Fact]
    public void InvalidRegex()
    {
        Assert.ThrowsAny<ArgumentException>(() => TextSearch.FindNext("abc", 0, 0, 3, new SearchOptions { Find = "(", RegularExpression = true }));
    }

    [Fact]
    public void Recent()
    {
        var recent = new List<string> { "b", "a" };
        TextSearch.AddRecent(recent, "a", max: 2);
        TextSearch.AddRecent(recent, "c", max: 2);
        Assert.Equal(["c", "a"], recent);
    }
}
