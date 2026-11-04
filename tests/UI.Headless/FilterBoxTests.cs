using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Eunomia.App.Objects;
using Eunomia.App.Settings;
using Eunomia.UI.Controls.Common.Input;
using Eunomia.UI.Controls.Tree;
using Eunomia.UI.Shell;
using static Eunomia.UI.Headless.Tests.RealServer;

namespace Eunomia.UI.Headless.Tests;

/// <summary>
/// Filter boxes: a clear button on the right as in HeidiSQL, and the bottom filter panel also on the table editor columns
/// and on the Host sub-tabs, with the right rows when the view is filtered.
/// </summary>
public sealed class FilterBoxTests
{
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static TreeNode Child(TreeNode parent, string name) => parent.Children!.Single(n => n.Name == name);

    [AvaloniaFact]
    public void ClearButtonShowsOnlyWithTextAndClearsTheBox()
    {
        var tree = new DatabaseTreeView();
        var window = new Window { Content = tree, Width = 300, Height = 200 };
        window.Show();
        foreach (var box in new[] { tree.DatabaseFilterBox, tree.TableFilterBox })
        {
            var clear = Assert.IsType<Button>(FilterBoxes.ClearButtonOf(box));
            Assert.False(clear.IsVisible);
            box.Text = "app_.*;log_\\d";
            Assert.True(clear.IsVisible);
            Assert.False(clear.Focusable);
            Assert.Equal(FilterBoxes.ClearCaption, Avalonia.Automation.AutomationProperties.GetName(clear));
            Click(clear);
            Assert.Equal(string.Empty, box.Text);
            Assert.False(clear.IsVisible);
            Assert.True(box.IsFocused);
        }

        // The main window's bottom filter panel has the same button.
        var shell = new MainWindow(TestShell.CreateContext());
        shell.Show();
        shell.LocalFilterBox.Text = "x";
        Assert.True(FilterBoxes.ClearButtonOf(shell.LocalFilterBox)!.IsVisible);
        shell.Close();
        window.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task BottomFilterWorksOnTableEditorColumnsAndHostSubTabs()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "No test server.");
        using var culture = new CultureScope(CultureInfo.InvariantCulture);
        var db = NewDatabaseName("eunomia_fb_");
        await ExecAsync(cs, $"CREATE DATABASE `{db}`; CREATE TABLE `{db}`.products (id INT PRIMARY KEY, name VARCHAR(40), shipping_rates LONGTEXT COMMENT 'rates', created DATETIME);");
        try
        {
            var (window, _, session, _, _) = CreateShell(cs, db);
            Assert.True(await window.ConnectAsync(window, session, null));
            var model = window.Tree.Tree.Model!;
            var root = await WaitAsync(() => model.Roots.FirstOrDefault());
            var dbNode = await WaitAsync(() => root.Children?.FirstOrDefault(n => n.Name == db));
            await model.ExpandAsync(dbNode);
            window.Tree.Tree.FocusedNode = Child(dbNode, "products");
            await WaitAsync(() => ReferenceEquals(window.Tabs.SelectedItem, window.Tabs.DataTab) ? "ok" : null);
            window.Tabs.SelectedItem = window.Tabs.TableTab;
            var editor = await WaitAsync(() => window.TableEditor);
            Dispatcher.UIThread.RunJobs();

            // Before, with the Table tab showing, the filter did nothing.
            window.LocalFilterBox.Text = "shipping";
            await window.ApplyLocalFilterAsync();
            Assert.Equal(["shipping_rates"], editor.VisibleColumnNames);
            Assert.Equal("1 out of 4 matching. 3 hidden.", window.LocalFilterInfo.Text);
            window.LocalFilterBox.Text = "^(id|rates)$";
            // TextChanged (which stores the list's text) arrives later, as when typing.
            Dispatcher.UIThread.RunJobs();
            await window.ApplyLocalFilterAsync();
            Assert.Equal(["id", "shipping_rates"], editor.VisibleColumnNames);

            // A new column goes through the active filter.
            editor.Design.Columns.Add(new ColumnDesign { Name = "id", DataType = "INT" });
            editor.Refresh();
            Assert.Equal(["id", "shipping_rates", "id"], editor.VisibleColumnNames);
            editor.Design.Columns.RemoveAt(4);
            editor.Refresh();

            // The text is kept per tab: the box is empty in the Data tab; back in the Table tab the filter returns, applied
            // to the rebuilt editor.
            window.ShowDataTab();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(string.Empty, window.LocalFilterBox.Text);
            window.Tabs.SelectedItem = window.Tabs.TableTab;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("^(id|rates)$", window.LocalFilterBox.Text);
            editor = await WaitAsync(() => window.TableEditor);
            Assert.Equal(["id", "shipping_rates"], editor.VisibleColumnNames);

            // The clear button shows everything at once (without waiting the 500 ms).
            Click(FilterBoxes.ClearButtonOf(window.LocalFilterBox)!);
            Assert.Equal(4, editor.VisibleColumnNames.Count);
            Assert.Equal(string.Empty, window.LocalFilterInfo.Text);

            // Host: the filter goes to the visible sub-tab (before, Variables and Status filtered the Databases list).
            window.Tree.Tree.FocusedNode = root;
            await WaitAsync(() => ReferenceEquals(window.Tabs.SelectedItem, window.Tabs.HostTab) ? "ok" : null);
            var host = window.HostView;
            await WaitAsync(() => host.DatabaseList.Roots.FirstOrDefault(n => n[0] == db));
            host.SelectedItem = host.VariablesTab;
            await WaitAsync(() => host.VariableList.Roots.FirstOrDefault(n => n[0] == "version"));
            Dispatcher.UIThread.RunJobs();
            var databases = host.DatabaseList.VisibleNodes.Count;
            window.LocalFilterBox.Text = "^version$";
            await window.ApplyLocalFilterAsync();
            Assert.Equal(["version"], host.VariableList.VisibleNodes.Select(n => n[0]));
            Assert.Equal(databases, host.DatabaseList.VisibleNodes.Count);
            host.SelectedItem = host.StatusTab;
            await WaitAsync(() => host.StatusList.Roots.FirstOrDefault(n => n[0] == "Uptime"));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(string.Empty, window.LocalFilterBox.Text);
            window.LocalFilterBox.Text = "^uptime$";
            await window.ApplyLocalFilterAsync();
            Assert.Equal(["Uptime"], host.StatusList.VisibleNodes.Select(n => n[0]));
            window.Close();
        }
        finally
        {
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`");
        }
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task WithTheLocalFilterQuickFilterUsesTheRightRowAndNextClearsTheFilter()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "No test server.");
        using var culture = new CultureScope(CultureInfo.InvariantCulture);
        var db = NewDatabaseName("eunomia_fq_");
        await ExecAsync(cs, $"CREATE DATABASE `{db}`; CREATE TABLE `{db}`.t (id INT PRIMARY KEY, name VARCHAR(20)); " +
            $"INSERT INTO `{db}`.t VALUES (1, 'ana'), (2, 'rui'), (3, 'eva'), (4, 'rita'), (5, 'ze');");
        try
        {
            var settings = new AppSettings();
            settings.Data.RowsPerStep = 4;
            var (window, context, session, _, _) = CreateShell(cs, db, settings);
            Assert.True(await window.ConnectAsync(window, session, null));
            var model = window.Tree.Tree.Model!;
            var root = await WaitAsync(() => model.Roots.FirstOrDefault());
            var dbNode = await WaitAsync(() => root.Children?.FirstOrDefault(n => n.Name == db));
            await model.ExpandAsync(dbNode);
            window.ShowDataTab();
            window.Tree.Tree.FocusedNode = Child(dbNode, "t");
            await WaitAsync(() => window.DataGrid.RowCount == 4 ? "ok" : null);

            // Only "rui" and "rita": grid row 0 is browser row 1.
            window.LocalFilterBox.Text = "^r";
            await window.ApplyLocalFilterAsync();
            Assert.Equal(2, window.DataGrid.RowCount);
            window.DataGrid.Focus();
            window.DataGrid.Selection.Focus(0, 1);
            Assert.True(await context.Commands.ExecuteAsync("quickFilter.focused1"));
            await WaitAsync(() => window.DataBrowser!.Options.Filter == "`name` IN ('rui')" ? "ok" : null);
            Assert.True(await context.Commands.ExecuteAsync("filter.remove"));
            await WaitAsync(() => window.DataBrowser!.Options.Filter is null && window.DataGrid.RowCount == 4 ? "ok" : null);

            // Next reloads: the local filter is cleared and the new rows show up.
            window.LocalFilterBox.Text = "^r";
            await window.ApplyLocalFilterAsync();
            Assert.Equal(2, window.DataGrid.RowCount);
            Assert.True(await context.Commands.ExecuteAsync("data.showNext"));
            await WaitAsync(() => window.DataGrid.RowCount == 5 ? "ok" : null);
            Assert.Equal(string.Empty, window.LocalFilterBox.Text);
            Assert.Equal(string.Empty, window.LocalFilterInfo.Text);
            Assert.Equal(4, window.DataGrid.Selection.FocusedRow);
            Assert.Equal("ze", window.DataGrid.GetDisplayText(4, 1));
            window.Close();
        }
        finally
        {
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`");
        }
    }
}
