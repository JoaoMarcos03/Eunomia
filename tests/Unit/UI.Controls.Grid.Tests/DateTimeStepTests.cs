namespace Eunomia.UI.Controls.Grid.Tests;

/// <summary>↑/↓ on the cursor segment of the date and time editor (appendix D B15, <c>TDateTimeEditorLink</c>).</summary>
public sealed class DateTimeStepTests
{
    [Theory]
    [InlineData("2026-10-06 18:30:05", 2, 1, "2027-10-06 18:30:05")]
    [InlineData("2026-12-06 18:30:05", 6, 1, "2026-01-06 18:30:05")]
    [InlineData("2026-10-01 18:30:05", 9, -1, "2026-10-31 18:30:05")]
    [InlineData("2026-10-06 23:30:05", 12, 1, "2026-10-06 00:30:05")]
    [InlineData("2026-10-06 18:59:05", 15, 1, "2026-10-06 18:00:05")]
    [InlineData("2026-10-06 18:30:00", 18, -1, "2026-10-06 18:30:59")]
    [InlineData("2026-10-06 18:30:05.999", 21, 1, "2026-10-06 18:30:05.000")]
    [InlineData("2026-10-06", 10, 1, "2026-10-07")]
    [InlineData("12:00:00", 0, -1, "11:00:00")]
    [InlineData("", 0, 1, "")]
    [InlineData("abc", 1, 1, "abc")]
    public void AddsToTheSegment(string text, int caret, int delta, string expected)
    {
        Assert.Equal(expected, DateTimeCellEditor.Step(text, caret, delta));
    }
}
