using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Eunomia.Engine.Results;
using Eunomia.UI.Controls.Grid;

namespace Eunomia.UI.Headless.Tests;

/// <summary>
/// Grid editing while rows stream in and a save runs: new rows follow the source, only what was sent is saved, the editor
/// follows its cell and does not steal the focus; plus the wheel, the SET editor colours and the cell text cache key.
/// </summary>
public sealed class GridEditRaceTests
{
    private static readonly ResultSchema Schema = new(
    [
        new ColumnInfo("id", "INT", TypeCategory.Integer, StorageKind.Int64, IsPrimaryKey: true, IsNullable: false),
        new ColumnInfo("name", "VARCHAR", TypeCategory.Text, StorageKind.String, false, true),
    ]);

    private const int BlockSize = 100;

    private static void Append(MaterializedRowSource source, int rows)
    {
        for (var added = 0; added < rows; added += BlockSize)
        {
            var first = source.RowCount.Count;
            var builder = new RowBlockBuilder(Schema, (int)(first / BlockSize), first, BlockSize);
            for (var r = 0; r < BlockSize; r++)
            {
                builder.SetInt64(r, 0, first + r);
                builder.SetString(r, 1, "n" + (first + r));
            }
            source.Append(builder.Build());
        }
        // The grid learns about the new rows on a posted job (OnDataArrived).
        Dispatcher.UIThread.RunJobs();
    }

    private static (Window Window, DataGridControl Grid, MaterializedRowSource Source) Show(Control? extra = null)
    {
        var source = new MaterializedRowSource(Schema, BlockSize);
        var grid = new DataGridControl { Culture = System.Globalization.CultureInfo.InvariantCulture, Height = 300 };
        var panel = new StackPanel();
        panel.Children.Add(grid);
        if (extra is not null)
        {
            panel.Children.Add(extra);
        }
        var window = new Window { Width = 700, Height = 400, Content = panel };
        window.Show();
        grid.SetSource(source);
        Append(source, BlockSize);
        window.CaptureRenderedFrame();
        grid.Focus();
        return (window, grid, source);
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task SaveOfANewRowLandsOnTheRowAfterMoreSourceRowsArrive()
    {
        var (window, grid, source) = Show();
        var save = new TaskCompletionSource<GridCommitResult>();
        grid.CommitHandler = _ => save.Task;
        var row = await grid.InsertRowAsync(new Dictionary<int, CellValue> { [1] = new("new") });
        Assert.Equal(100, row);

        var post = grid.PostAsync();
        Assert.True(grid.IsCommitting);
        Append(source, 500);
        Assert.Equal(600, grid.Edits.PendingRow);
        save.SetResult(new GridCommitResult(true));

        Assert.True(await post);
        Assert.False(grid.Edits.HasPending);
        Assert.True(grid.Edits.TryGetCommitted(600, 1, out var saved) && saved.Text == "new");
        Assert.False(grid.Edits.TryGetValue(100, 1, out _));
        window.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task FailedSaveReturnsTheFocusToTheShiftedRow()
    {
        var (window, grid, source) = Show();
        var save = new TaskCompletionSource<GridCommitResult>();
        grid.CommitHandler = _ => save.Task;
        await grid.InsertRowAsync(new Dictionary<int, CellValue> { [1] = new("new") });

        var post = grid.PostAsync();
        Append(source, 500);
        grid.FocusCell(3, 1);
        save.SetResult(GridCommitResult.Failed);

        Assert.False(await post);
        Assert.Equal(600, grid.Edits.PendingRow);
        Assert.Equal(600, grid.Selection.FocusedRow);
        window.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task ValueChangedDuringTheSaveStaysPending()
    {
        var (window, grid, _) = Show();
        var save = new TaskCompletionSource<GridCommitResult>();
        grid.CommitHandler = _ => save.Task;
        Assert.True(await grid.SetCellValueAsync(2, 1, new CellValue("sent")));

        var post = grid.PostAsync();
        grid.Edits.SetValue(2, 1, new CellValue("typed later"));
        save.SetResult(new GridCommitResult(true));

        Assert.True(await post);
        Assert.True(grid.Edits.HasPending);
        Assert.True(grid.Edits.IsModified(2, 1));
        Assert.True(grid.Edits.TryGetCommitted(2, 1, out var committed) && committed.Text == "sent");
        Assert.True(grid.Edits.TryGetValue(2, 1, out var shown) && shown.Text == "typed later");
        window.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task EditorDoesNotOpenWhileTheValueLoadsAndASaveStarts()
    {
        var (window, grid, _) = Show();
        var save = new TaskCompletionSource<GridCommitResult>();
        grid.CommitHandler = _ => save.Task;
        var provider = new TaskCompletionSource<GridCellEditor?>();
        grid.EditorProvider = (_, _, _) => provider.Task;
        Assert.True(await grid.SetCellValueAsync(2, 1, new CellValue("sent")));
        grid.FocusCell(2, 1);

        var begin = grid.BeginEditAsync();
        var post = grid.CommitPendingAsync();
        provider.SetResult(null);
        await begin;

        Assert.False(grid.IsEditing);
        save.SetResult(new GridCommitResult(true));
        Assert.True(await post);
        window.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task OpenEditorFollowsItsNewRowWhenMoreSourceRowsArrive()
    {
        var (window, grid, source) = Show();
        await grid.InsertRowAsync();
        grid.FocusCell(100, 1);
        await grid.BeginEditAsync();
        Assert.NotNull(grid.Editor);
        grid.Editor!.Text = "typed";

        Append(source, 200);
        grid.EndEdit(commit: true);

        Assert.Equal(300, grid.Edits.PendingRow);
        Assert.True(grid.Edits.TryGetValue(300, 1, out var value) && value.Text == "typed");
        window.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task ClosingTheEditorWhenTheFocusLeavesDoesNotTakeItBack()
    {
        var other = new TextBox();
        var (window, grid, _) = Show(other);
        grid.FocusCell(1, 1);
        await grid.BeginEditAsync();
        Assert.True(grid.IsEditing);

        other.Focus();

        Assert.False(grid.IsEditing);
        Assert.True(other.IsFocused);
        Assert.Same(other, window.FocusManager?.GetFocusedElement());
        window.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task OpenEditorMovesWithItsCellWhenTheGridScrolls()
    {
        var (window, grid, _) = Show();
        grid.FocusCell(5, 1);
        await grid.BeginEditAsync();
        window.CaptureRenderedFrame();
        var before = grid.EditorControl!.Bounds.Y;

        grid.TopRow = 2;
        window.CaptureRenderedFrame();

        Assert.Equal(before - (2 * grid.RowHeight), grid.EditorControl!.Bounds.Y, 3);
        window.Close();
    }

    [AvaloniaFact]
    public void FractionsOfAWheelNotchAddUp()
    {
        var (window, grid, _) = Show();
        grid.TopRow = 10;
        var point = grid.TranslatePoint(new Point(200, 100), window)!.Value;

        // A precision touchpad: five fifths of a notch down are one notch (three rows).
        for (var i = 0; i < 5; i++)
        {
            window.MouseWheel(point, new Vector(0, -0.2));
        }

        Assert.Equal(13, grid.TopRow);
        window.Close();
    }

    [AvaloniaFact]
    public void SetEditorUsesTheThemeColours()
    {
        var control = new SetCellEditor(["a", "b"]).Create("a", 12, FontFamily.Default);
        var window = new Window { Width = 300, Height = 300, Content = control, RequestedThemeVariant = ThemeVariant.Dark };
        window.Show();

        var border = Assert.IsType<Border>(control);
        Assert.Same(GridPalette.Dark.Background, border.Background);
        window.RequestedThemeVariant = ThemeVariant.Light;
        Assert.Same(GridPalette.Light.Background, border.Background);
        window.Close();
    }

    [AvaloniaFact]
    public void CellTextCacheKeepsCellsAndDrawingOptionsApart()
    {
        var cache = new CellTextCache(1000);
        var typeface = new Typeface("Tahoma");
        var glyphs = new GlyphRunFactory(typeface, 11);
        using var bitmap = new RenderTargetBitmap(new PixelSize(200, 100));
        using var context = bitmap.CreateDrawingContext();
        void Draw(long row, int column, string text, bool alignRight = false, Typeface? face = null) =>
            cache.Draw(context, row, column, text, glyphs, face ?? typeface, 11, Brushes.Black, alignRight, 100, default, 20);

        // Column 4096 of row 0 and column 0 of row 1 used to share a key and replaced each other every frame.
        Draw(0, 4096, "a");
        Draw(1, 0, "b");
        Draw(0, 4096, "a");
        Draw(1, 0, "b");
        Assert.Equal(2, cache.Created);

        // The same text with another alignment or typeface is a new entry.
        Draw(1, 0, "b", alignRight: true);
        Assert.Equal(3, cache.Created);
        Draw(1, 0, "b", alignRight: true, face: new Typeface("Tahoma", FontStyle.Normal, FontWeight.Bold));
        Assert.Equal(4, cache.Created);
    }
}
