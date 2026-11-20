namespace Eunomia.UI.Controls.Grid.Tests;

/// <summary>New rows stay after the source rows: when Next loads more rows, their keys move with the source.</summary>
public sealed class GridInsertShiftTests
{
    [Fact]
    public void InsertNextThenCommitKeepsTheNewRowAfterTheSource()
    {
        var b = new GridEditBuffer();
        b.SetCommitted(5, 0, new CellValue("source edit"));
        var saved = b.InsertRow(1000);
        b.SetValue(saved, 0, new CellValue("saved"));
        b.Commit();
        var pending = b.InsertRow(1000);
        b.SetValue(pending, 0, new CellValue("pending"));

        b.ShiftInsertedRows(1000, 2000);

        Assert.True(b.TryGetValue(2000, 0, out var first) && first.Text == "saved");
        Assert.Equal(2001, b.PendingRow);
        Assert.True(b.Commit());
        Assert.True(b.TryGetValue(2001, 0, out var second) && second.Text == "pending");
        Assert.False(b.TryGetValue(1000, 0, out _));
        Assert.False(b.TryGetValue(1001, 0, out _));
        Assert.True(b.TryGetValue(5, 0, out var source) && source.Text == "source edit");
        Assert.Equal(2002, b.InsertRow(2000));
    }
}
