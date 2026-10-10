using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Eunomia.UI.Controls.Grid;

namespace Eunomia.UI.Headless.Tests;

/// <summary>The grid's fast text path: plain text only; the rest has to go through full shaping.</summary>
public sealed class GlyphRunFactoryTests
{
    private static GlyphRunFactory Tahoma() => new(new Typeface("Tahoma"), 8 * 96.0 / 72.0);

    [AvaloniaTheory]
    [InlineData("abc 123")]
    [InlineData("Größe ação café")]
    [InlineData("1,234,567.89")]
    public void PlainTextBecomesAGlyphRun(string text)
    {
        Assert.True(Tahoma().TryCreate(text, 300, alignRight: false, out var run, out var offset));
        Assert.Equal(text.Length, run!.GlyphInfos.Count);
        Assert.Equal(0, offset);
    }

    [AvaloniaTheory]
    [InlineData("مرحبا")]           // RTL
    [InlineData("e\u0301")]         // combining mark
    [InlineData("😀")]               // surrogate
    [InlineData("a\tb")]            // control character
    [InlineData("")]
    public void ComplexTextUsesFullShaping(string text)
    {
        Assert.False(Tahoma().TryCreate(text, 300, alignRight: false, out _, out _));
    }

    [AvaloniaFact]
    public void TextThatDoesNotFitGetsAnEllipsis()
    {
        var factory = Tahoma();
        var text = new string('x', 200);

        Assert.True(factory.TryCreate(text, 60, alignRight: false, out var run, out _));
        Assert.EndsWith("…", run!.Characters.ToString(), StringComparison.Ordinal);
        Assert.True(run.Metrics.WidthIncludingTrailingWhitespace <= 60.01);
        Assert.True(run.GlyphInfos.Count < 30);
    }

    [AvaloniaFact]
    public void RightAlignmentShifts()
    {
        Assert.True(Tahoma().TryCreate("42", 100, alignRight: true, out var run, out var offset));
        Assert.Equal(100 - run!.Metrics.WidthIncludingTrailingWhitespace, offset, 1);
    }
}
