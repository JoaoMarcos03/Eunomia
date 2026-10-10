using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Eunomia.UI.Controls.Tree;
using Eunomia.UI.Shell.Views;
using static Eunomia.UI.Headless.Tests.RealServer;

namespace Eunomia.UI.Headless.Tests;

/// <summary><c>popupDB</c> against the real server (phase 4; appendix C §3.3): Drop, Empty, Select top rows, Create database, drag.</summary>
public sealed class TreeObjectCommandsTests
{
    private static TreeNode Child(TreeNode parent, string name) => parent.Children!.Single(n => n.Name == name);

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task DropEmptySelectTopRowsAndCreateDatabase()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "No test server.");
        var db = NewDatabaseName("eunomia_tc_");
        var created = db + "_new";
        await ExecAsync(cs, $"CREATE DATABASE `{db}`; CREATE TABLE `{db}`.people (id INT); INSERT INTO `{db}`.people VALUES (1), (2); CREATE TABLE `{db}`.old (id INT);");
        try
        {
            var (window, context, session, connector, log) = CreateShell(cs, string.Empty);
            var questions = new List<string>();
            window.ConfirmOverride = (_, message, verification) =>
            {
                questions.Add(message);
                return Task.FromResult((true, verification is not null));
            };
            Assert.True(await window.ConnectAsync(window, session, null));
            var model = window.Tree.Tree.Model!;
            var root = await WaitAsync(() => model.Roots.FirstOrDefault());
            var dbNode = await WaitAsync(() => root.Children?.FirstOrDefault(n => n.Name == db));
            await model.ExpandAsync(dbNode);

            // The context menu is popupDB, with its submenus.
            var menu = Assert.IsType<ContextMenu>(window.Tree.Tree.ContextMenu);
            var headers = menu.Items.OfType<MenuItem>().Select(i => i.Header as string).ToList();
            Assert.Contains("Create new", headers);
            Assert.Contains("Tree style options", headers);

            // Drop of a table: asks, drops it and focuses the database again.
            window.Tree.Tree.FocusedNode = Child(dbNode, "old");
            Assert.True(await context.Commands.ExecuteAsync("object.drop"));
            Assert.Contains($"Drop 1 object(s) in database \"{db}\"?", questions[^1], StringComparison.Ordinal);
            var refreshed = await WaitAsync(() => root.Children?.FirstOrDefault(n => n.Name == db) is { Children: { } c } d && c.All(n => n.Name != "old") ? d : null);
            Assert.Same(refreshed, window.Tree.Tree.FocusedNode);

            // Empty table(s) with the "Disable foreign key checks" box.
            window.Tree.Tree.FocusedNode = Child(refreshed, "people");
            Assert.True(await context.Commands.ExecuteAsync("object.emptyTables"));
            Assert.StartsWith("Empty 1 table(s) and/or view(s)?", questions[^1], StringComparison.Ordinal);
            Assert.Contains(log.Snapshot(), l => l.Text.StartsWith("SET @OLD_FOREIGN_KEY_CHECKS", StringComparison.Ordinal));

            // Select top 1000 rows: a new query tab with the SELECT, already run.
            var people = await WaitAsync(() => root.Children?.FirstOrDefault(n => n.Name == db)?.Children?.FirstOrDefault(n => n.Name == "people"));
            window.Tree.Tree.FocusedNode = people;
            var tabs = window.Tabs.QueryTabs.Count;
            Assert.True(await context.Commands.ExecuteAsync("object.selectTopRows"));
            Assert.Equal(tabs + 1, window.Tabs.QueryTabs.Count);
            Assert.Equal("SELECT * FROM `people` LIMIT 1000;", window.SqlEditor.Text);
            await WaitAsync(() => log.Snapshot().Any(l => l.Text.StartsWith("SELECT * FROM `people` LIMIT 1000", StringComparison.Ordinal)) ? "ok" : null);

            // Dragging a table to the editor: with Shift, qualified with the database.
            window.SqlEditor.Text = string.Empty;
            var item = new DataTransferItem();
            item.Set(DatabaseTreeControl.NodeFormat, people);
            var data = new DataTransfer();
            data.Add(item);
            var area = window.SqlEditor.Editor.TextArea;
            area.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, data, area, new Point(5, 5), KeyModifiers.Shift));
            Assert.Equal($"{db}.people", window.SqlEditor.Text);

            // Create database from the session node.
            window.Tree.Tree.FocusedNode = root;
            string? preselected = null;
            window.CreateDatabaseOverride = (collations, pre) =>
            {
                preselected = pre;
                return Task.FromResult<CreateDatabaseRequest?>(new CreateDatabaseRequest(created, "utf8mb4_unicode_ci"));
            };
            Assert.True(await context.Commands.ExecuteAsync("object.createDatabase"));
            Assert.NotNull(preselected);
            var newDb = await WaitAsync(() => window.Tree.Tree.FocusedNode is { Kind: TreeNodeKind.Database } f && f.Name == created ? f : null);
            Assert.Equal("utf8mb4_unicode_ci", context.Settings.Dialogs.CreateDatabaseCollation);

            // Drop database.
            Assert.True(await context.Commands.ExecuteAsync("object.drop"));
            Assert.StartsWith($"Drop Database \"{created}\"?", questions[^1], StringComparison.Ordinal);
            await WaitAsync(() => root.Children?.Any(n => n.Name == created) == false && root.Children.Count > 0 ? "ok" : null);
            Assert.NotEqual(newDb, window.Tree.Tree.FocusedNode);

            await connector.DisconnectAllAsync();
            window.Close();
        }
        finally
        {
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`; DROP DATABASE IF EXISTS `{created}`");
        }
    }
}
