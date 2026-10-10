using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Eunomia.Engine.Results.Synthetic;
using Eunomia.UI.Controls.Grid;
using Eunomia.UI.Controls.Tree;
using Eunomia.UI.Shell;

namespace Eunomia.UI.Headless.Tests;

/// <summary>
/// Requests from 1 Oct 2026: free scroll with the middle button in the grid and double click on a
/// table in the tree inserting the name in the query (FAM-T07), without leaving the Query tab.
/// </summary>
public sealed class UserRequestsTests
{
    private static async Task<(Avalonia.Controls.Window Window, DataGridControl Grid)> GridAsync()
    {
        var source = new SyntheticRowSource();
        var view = new DataGridView();
        var window = new Avalonia.Controls.Window { Width = 900, Height = 500, Content = view };
        window.Show();
        view.Control.SetSource(source);
        await source.RequestRangeAsync(0, 4096);
        window.CaptureRenderedFrame();
        return (window, view.Control);
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task MiddleButtonDragScrollsDiagonallyAndStopsOnRelease()
    {
        var (window, grid) = await GridAsync();
        var origin = new Point(400, 250);

        window.MouseDown(origin, MouseButton.Middle);
        Assert.True(grid.AutoScroll.IsActive);
        window.MouseMove(new Point(500, 400), RawInputModifiers.MiddleMouseButton);
        grid.ApplyAutoScroll(0.5);

        Assert.True(grid.TopRow > 0);
        Assert.True(grid.ScrollX > 0);
        window.MouseUp(new Point(500, 400), MouseButton.Middle);
        Assert.False(grid.AutoScroll.IsActive);
        window.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task MiddleButtonSingleClickStaysActiveUntilTheNextClick()
    {
        var (window, grid) = await GridAsync();
        var origin = new Point(400, 250);

        window.MouseDown(origin, MouseButton.Middle);
        window.MouseUp(origin, MouseButton.Middle);
        Assert.True(grid.AutoScroll.IsActive);

        window.MouseMove(new Point(400, 100));
        grid.ApplyAutoScroll(0.2);
        Assert.Equal(0, grid.TopRow); // already at the top: scrolling up does not go below 0
        window.MouseMove(new Point(400, 450));
        grid.ApplyAutoScroll(0.5);
        Assert.True(grid.TopRow > 0);

        var row = grid.Selection.FocusedRow;
        window.MouseDown(new Point(300, 200), MouseButton.Left);
        window.MouseUp(new Point(300, 200), MouseButton.Left);
        Assert.False(grid.AutoScroll.IsActive);
        Assert.Equal(row, grid.Selection.FocusedRow); // the click that ends it does not select
        window.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task KeyEndsTheFreeScroll()
    {
        var (window, grid) = await GridAsync();
        grid.Focus();

        window.MouseDown(new Point(400, 250), MouseButton.Middle);
        window.MouseUp(new Point(400, 250), MouseButton.Middle);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);

        Assert.False(grid.AutoScroll.IsActive);
        window.Close();
    }

    private static async Task<(MainWindow Window, TreeNode Table, TreeNode Db)> MainAsync()
    {
        var window = new MainWindow(TestShell.CreateContext());
        window.Show();
        var model = window.Tree.Tree.Model!;
        await model.ExpandAsync(model.Roots[0]);
        var db = model.Roots[0].Children![5];
        await model.ExpandAsync(db);
        window.CaptureRenderedFrame();
        return (window, db.Children![0], db);
    }

    private static void DoubleClick(MainWindow window, TreeNode node, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        var tree = window.Tree.Tree;
        tree.FocusedNode = node;
        window.CaptureRenderedFrame();
        var index = tree.Model!.IndexOf(node) - tree.TopRow;
        var local = new Point(tree.Bounds.Width / 2, (index * DatabaseTreeControl.RowHeight) + 10);
        var p = tree.TranslatePoint(local, window)!.Value;
        window.MouseDown(p, MouseButton.Left, modifiers);
        window.MouseUp(p, MouseButton.Left, modifiers);
        window.MouseDown(p, MouseButton.Left, modifiers);
        window.MouseUp(p, MouseButton.Left, modifiers);
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task DoubleClickOnATableInsertsTheNameInTheQuery()
    {
        var (window, table, db) = await MainAsync();
        window.Tabs.SelectedItem = window.Tabs.QueryTabs[0].Item;
        window.SqlEditor.Text = "SELECT * FROM ";
        window.SqlEditor.Editor.CaretOffset = window.SqlEditor.Text.Length;

        DoubleClick(window, table);

        Assert.Same(window.Tabs.QueryTabs[0].Item, window.Tabs.SelectedItem);
        Assert.Equal("SELECT * FROM " + table.Name, window.SqlEditor.Text);
        Assert.Equal(window.SqlEditor.Text.Length, window.SqlEditor.Editor.CaretOffset);

        window.SqlEditor.InsertAtCaret(" JOIN ");
        DoubleClick(window, db.Children![1], RawInputModifiers.Shift);
        Assert.EndsWith($" JOIN {db.Name}.{db.Children[1].Name}", window.SqlEditor.Text, StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task ClickOnATableDoesNotLeaveTheQueryTab()
    {
        var (window, table, _) = await MainAsync();
        window.Tabs.SelectedItem = window.Tabs.QueryTabs[0].Item;

        window.Tree.Tree.FocusedNode = table;

        Assert.Same(window.Tabs.QueryTabs[0].Item, window.Tabs.SelectedItem);
        window.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task InTheDataTabDoubleClickDoesNotTouchTheQuery()
    {
        var (window, table, _) = await MainAsync();
        window.Tabs.SelectedIndex = 0;
        var before = window.SqlEditor.Text;

        DoubleClick(window, table);

        Assert.Equal(before, window.SqlEditor.Text);
        window.Close();
    }
}
