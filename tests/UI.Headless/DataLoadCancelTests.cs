using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Eunomia.App.Settings;
using Eunomia.UI.Controls.Tree;
using Eunomia.UI.Features.QueryTab;
using static Eunomia.UI.Headless.Tests.RealServer;

namespace Eunomia.UI.Headless.Tests;

/// <summary>
/// Slow load in the Data tab (a large table on a slow server; here a filter with <c>SLEEP</c> per row): choosing something
/// else in the tree or running a query cancels it, instead of the primary connection staying busy until the end (beta
/// feedback, 9 Oct 2026). Each slow load takes 25 s or more; whatever comes next must show up long before that.
/// </summary>
public sealed class DataLoadCancelTests
{
    private const string Digits = "(SELECT 0 n UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9)";

    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(10);

    private static TreeNode Child(TreeNode parent, string name) => parent.Children!.Single(n => n.Name == name);

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task ChoosingSomethingElseCancelsTheDataTabLoad()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "No test server.");
        var db = NewDatabaseName("eunomia_dl_");
        var db2 = NewDatabaseName("eunomia_dl2_");
        await ExecAsync(cs, $"CREATE DATABASE `{db}`; CREATE DATABASE `{db2}`; " +
            $"CREATE TABLE `{db}`.slow (id INT PRIMARY KEY); INSERT INTO `{db}`.slow SELECT a.n + 10 * b.n + 1 FROM {Digits} a, {Digits} b WHERE a.n + 10 * b.n < 40; " +
            $"CREATE TABLE `{db}`.tail (id INT PRIMARY KEY); INSERT INTO `{db}`.tail SELECT id FROM `{db}`.slow; " +
            $"CREATE TABLE `{db}`.small (id INT PRIMARY KEY); INSERT INTO `{db}`.small VALUES (1), (2), (3); " +
            $"CREATE TABLE `{db2}`.other (id INT PRIMARY KEY); INSERT INTO `{db2}`.other VALUES (1), (2);");
        try
        {
            var settings = new AppSettings();
            settings.Data.RowsPerStep = 5;
            var (window, context, session, connector, log) = CreateShell(cs, $"{db};{db2}", settings);
            Assert.True(await window.ConnectAsync(window, session, null));
            var model = window.Tree.Tree.Model!;
            var root = await WaitAsync(() => model.Roots.FirstOrDefault());
            await WaitAsync(() => root.Children?.Count == 2 ? "ok" : null);
            var dbNode = root.Children!.Single(n => n.Name == db);
            var db2Node = root.Children!.Single(n => n.Name == db2);
            await model.ExpandAsync(dbNode);
            await model.ExpandAsync(db2Node);
            var host = connector.Open.Single().Host;
            window.ShowDataTab();

            bool Showing(string name, int rows) =>
                window.DataBrowser?.Table.Ref.Name == name && window.DataGrid.RowCount == rows && !host.IsPrimaryBusy;

            async Task<TimeSpan> ShowAsync(TreeNode node, int rows)
            {
                var clock = Stopwatch.StartNew();
                window.Tree.Tree.FocusedNode = node;
                await WaitAsync(() => Showing(node.Name, rows) ? "ok" : null, TimeSpan.FromSeconds(60));
                return clock.Elapsed;
            }

            async Task FilterAsync(string filter)
            {
                window.DataView.FilterEditor.Text = filter;
                Assert.True(await context.Commands.ExecuteAsync("filter.apply"));
            }

            // "slow": each filtered row sleeps 5 s (first page of 5 rows: 25 s).
            await ShowAsync(Child(dbNode, "slow"), 5);
            await FilterAsync("SLEEP(5) = 0");
            await WaitAsync(() => host.IsPrimaryBusy ? "ok" : null);

            // A table from another database: the USE no longer waits for the load to finish.
            Assert.True(await ShowAsync(Child(db2Node, "other"), 2) < Quick);

            // The database node: the load ends right away and the primary connection is free.
            window.Tree.Tree.FocusedNode = Child(dbNode, "slow");
            await WaitAsync(() => host.IsPrimaryBusy ? "ok" : null);
            var clock = Stopwatch.StartNew();
            window.Tree.Tree.FocusedNode = dbNode;
            await WaitAsync(() => !host.IsPrimaryBusy ? "ok" : null, TimeSpan.FromSeconds(60));
            Assert.True(clock.Elapsed < Quick);

            // A query on the same session: Run is enabled and the load yields the connection to it. (From the database, the object opens in the Table tab.)
            window.Tree.Tree.FocusedNode = Child(dbNode, "slow");
            window.ShowDataTab();
            await WaitAsync(() => host.IsPrimaryBusy ? "ok" : null);
            window.Tabs.SelectedItem = window.Tabs.QueryTabs[0].Item;
            window.SqlEditor.Text = "SELECT 42 AS answer";
            Assert.True(context.Commands.StateOf("query.run").CanExecute);
            clock.Restart();
            Assert.True(await context.Commands.ExecuteAsync("query.run"));
            Assert.True(clock.Elapsed < Quick);
            var view = Assert.IsType<QueryTabView>(window.Tabs.QueryTabs[0].Content);
            Assert.Equal("42", view.ResultGrids[0].Control.GetDisplayText(0, 0));

            // Show all halfway (each row above 5 sleeps 1 s: 35 s) and another table from the same database.
            window.Tree.Tree.FocusedNode = Child(dbNode, "tail");
            window.ShowDataTab();
            await WaitAsync(() => Showing("tail", 5) ? "ok" : null);
            await FilterAsync("SLEEP(IF(id > 5, 1, 0)) = 0");
            await WaitAsync(() => Showing("tail", 5) && window.DataBrowser!.Options.Filter is { } f && f.StartsWith("SLEEP", StringComparison.Ordinal) ? "ok" : null);
            var showAll = context.Commands.ExecuteAsync("data.showAll").AsTask();
            await WaitAsync(() => host.IsPrimaryBusy ? "ok" : null);
            Assert.True(await ShowAsync(Child(dbNode, "small"), 3) < Quick);
            await showAll;

            // Going back to the abandoned table loads it again; no cancellation shows as an error.
            await ShowAsync(Child(dbNode, "tail"), 5);
            Assert.DoesNotContain(log.Snapshot(), l => l.Kind == Eunomia.Presentation.Logging.LogLineKind.Error);

            await connector.DisconnectAllAsync();
            window.Close();
        }
        finally
        {
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`; DROP DATABASE IF EXISTS `{db2}`");
        }
    }
}
