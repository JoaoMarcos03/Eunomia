using System.Globalization;
using Avalonia.Headless.XUnit;
using Eunomia.Drivers.Abstractions;
using Eunomia.Presentation.Objects;
using Eunomia.UI.Controls.Common.TreeList;
using Eunomia.UI.Controls.Tree;
using MySqlConnector;
using static Eunomia.UI.Headless.Tests.RealServer;

namespace Eunomia.UI.Headless.Tests;

/// <summary>Host tab (Databases, Processes) and Database tab with real data (phase 4; section 4.9).</summary>
public sealed class HostDatabaseTabTests
{
    private static TreeNode Child(TreeNode parent, string name) => parent.Children!.Single(n => n.Name == name);

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task HostTabListsDatabasesFetchesItemsAndKillsProcesses()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "No test server.");
        var db = NewDatabaseName("eunomia_ht_");
        await ExecAsync(cs, $"CREATE DATABASE `{db}`; CREATE TABLE `{db}`.a (id INT); CREATE TABLE `{db}`.b (id INT); CREATE VIEW `{db}`.v AS SELECT id FROM `{db}`.a;");
        await using var victim = new MySqlConnection(cs);
        await victim.OpenAsync();
        await using var idCommand = new MySqlCommand("SELECT CONNECTION_ID()", victim);
        var victimId = Convert.ToInt64(await idCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        try
        {
            var (window, context, session, connector, _) = CreateShell(cs, db);
            window.ConfirmOverride = (_, _, _) => Task.FromResult((true, false));
            Assert.True(await window.ConnectAsync(window, session, null));
            var model = window.Tree.Tree.Model!;
            var root = await WaitAsync(() => model.Roots.FirstOrDefault());
            await WaitAsync(() => root.Children?.FirstOrDefault());
            window.Tree.Tree.FocusedNode = root;
            await WaitAsync(() => ReferenceEquals(window.Tabs.SelectedItem, window.Tabs.HostTab) ? "ok" : null);

            // Databases: the database shows; without a cache, the counts stay empty until "Fetch database items".
            var host = window.HostView;
            var row = await WaitAsync(() => host.DatabaseList.Roots.FirstOrDefault(n => n[0] == db));
            Assert.Equal(string.Empty, row[4]);
            host.DatabaseList.FocusedNode = row;
            Assert.True(await context.Commands.ExecuteAsync("host.fetchDatabaseItems"));
            var fetched = await WaitAsync(() => host.DatabaseList.Roots.FirstOrDefault(n => n[0] == db && n[4] == "2"));
            Assert.Equal("3", fetched[2]);
            Assert.Equal("1", fetched[5]);
            Assert.StartsWith("Databases (", HeaderText(host.DatabasesTab), StringComparison.Ordinal);

            // Variables: session (main connection) and global side by side; a different session value is highlighted.
            await connector.Open[0].Host.RunPrimaryAsync((h, ct) => h.ExecuteNonQueryAsync("SET SESSION sort_buffer_size = 1048576", ct), CancellationToken.None);
            host.SelectedItem = host.VariablesTab;
            var sortBuffer = await WaitAsync(() => host.VariableList.Roots.FirstOrDefault(n => n[0] == "sort_buffer_size"));
            Assert.Equal("1048576", sortBuffer[1]);
            Assert.NotEqual(sortBuffer[1], sortBuffer[2]);
            Assert.Equal([1], sortBuffer.Highlighted!);
            Assert.StartsWith("Variables (", HeaderText(host.VariablesTab), StringComparison.Ordinal);

            // Status: Uptime with averages; Command-Statistics: Com_* counters in descending order, with percentages.
            host.SelectedItem = host.StatusTab;
            var uptime = await WaitAsync(() => host.StatusList.Roots.FirstOrDefault(n => n[0] == "Uptime"));
            Assert.NotEqual(string.Empty, uptime[3]);
            host.SelectedItem = host.CommandStatisticsTab;
            var select = await WaitAsync(() => host.CommandList.VisibleNodes.FirstOrDefault(n => n[0] == "Select"));
            Assert.EndsWith(" %", select[4], StringComparison.Ordinal);
            var counts = host.CommandList.VisibleNodes.Select(n => ((CommandStatisticsRow)n.Tag!).Count).ToList();
            Assert.Equal(counts.OrderByDescending(c => c), counts);

            // Processes: kills another connection, never the session's own.
            host.SelectedItem = host.ProcessesTab;
            var process = await WaitAsync(() => host.ProcessList.Roots.FirstOrDefault(n => n.Tag is ServerProcess p && p.Id == victimId));
            host.ProcessList.FocusedNode = process;
            Assert.True(await context.Commands.ExecuteAsync("host.killProcess"));
            await WaitAsync(() => host.ProcessList.Roots.Count > 0 && host.ProcessList.Roots.All(n => n.Tag is ServerProcess p && p.Id != victimId) ? "ok" : null);
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await using var again = new MySqlCommand("SELECT 1", victim);
                await again.ExecuteScalarAsync();
            });

            await connector.DisconnectAllAsync();
            window.Close();
        }
        finally
        {
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`");
        }
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task DatabaseTabListsRenamesEmptiesAndOpensObjects()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "No test server.");
        var db = NewDatabaseName("eunomia_dt_");
        await ExecAsync(cs, $"CREATE DATABASE `{db}`; CREATE TABLE `{db}`.a (id INT); CREATE TABLE `{db}`.b (id INT); INSERT INTO `{db}`.a VALUES (1), (2); " +
            $"INSERT INTO `{db}`.b VALUES (3); CREATE VIEW `{db}`.v AS SELECT id FROM `{db}`.a;");
        try
        {
            var (window, context, session, connector, _) = CreateShell(cs, db);
            var questions = new List<string>();
            window.ConfirmOverride = (_, message, _) =>
            {
                questions.Add(message);
                return Task.FromResult((true, true));
            };
            Assert.True(await window.ConnectAsync(window, session, null));
            var model = window.Tree.Tree.Model!;
            var root = await WaitAsync(() => model.Roots.FirstOrDefault());
            var dbNode = await WaitAsync(() => root.Children?.FirstOrDefault());
            window.Tree.Tree.FocusedNode = dbNode;
            await WaitAsync(() => ReferenceEquals(window.Tabs.SelectedItem, window.Tabs.DatabaseTab) ? "ok" : null);

            var view = window.DatabaseView;
            var list = view.List;
            await WaitAsync(() => list.Roots.Count == 3 ? "ok" : null);
            Assert.Equal(["a", "b", "v"], list.VisibleNodes.Select(n => n[0]));
            Assert.Equal(["Name", "Rows", "Size", "Created", "Updated", "Engine", "Comment", "Type"], view.VisibleColumns);
            Assert.Equal($"{db}: 3 objects (2 tables, 1 views)", context.Status.Info);

            // Local filter panel on the lists too.
            window.LocalFilterBox.Text = "^V$";
            await window.ApplyLocalFilterAsync();
            Assert.Equal(["v"], list.VisibleNodes.Select(n => n[0]));
            Assert.Equal("1 out of 3 matching. 2 hidden.", window.LocalFilterInfo.Text);
            window.LocalFilterBox.Text = string.Empty;
            await window.ApplyLocalFilterAsync();
            Assert.Equal(3, list.VisibleNodes.Count);

            // Empty table(s) on the list's multi-selection (Ctrl+A), with the list focused.
            list.Focus();
            list.SelectAll();
            Assert.Equal(3, view.SelectedObjects.Count);
            Assert.True(await context.Commands.ExecuteAsync("object.emptyTables"));
            Assert.StartsWith("Empty 3 table(s) and/or view(s)?", questions[^1], StringComparison.Ordinal);
            await using (var check = new MySqlConnection(cs))
            {
                await check.OpenAsync();
                await using var count = new MySqlCommand($"SELECT (SELECT COUNT(*) FROM `{db}`.a) + (SELECT COUNT(*) FROM `{db}`.b)", check);
                Assert.Equal(0L, Convert.ToInt64(await count.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
            }

            // F2 rename: table renamed on the server, in the list and in the tree; a view too; the rest refuses.
            var b = await WaitAsync(() => list.Roots.FirstOrDefault(n => n[0] == "b"));
            await window.RenameObjectAsync(new TreeListRenameEventArgs(b, "b2"));
            await WaitAsync(() => list.Roots.Any(n => n[0] == "b2") ? "ok" : null);
            var dbNow = root.Children!.First();
            await model.ExpandAsync(dbNow);
            Assert.Equal(TreeNodeKind.Table, Child(dbNow, "b2").Kind);
            Assert.DoesNotContain(dbNow.Children!, n => n.Name == "b");

            // Enter on a row opens the object: focus in the tree and the Table tab.
            list.FocusedNode = list.Roots.First(n => n[0] == "a");
            list.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter });
            await WaitAsync(() => window.Tree.Tree.FocusedNode is { Name: "a", Kind: TreeNodeKind.Table } ? "ok" : null);
            await WaitAsync(() => ReferenceEquals(window.Tabs.SelectedItem, window.Tabs.TableTab) ? "ok" : null);

            await connector.DisconnectAllAsync();
            window.Close();
        }
        finally
        {
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`");
        }
    }

    private static string HeaderText(Avalonia.Controls.TabItem tab) =>
        ((Avalonia.Controls.StackPanel)tab.Header!).Children.OfType<Avalonia.Controls.TextBlock>().First().Text!;
}
