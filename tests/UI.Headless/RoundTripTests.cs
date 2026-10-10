using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Eunomia.Presentation.Logging;
using Eunomia.UI.Controls.Tree;
using static Eunomia.UI.Headless.Tests.RealServer;

namespace Eunomia.UI.Headless.Tests;

/// <summary>
/// Round trips per action (on a remote server each one costs the full latency): counted from the statements in the SQL
/// log. Opening a table in the Data tab takes three (USE of the database, definition, SELECT); another one from the same
/// database, two; the table editor asks for collations and engines only for the first table and then only the definition.
/// </summary>
public sealed class RoundTripTests
{
    private static TreeNode Child(TreeNode parent, string name) => parent.Children!.Single(n => n.Name == name);

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task UmaIdaPorPassoAoAbrirTabelas()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "Sem servidor de teste.");
        var db = NewDatabaseName("eunomia_rtt_");
        await ExecAsync(cs, $"CREATE DATABASE `{db}`; " +
            $"CREATE TABLE `{db}`.parent (id INT PRIMARY KEY, name VARCHAR(20), UNIQUE KEY uq (name)); " +
            $"CREATE TABLE `{db}`.child (id INT PRIMARY KEY, parent_id INT, KEY ix (parent_id), CONSTRAINT fk FOREIGN KEY (parent_id) REFERENCES `{db}`.parent (id)); " +
            $"CREATE TABLE `{db}`.c (id INT PRIMARY KEY); CREATE TABLE `{db}`.d (id INT PRIMARY KEY); " +
            $"INSERT INTO `{db}`.parent VALUES (1, 'a'); INSERT INTO `{db}`.child VALUES (1, 1), (2, 1);");
        try
        {
            var (window, _, session, connector, log) = CreateShell(cs, db);
            Assert.True(await window.ConnectAsync(window, session, null));
            var model = window.Tree.Tree.Model!;
            var root = await WaitAsync(() => model.Roots.FirstOrDefault());
            var dbNode = await WaitAsync(() => root.Children?.FirstOrDefault());
            await model.ExpandAsync(dbNode);
            var host = connector.Open.Single().Host;

            // SQL statements sent during an action, counted after everything settles (500 ms with nothing new).
            async Task<List<string>> SqlDuringAsync(Action action, Func<bool> done)
            {
                await QuietAsync();
                var last = log.Snapshot().LastOrDefault()?.Number ?? 0;
                action();
                await WaitAsync(() => done() ? "ok" : null);
                await QuietAsync();
                // The server clock (every 20 s) does not count.
                return [.. log.Snapshot().Where(l => l.Number > last && l.Kind == LogLineKind.Sql && !l.Text.Contains("SELECT NOW()", StringComparison.Ordinal)).Select(l => l.Text)];
            }

            async Task QuietAsync()
            {
                var seen = log.Snapshot().LastOrDefault()?.Number ?? 0;
                var quiet = System.Diagnostics.Stopwatch.StartNew();
                while (quiet.Elapsed < TimeSpan.FromMilliseconds(500))
                {
                    Dispatcher.UIThread.RunJobs();
                    await Task.Delay(20);
                    var now = log.Snapshot().LastOrDefault()?.Number ?? 0;
                    if (now != seen || host.IsPrimaryBusy)
                    {
                        seen = now;
                        quiet.Restart();
                    }
                }
            }

            window.ShowDataTab();
            var first = await SqlDuringAsync(() => window.Tree.Tree.FocusedNode = Child(dbNode, "child"),
                () => window.DataBrowser?.Table.Ref.Name == "child" && window.DataGrid.RowCount == 2);
            Assert.True(first.Count == 3, string.Join(" | ", first));
            Assert.StartsWith("USE ", first[0], StringComparison.Ordinal);
            Assert.Single(first, s => s.Contains("information_schema.COLUMNS", StringComparison.Ordinal));

            var second = await SqlDuringAsync(() => window.Tree.Tree.FocusedNode = Child(dbNode, "parent"),
                () => window.DataBrowser?.Table.Ref.Name == "parent" && window.DataGrid.RowCount == 1);
            Assert.True(second.Count == 2, string.Join(" | ", second));

            // The session's first editor reads the collations and the engines; waits for it to finish (not just 500 ms without a log: with
            // the machine under load the request had not gone out yet and the next choice cancelled it).
            window.Tabs.SelectedItem = window.Tabs.TableTab;
            await WaitAsync(() => (window.Tabs.TableTab.Content as UI.Features.TableEditor.TableEditorView)?.Design.Name == "parent" ? "ok" : null);
            await QuietAsync();
            var editor = await SqlDuringAsync(() => window.Tree.Tree.FocusedNode = Child(dbNode, "c"),
                () => (window.Tabs.TableTab.Content as UI.Features.TableEditor.TableEditorView)?.Design.Name == "c");
            Assert.True(editor.Count == 1, string.Join(" | ", editor));
            var again = await SqlDuringAsync(() => window.Tree.Tree.FocusedNode = Child(dbNode, "d"),
                () => (window.Tabs.TableTab.Content as UI.Features.TableEditor.TableEditorView)?.Design.Name == "d");
            Assert.True(again.Count == 1, string.Join(" | ", again));

            await connector.DisconnectAllAsync();
            window.Close();
        }
        finally
        {
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`");
        }
    }
}
