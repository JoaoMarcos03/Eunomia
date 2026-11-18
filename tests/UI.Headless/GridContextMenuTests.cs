using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Eunomia.UI.Controls.Common.Text;
using Eunomia.UI.Controls.Grid;
using Eunomia.UI.Controls.Tree;
using Eunomia.UI.Features.QueryTab;
using Eunomia.UI.Shell;
using MySqlConnector;
using static Eunomia.UI.Headless.Tests.RealServer;

namespace Eunomia.UI.Headless.Tests;

/// <summary>
/// Commands chosen in the grid context menus with the mouse and the keyboard, through the real popups. The menu takes the
/// keyboard focus when it opens, so the commands must still act on the grid the menu was opened on (and never on the
/// Data tab grid when the menu belongs to a query result).
/// </summary>
public sealed class GridContextMenuTests
{
    private const string Before = "clipboard before";

    private static async Task<string?> ScalarAsync(string cs, string sql)
    {
        await using var connection = new MySqlConnection(cs);
        await connection.OpenAsync();
        await using var cmd = new MySqlCommand(sql, connection);
        return Convert.ToString(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static TreeNode Child(TreeNode parent, string name) => parent.Children!.Single(n => n.Name == name);

    private static MenuItem Item(ItemsControl parent, string caption) =>
        parent.Items.OfType<MenuItem>().Single(i => i.Header is string h && (h == caption || h == LiteralText.Escaped(caption)));

    private static void Key(Window window, Key key, PhysicalKey physical)
    {
        window.KeyPress(key, RawInputModifiers.None, physical, null);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Opens the submenus of <paramref name="path"/> and clicks (or presses Enter on) its last item.</summary>
    internal static async Task ChooseAsync(Window window, ContextMenu menu, bool keyboard, params string[] path)
    {
        Assert.True(menu.IsOpen);
        ItemsControl parent = menu;
        for (var depth = 0; depth < path.Length; depth++)
        {
            var item = Item(parent, path[depth]);
            var last = depth == path.Length - 1;
            if (keyboard)
            {
                // Down until the item has the focus (a menu opened again starts from the item chosen the last time).
                Assert.True(item.IsEnabled, $"'{path[depth]}' is disabled.");
                for (var i = 0; i <= parent.ItemCount && !ReferenceEquals(window.FocusManager!.GetFocusedElement(), item); i++)
                {
                    Key(window, Avalonia.Input.Key.Down, PhysicalKey.ArrowDown);
                }
                Assert.Same(item, window.FocusManager!.GetFocusedElement());
                Key(window, last ? Avalonia.Input.Key.Enter : Avalonia.Input.Key.Right, last ? PhysicalKey.Enter : PhysicalKey.ArrowRight);
            }
            else
            {
                Assert.True(item.IsEnabled, $"'{path[depth]}' is disabled.");
                var top = TopLevel.GetTopLevel(item)!;
                var point = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), top)!.Value;
                top.MouseMove(point, RawInputModifiers.None);
                Dispatcher.UIThread.RunJobs();
                if (last)
                {
                    top.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
                    top.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
                    Dispatcher.UIThread.RunJobs();
                }
            }
            if (!last)
            {
                await WaitAsync(() => item.IsSubMenuOpen ? "open" : null);
                parent = item;
            }
        }
        await WaitAsync(() => menu.IsOpen ? null : "closed");
    }

    private static async Task<string> ClipboardChangedAsync(Window window)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        string text;
        do
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
            text = await window.Clipboard!.TryGetTextAsync() ?? string.Empty;
        }
        while (text == Before && DateTime.UtcNow < deadline);
        return text;
    }

    private static void RightClick(Window window, DataGridControl grid, long row, int column)
    {
        var point = grid.TranslatePoint(grid.CellBounds(row, column).Center, window)!.Value;
        window.MouseDown(point, MouseButton.Right, RawInputModifiers.None);
        window.MouseUp(point, MouseButton.Right, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Connects with the table <c>t</c> (rows 'datatab' and 'second'), optionally shown in the Data tab.</summary>
    private static async Task<(MainWindow Window, ShellContext Context)> OpenAsync(string cs, string db, bool dataTab)
    {
        await ExecAsync(cs, $"CREATE DATABASE `{db}`; CREATE TABLE `{db}`.t (id INT PRIMARY KEY, name VARCHAR(20)); INSERT INTO `{db}`.t VALUES (1, 'datatab'), (2, 'second');");
        var (window, context, session, _, _) = CreateShell(cs, db);
        Assert.True(await window.ConnectAsync(window, session, null));
        var model = window.Tree.Tree.Model!;
        var root = await WaitAsync(() => model.Roots.FirstOrDefault());
        if (dataTab)
        {
            var dbNode = await WaitAsync(() => root.Children?.FirstOrDefault(n => n.Name == db));
            await model.ExpandAsync(dbNode);
            window.ShowDataTab();
            window.Tree.Tree.FocusedNode = Child(dbNode, "t");
            await WaitAsync(() => window.DataGrid.RowCount == 2 ? "ok" : null);
        }
        return (window, context);
    }

    private static async Task<DataGridControl> RunQueryAsync(MainWindow window, ShellContext context)
    {
        window.Tabs.SelectedItem = window.Tabs.QueryTabs[0].Item;
        var view = Assert.IsType<QueryTabView>(window.Tabs.QueryTabs[0].Content);
        window.SqlEditor.Text = "SELECT 'from-query' AS v";
        Assert.True(await context.Commands.ExecuteAsync("query.run"));
        var grid = view.ActiveGrid!;
        await grid.PendingAutoSize;
        window.CaptureRenderedFrame();
        await window.Clipboard!.SetTextAsync(Before);
        return grid;
    }

    [AvaloniaTheory(Timeout = UiTimeouts.Default)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CopyAsFromTheResultMenuCopiesTheResult(bool dataTabOpen, bool keyboard)
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "No test server.");
        using var culture = new CultureScope(CultureInfo.InvariantCulture);
        var db = NewDatabaseName("eunomia_gcm_");
        try
        {
            var (window, context) = await OpenAsync(cs, db, dataTabOpen);
            var grid = await RunQueryAsync(window, context);

            RightClick(window, grid, 0, 0);
            await ChooseAsync(window, window.LastResultGridMenu!, keyboard, "Copy as", "Excel CSV");

            var copied = await ClipboardChangedAsync(window);
            Assert.Contains("from-query", copied, StringComparison.Ordinal);
            Assert.DoesNotContain("datatab", copied, StringComparison.Ordinal);
            Assert.Contains(context.Log.Snapshot(), l => l.Text.Contains("copied to clipboard as Excel CSV", StringComparison.Ordinal));
            window.Close();
        }
        finally
        {
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`");
        }
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task CopyFromTheResultMenuCopiesTheResultCell()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "No test server.");
        using var culture = new CultureScope(CultureInfo.InvariantCulture);
        var db = NewDatabaseName("eunomia_gcm_");
        try
        {
            var (window, context) = await OpenAsync(cs, db, dataTab: true);
            var grid = await RunQueryAsync(window, context);

            RightClick(window, grid, 0, 0);
            await ChooseAsync(window, window.LastResultGridMenu!, keyboard: false, "Copy");

            Assert.Equal("from-query", (await ClipboardChangedAsync(window)).TrimEnd());
            window.Close();
        }
        finally
        {
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`");
        }
    }

    [AvaloniaTheory(Timeout = UiTimeouts.Default)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DataGridMenuActsOnTheClickedCell(bool keyboard)
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "No test server.");
        using var culture = new CultureScope(CultureInfo.InvariantCulture);
        var db = NewDatabaseName("eunomia_gcm_");
        try
        {
            var (window, _) = await OpenAsync(cs, db, dataTab: true);
            var grid = window.DataGrid;
            window.CaptureRenderedFrame();

            // Insert value ▸ NULL on the clicked cell, then Post.
            RightClick(window, grid, 1, 1);
            Assert.Equal((1L, 1), (grid.Selection.FocusedRow, grid.Selection.FocusedColumn));
            await ChooseAsync(window, grid.ContextMenu!, keyboard, "Insert value", "NULL");
            Assert.True(grid.Edits.HasPending);
            Assert.True(await grid.PostAsync());
            await WaitAsync(() => grid.Edits.HasPending ? null : "posted");
            Assert.Equal("NULL", await ScalarAsync(cs, $"SELECT IFNULL(name, 'NULL') FROM `{db}`.t WHERE id = 2"));

            // Copy as ▸ SQL INSERTs of the clicked row, with the value just saved (the grid has not reloaded).
            await window.Clipboard!.SetTextAsync(Before);
            RightClick(window, grid, 1, 0);
            await ChooseAsync(window, grid.ContextMenu!, keyboard, "Copy as", "SQL INSERTs");
            var inserts = await ClipboardChangedAsync(window);
            Assert.Contains("(2, NULL)", inserts, StringComparison.Ordinal);
            window.Close();
        }
        finally
        {
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`");
        }
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task MainMenuCopyActsOnTheGridThatHadTheFocus()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "No test server.");
        using var culture = new CultureScope(CultureInfo.InvariantCulture);
        var db = NewDatabaseName("eunomia_gcm_");
        try
        {
            var (window, context) = await OpenAsync(cs, db, dataTab: true);
            var grid = await RunQueryAsync(window, context);
            grid.Focus();
            grid.FocusCell(0, 0);

            // Edit ▸ Copy: hovering the items moves the focus into the menu.
            var edit = Item(window.GetVisualDescendants().OfType<Menu>().First(), "_Edit");
            var top = TopLevel.GetTopLevel(edit)!;
            var point = edit.TranslatePoint(new Point(edit.Bounds.Width / 2, edit.Bounds.Height / 2), top)!.Value;
            top.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
            top.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
            await WaitAsync(() => edit.IsSubMenuOpen ? "open" : null);
            var copy = Item(edit, "Copy");
            var popup = TopLevel.GetTopLevel(copy)!;
            var at = copy.TranslatePoint(new Point(copy.Bounds.Width / 2, copy.Bounds.Height / 2), popup)!.Value;
            popup.MouseMove(at, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.NotSame(grid, window.FocusManager!.GetFocusedElement());
            Assert.Same(grid, window.FocusedElement);
            popup.MouseDown(at, MouseButton.Left, RawInputModifiers.None);
            popup.MouseUp(at, MouseButton.Left, RawInputModifiers.None);

            Assert.Equal("from-query", (await ClipboardChangedAsync(window)).TrimEnd());
            window.Close();
        }
        finally
        {
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`");
        }
    }
}
