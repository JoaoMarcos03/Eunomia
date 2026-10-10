using System.Globalization;
using Avalonia.Headless.XUnit;
using Eunomia.Drivers.Abstractions;
using Eunomia.UI.Controls.Tree;
using Eunomia.UI.Features.QueryTab;
using MySqlConnector;
using static Eunomia.UI.Headless.Tests.RealServer;

namespace Eunomia.UI.Headless.Tests;

/// <summary>
/// Phase 13, gaps a HeidiSQL user notices: Explain current query, EXPLAIN of a process, Auto refresh,
/// Follow Foreign Key and Open URL, against the test server.
/// </summary>
public sealed class HeidiGapsTests
{
    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task ExplainAutoRefreshFollowForeignKeyEOpenUrl()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "Sem servidor de teste.");
        using var culture = new CultureScope(CultureInfo.InvariantCulture);
        var db = NewDatabaseName("eunomia_gp_");
        await ExecAsync(cs, $"CREATE DATABASE `{db}`; " +
            $"CREATE TABLE `{db}`.customers (id INT PRIMARY KEY, name VARCHAR(20), site VARCHAR(100)); " +
            $"INSERT INTO `{db}`.customers VALUES (1, 'Ana', 'https://example.com/ana'), (2, 'Rui', 'not a url'); " +
            $"CREATE TABLE `{db}`.orders (id INT PRIMARY KEY, customer_id INT, FOREIGN KEY (customer_id) REFERENCES `{db}`.customers (id)); " +
            $"INSERT INTO `{db}`.orders VALUES (10, 2), (11, 1);");
        await using var sleeper = new MySqlConnection(cs);
        await sleeper.OpenAsync();
        await using var idCommand = new MySqlCommand("SELECT CONNECTION_ID()", sleeper);
        var sleeperId = Convert.ToInt64(await idCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        await using var sleepCommand = new MySqlCommand("SELECT SLEEP(20) FROM DUAL", sleeper);
        var sleeping = sleepCommand.ExecuteScalarAsync();
        try
        {
            var (window, context, session, connector, _) = CreateShell(cs, db);
            Assert.True(await window.ConnectAsync(window, session, null));
            var model = window.Tree.Tree.Model!;
            var root = await WaitAsync(() => model.Roots.FirstOrDefault());
            var dbNode = await WaitAsync(() => root.Children?.FirstOrDefault(n => n.Name == db));
            await model.ExpandAsync(dbNode);

            // Explain current query: EXPLAIN of the statement under the cursor, in the results.
            window.Tabs.SelectedItem = window.Tabs.QueryTabs[0].Item;
            window.SqlEditor.Text = $"SELECT 1;\nSELECT * FROM `{db}`.orders WHERE id = 10;";
            window.SqlEditor.Editor.CaretOffset = window.SqlEditor.Text.Length - 3;
            Assert.True(await context.Commands.ExecuteAsync("query.explainCurrent"));
            var view = Assert.IsType<QueryTabView>(window.Tabs.QueryTabs[0].Content);
            var explain = await WaitAsync(() => view.ActiveGrid is { Source: { } s } g && s.Schema.Columns.Any(c => c.Name == "select_type") ? g : null);
            Assert.Equal("orders", explain.GetDisplayText(0, explain.Source!.Schema.Columns.ToList().FindIndex(c => c.Name == "table")));

            // Host tab › Processes: EXPLAIN query of the sleeping process opens a query tab with USE and EXPLAIN.
            // In a query tab, focusing the session does not switch tab (as in HeidiSQL): the Host tab is chosen.
            window.Tree.Tree.FocusedNode = root;
            window.Tabs.SelectedItem = window.Tabs.HostTab;
            var host = window.HostView;
            host.SelectedItem = host.ProcessesTab;
            var process = await WaitAsync(() => host.ProcessList.Roots.FirstOrDefault(n => n.Tag is ServerProcess { Info: { } info } && info.StartsWith("SELECT SLEEP", StringComparison.Ordinal)));
            host.ProcessList.FocusedNode = process;
            var tabsBefore = window.Tabs.QueryTabs.Count;
            Assert.True(await context.Commands.ExecuteAsync("host.explainProcess"));
            Assert.Equal(tabsBefore + 1, window.Tabs.QueryTabs.Count);
            Assert.Contains("EXPLAIN SELECT SLEEP(20) FROM DUAL;", window.SqlEditor.Text, StringComparison.Ordinal);

            // Auto refresh: turned on with Ctrl+F5, the interval changes through "Set interval", and each round reloads the Host tab list.
            window.Tabs.SelectedItem = window.Tabs.HostTab;
            window.AutoRefreshIntervalOverride = _ => Task.FromResult<string?>("3");
            Assert.True(await context.Commands.ExecuteAsync("view.autoRefreshInterval"));
            Assert.Equal(3, window.AutoRefreshSeconds);
            Assert.True(await context.Commands.ExecuteAsync("view.autoRefresh"));
            Assert.True(context.Commands.StateOf("view.autoRefresh").IsChecked);
            host.ProcessList.SetRoots([]);
            await window.AutoRefreshTickAsync();
            Assert.NotEmpty(host.ProcessList.Roots);
            Assert.True(await context.Commands.ExecuteAsync("view.autoRefresh"));
            Assert.False(context.Commands.StateOf("view.autoRefresh").IsChecked);

            // Follow Foreign Key: in the orders table, the customer_id column leads to customers filtered by the id.
            var orders = TreeModel.ObjectsOf(dbNode).Single(n => n.Name == "orders");
            window.Tree.Tree.FocusedNode = orders;
            window.Tabs.SelectedItem = window.Tabs.DataTab;
            await WaitAsync(() => window.DataGrid.RowCount == 2 ? "ok" : null);
            window.DataGrid.Focus();
            window.DataGrid.Selection.Focus(0, 1);
            // The row order is the server's (the FK index may be used): the expected customer comes from the focused row.
            var customerId = window.DataGrid.GetDisplayText(0, 1);
            Assert.True(context.Commands.StateOf("grid.followForeignKey").CanExecute);
            Assert.True(await context.Commands.ExecuteAsync("grid.followForeignKey"));
            await WaitAsync(() => window.Tree.Tree.FocusedNode is { Name: "customers" } && window.DataGrid.RowCount == 1 ? "ok" : null);
            Assert.Equal(customerId == "1" ? "Ana" : "Rui", window.DataGrid.GetDisplayText(0, 1));
            Assert.Equal($"`id` = {customerId}", window.DataView.FilterEditor.Text);

            // Open URL: only with an http/https value in the focused cell.
            var opened = new List<string>();
            window.OpenUrlOverride = opened.Add;
            await context.Commands.ExecuteAsync("filter.remove");
            await WaitAsync(() => window.DataGrid.RowCount == 2 ? "ok" : null);
            window.DataGrid.Focus();
            window.DataGrid.Selection.Focus(1, 2);
            Assert.False(context.Commands.StateOf("grid.openUrl").CanExecute);
            window.DataGrid.Selection.Focus(0, 2);
            Assert.True(await context.Commands.ExecuteAsync("grid.openUrl"));
            Assert.Equal(["https://example.com/ana"], opened);

            await connector.DisconnectAllAsync();
            window.Close();
        }
        finally
        {
            try
            {
                await ExecAsync(cs, $"KILL QUERY {sleeperId}");
                await sleeping;
            }
            catch (MySqlException)
            {
            }
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`");
        }
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task SqlHelpComF1NaPalavraDoCursor()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "Sem servidor de teste.");
        var (window, context, session, connector, _) = CreateShell(cs, string.Empty);
        Assert.True(await window.ConnectAsync(window, session, null));
        await WaitAsync(() => window.Tree.Tree.Model!.Roots.FirstOrDefault());
        window.Tabs.SelectedItem = window.Tabs.QueryTabs[0].Item;
        window.SqlEditor.Text = "SELECT 1";
        window.SqlEditor.Editor.CaretOffset = 3;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.SqlEditor.Editor.TextArea.Focus();

        Assert.True(window.TryExecuteShortcut("F1"));
        var help = await WaitAsync(() => window.SqlHelp is { TopicName: "SELECT" } h ? h : null);
        Assert.Contains("SELECT", help.DescriptionText, StringComparison.Ordinal);

        // Contents: the categories; opening a category lists its topics.
        await help.ShowAsync("contents");
        Assert.Contains(help.Entries, e => e is { Name: "Data Manipulation", IsCategory: true });
        await help.ShowAsync("Data Manipulation");
        Assert.Contains(help.Entries, e => e is { Name: "SELECT", IsCategory: false });
        await help.ShowAsync("no_such_keyword_xyz");
        Assert.Equal("No help found for \"no_such_keyword_xyz\".", help.TopicName);

        if (Environment.GetEnvironmentVariable("EUNOMIA_REVIEW_DIR") is { Length: > 0 } dir)
        {
            await help.ShowAsync("SELECT");
            Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(help);
            Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(help)!.Save(Path.Combine(dir, "sql-help.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        help.Close();
        await connector.DisconnectAllAsync();
        window.Close();
    }
}
