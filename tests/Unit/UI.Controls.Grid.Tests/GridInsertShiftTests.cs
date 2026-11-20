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

    [Fact]
    public void ShrinkingSourceDropsVanishedRowsAndMovesTheNewRowsDown()
    {
        var b = new GridEditBuffer();
        b.SetCommitted(5, 0, new CellValue("kept"));
        b.SetCommitted(1500, 0, new CellValue("vanished"));
        var saved = b.InsertRow(2000);
        b.SetValue(saved, 0, new CellValue("saved"));
        b.Commit();
        var pending = b.InsertRow(2000);
        b.SetValue(pending, 0, new CellValue("pending"));

        b.ShiftInsertedRows(2000, 1000);

        Assert.True(b.TryGetValue(5, 0, out var kept) && kept.Text == "kept");
        Assert.False(b.TryGetValue(1500, 0, out _));
        Assert.True(b.TryGetValue(1000, 0, out var first) && first.Text == "saved");
        Assert.Equal(1001, b.PendingRow);
        Assert.False(b.TryGetValue(2000, 0, out _));
    }

    [Fact]
    public void WithoutNewRowsSourceEditsStayWhereTheyAre()
    {
        var b = new GridEditBuffer();
        b.SetCommitted(5, 0, new CellValue("low"));
        b.SetCommitted(900, 0, new CellValue("high"));

        b.ShiftInsertedRows(1000, 2000);
        Assert.True(b.TryGetValue(5, 0, out var low) && low.Text == "low");
        Assert.True(b.TryGetValue(900, 0, out var high) && high.Text == "high");
        Assert.False(b.TryGetValue(1900, 0, out _));

        b.ShiftInsertedRows(2000, 500);
        Assert.True(b.TryGetValue(5, 0, out low) && low.Text == "low");
        Assert.False(b.TryGetValue(900, 0, out _));
        Assert.Equal(0, b.InsertedRows);
    }

    [Fact]
    public void CommittingTheSentSnapshotKeepsLaterValuesPending()
    {
        var b = new GridEditBuffer();
        var row = b.InsertRow(10);
        b.SetValue(row, 0, new CellValue("a"));
        b.SetValue(row, 1, new CellValue("b"));
        var sent = new Dictionary<int, CellValue>(b.PendingValues);
        b.SetValue(row, 1, new CellValue("b2"));
        b.SetValue(row, 2, new CellValue("c"));

        Assert.True(b.Commit(sent));

        Assert.Equal(row, b.PendingRow);
        Assert.False(b.PendingIsInsert);
        Assert.False(b.IsModified(row, 0));
        Assert.True(b.IsModified(row, 1));
        Assert.True(b.IsModified(row, 2));
        Assert.True(b.TryGetCommitted(row, 1, out var committed) && committed.Text == "b");
        Assert.True(b.TryGetValue(row, 1, out var shown) && shown.Text == "b2");

        Assert.True(b.Commit(new Dictionary<int, CellValue>(b.PendingValues)));
        Assert.False(b.HasPending);
    }
}
