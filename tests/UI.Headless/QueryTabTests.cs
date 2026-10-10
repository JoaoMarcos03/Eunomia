using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Eunomia.Infra.Settings;
using Eunomia.UI.Controls.Grid;
using Eunomia.UI.Features.QueryTab;
using static Eunomia.UI.Headless.Tests.RealServer;

namespace Eunomia.UI.Headless.Tests;

/// <summary>Query tabs against the real server (phase 6; appendix C §4.4; appendix D B18; FAM-Q01–Q04, Q11; D2).</summary>
public sealed class QueryTabTests
{
    private static string Header(TabItem item) => (string)item.Header!;

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task ResultTabsOrdenacaoErroEUnsafe()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "Sem servidor de teste.");
        using var culture = new CultureScope(CultureInfo.InvariantCulture);
        var db = NewDatabaseName("eunomia_qt_");
        await ExecAsync(cs, $"CREATE DATABASE `{db}`; CREATE TABLE `{db}`.people (id INT PRIMARY KEY, name VARCHAR(20)); INSERT INTO `{db}`.people VALUES (1, 'rui'), (2, 'Ana'), (3, NULL);");
        var dir = Path.Combine(Path.GetTempPath(), "eunomia-qt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            using var appDb = new SqliteAppDatabase(Path.Combine(dir, "app.db"));
            var (window, context, session, _, log) = CreateShell(cs, db, appDatabase: appDb, color: 0x006400);
            var questions = new List<string>();
            var answer = false;
            window.ConfirmOverride = (_, message, _) =>
            {
                questions.Add(message);
                return Task.FromResult((answer, false));
            };
            var errors = new List<string>();
            window.QueryErrorOverride = errors.Add;
            Assert.True(await window.ConnectAsync(window, session, null));
            await WaitAsync(() => window.Tree.Tree.Model!.Roots.FirstOrDefault());
            window.Tabs.SelectedItem = window.Tabs.QueryTabs[0].Item;
            var view = Assert.IsType<QueryTabView>(window.Tabs.QueryTabs[0].Content);

            // Two result sets named by comments, "(Nr × Mc)" captions and the summary in the log.
            window.SqlEditor.Text = $"-- names: um, dois\nSELECT 1 AS a;\nSELECT * FROM `{db}`.people;";
            Assert.True(await context.Commands.ExecuteAsync("query.run"));
            Assert.Equal(["um (1r × 1c)", "dois (3r × 2c)"], view.Results.Items.OfType<TabItem>().Select(Header));
            Assert.Contains(log.Snapshot(), l => l.Text.Contains("Affected rows: 0  Found rows: 4  Warnings: 0  Duration for 2 queries:", StringComparison.Ordinal));
            Assert.Equal(0xFF006400u, view.ResultGrids[0].Control.SessionColor);
            Assert.True(view.ResultGrids[0].Control.IsReadOnly);
            Assert.Single(appDb.GetHistory("it"));
            var helpers = Assert.IsType<QueryHelpersView>(view.Helpers);
            Assert.Single(Assert.Single(helpers.Tree.Roots[Eunomia.App.Query.QueryHelpers.HistoryRoot].Children).Children);

            // Alt+→ / Alt+←.
            Assert.True(await context.Commands.ExecuteAsync("query.nextResult"));
            Assert.Equal(1, view.Results.SelectedIndex);
            Dispatcher.UIThread.RunJobs();

            // Click on the "name" header: local ascending sort with NULL first; a second click reverses it.
            var grid = view.ActiveGrid!;
            await grid.PendingAutoSize;
            var header = grid.TranslatePoint(new Point(grid.CellBounds(0, 1).Center.X, grid.HeaderHeight / 2), window)!.Value;
            window.MouseDown(header, MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(header, MouseButton.Left, RawInputModifiers.None);
            await WaitAsync(() => grid.SortIndicators.Count == 1 && grid.GetDisplayText(1, 1) == "Ana" ? "ok" : null);
            Assert.Equal("(NULL)", grid.GetDisplayText(0, 1));
            Assert.Equal(new GridSortIndicator(false, 1), grid.SortIndicators[1]);
            window.MouseDown(header, MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(header, MouseButton.Left, RawInputModifiers.None);
            await WaitAsync(() => grid.GetDisplayText(0, 1) == "rui" ? "ok" : null);
            Assert.Equal(new GridSortIndicator(true, 1), grid.SortIndicators[1]);

            // Error on the third line of the second statement: dialog, log and the line in pink with the cursor on it.
            window.SqlEditor.Text = "SELECT 1;\nSELECT\n  1 FORM x;\nSELECT 2;";
            Assert.True(await context.Commands.ExecuteAsync("query.run"));
            var error = Assert.Single(errors);
            Assert.StartsWith("SQL Error (1064): ", error, StringComparison.Ordinal);
            Assert.Contains(log.Snapshot(), l => l.Text.Contains(error, StringComparison.Ordinal));
            Assert.Equal(3, window.SqlEditor.ErrorLine);
            Assert.Equal(3, window.SqlEditor.Editor.TextArea.Caret.Line);
            Assert.Single(view.Results.Items);
            Assert.Single(appDb.GetHistory("it"));

            // UPDATE without WHERE asks; "no" runs nothing.
            window.SqlEditor.Text = $"UPDATE `{db}`.people SET name = 'x'";
            Assert.True(await context.Commands.ExecuteAsync("query.run"));
            Assert.Equal("Run unsafe queries without a WHERE clause?", Assert.Single(questions));
            Assert.DoesNotContain(log.Snapshot(), l => l.Text.StartsWith("UPDATE", StringComparison.Ordinal));
            window.Close();
        }
        finally
        {
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`");
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task AutocompleteComAliasNaSessaoAtiva()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "Sem servidor de teste.");
        var db = NewDatabaseName("eunomia_qa_");
        await ExecAsync(cs, $"CREATE DATABASE `{db}`; CREATE TABLE `{db}`.people (id INT PRIMARY KEY, `full name` VARCHAR(20));");
        try
        {
            var (window, context, session, _, _) = CreateShell(cs, db);
            Assert.True(await window.ConnectAsync(window, session, null));
            await WaitAsync(() => window.Tree.Tree.Model!.Roots.FirstOrDefault());
            window.Tabs.SelectedItem = window.Tabs.QueryTabs[0].Item;
            var editor = window.SqlEditor;
            editor.Text = $"SELECT p. FROM `{db}`.people p";
            editor.Editor.CaretOffset = "SELECT p.".Length;
            Assert.True(await context.Commands.ExecuteAsync("editor.completion"));
            await editor.PendingCompletion;
            Assert.Equal(["id", "full name"], editor.CompletionItems.Select(i => i.Text));
            Assert.Equal("`full name`", editor.CompletionItems[1].InsertText);
            editor.CompletionWindow!.Close();

            // Inside a string it does not open.
            editor.Text = "SELECT 'p.";
            editor.Editor.CaretOffset = editor.Text.Length;
            Assert.True(await context.Commands.ExecuteAsync("editor.completion"));
            await editor.PendingCompletion;
            Assert.Null(editor.CompletionWindow);
            window.Close();
        }
        finally
        {
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`");
        }
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task ResultadosEditaveisSoComTabelaEChave()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "Sem servidor de teste.");
        var db = NewDatabaseName("eunomia_qe_");
        await ExecAsync(cs, $"CREATE DATABASE `{db}`; CREATE TABLE `{db}`.people (id INT PRIMARY KEY, name VARCHAR(20)); INSERT INTO `{db}`.people VALUES (1, 'Ana'), (2, 'Rui');");
        try
        {
            var (window, context, session, _, _) = CreateShell(cs, db);
            var errors = new List<string>();
            window.EditErrorOverride = errors.Add;
            Assert.True(await window.ConnectAsync(window, session, null));
            await WaitAsync(() => window.Tree.Tree.Model!.Roots.FirstOrDefault());
            window.Tabs.SelectedItem = window.Tabs.QueryTabs[0].Item;
            var view = Assert.IsType<QueryTabView>(window.Tabs.QueryTabs[0].Content);
            window.SqlEditor.Text = $"SELECT id, name FROM `{db}`.people ORDER BY id;\nSELECT name FROM `{db}`.people;\nSELECT id, UPPER(name) AS n FROM `{db}`.people;";
            Assert.True(await context.Commands.ExecuteAsync("query.run"));
            var grids = view.ResultGrids.Select(g => g.Control).ToList();
            var editable = await WaitAsync(() => grids[0] is { IsReadOnly: false } g ? g : null);
            Assert.True(grids[1].IsReadOnly);
            Assert.True(grids[2].IsReadOnly);

            // The result with the table and the key saves like the Data tab.
            Assert.True(await editable.SetCellValueAsync(1, 1, new CellValue("Zé")));
            Assert.True(await editable.PostAsync());
            Assert.Equal("Zé", editable.GetDisplayText(1, 1));
            await using (var connection = new MySqlConnector.MySqlConnection(cs))
            {
                await connection.OpenAsync();
                await using var cmd = new MySqlConnector.MySqlCommand($"SELECT name FROM `{db}`.people WHERE id = 2", connection);
                Assert.Equal("Zé", await cmd.ExecuteScalarAsync());
            }

            // Without a key (or with an expression), read-only and HeidiSQL's message.
            await grids[1].BeginEditAsync();
            Assert.Equal(Eunomia.UI.Shell.MainWindow.NoKeyMessage, Assert.Single(errors));
            window.Close();
        }
        finally
        {
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`");
        }
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task SessaoAlvoEFixarTab()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "Sem servidor de teste.");
        var (window, context, session, connector, log) = CreateShell(cs, string.Empty, color: 0xC00000);
        var questions = new List<string>();
        var answer = false;
        window.ConfirmOverride = (_, message, _) =>
        {
            questions.Add(message);
            return Task.FromResult((answer, false));
        };
        Assert.True(await window.ConnectAsync(window, session, null));
        var root = await WaitAsync(() => window.Tree.Tree.Model!.Roots.FirstOrDefault());
        window.Tree.Tree.FocusedNode = root;
        var tab = window.Tabs.QueryTabs[0];
        window.Tabs.SelectedItem = tab.Item;
        var view = Assert.IsType<QueryTabView>(tab.Content);
        Dispatcher.UIThread.RunJobs();

        // Target session indicator: editor bar, gutter and the tab tooltip.
        Assert.Equal("Session: it", view.TargetText);
        Assert.Equal(0xFFC00000u, view.Editor.SessionColor);
        Assert.Equal("Session: it", ToolTip.GetTip(tab.Item));

        // Pinning to the session; disconnected, Run asks whether to connect and does not run on another session.
        Assert.True(await context.Commands.ExecuteAsync("query.pinToSession"));
        Assert.Equal("Pinned to session: it", view.TargetText);
        Assert.True(context.Commands.StateOf("query.pinToSession").IsChecked);
        await connector.DisconnectAllAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.Null(window.ActiveSession);
        Assert.Equal("Pinned to session: it", view.TargetText);
        window.SqlEditor.Text = "SELECT 42 AS answer;";
        Assert.True(context.Commands.StateOf("query.run").CanExecute);
        Assert.True(await context.Commands.ExecuteAsync("query.run"));
        Assert.Contains("pinned to session \"it\", which is not connected. Connect now?", Assert.Single(questions), StringComparison.Ordinal);
        Assert.Empty(view.Results.Items);

        answer = true;
        Assert.True(await context.Commands.ExecuteAsync("query.run"));
        Assert.Equal(["Result #1 (1r × 1c)"], view.Results.Items.OfType<TabItem>().Select(Header));
        Assert.Single(connector.Open);

        // Not pinned and without an active session, it does not run.
        Dispatcher.UIThread.RunJobs();
        Assert.Same(tab.Item, window.Tabs.SelectedItem);
        Assert.True(context.Commands.StateOf("query.pinToSession").IsChecked);
        Assert.True(await context.Commands.ExecuteAsync("query.pinToSession"));
        await connector.DisconnectAllAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.False(context.Commands.StateOf("query.run").CanExecute);
        Assert.Null(view.TargetText);
        window.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task CancelSoAtuaNaPropriaTab()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "Sem servidor de teste.");
        var (window, context, session, _, log) = CreateShell(cs, string.Empty);
        Assert.True(await window.ConnectAsync(window, session, null));
        await WaitAsync(() => window.Tree.Tree.Model!.Roots.FirstOrDefault());
        var first = window.Tabs.AddQueryTab();
        window.SqlEditor.Text = "SELECT SLEEP(20);";
        var run = context.Commands.ExecuteAsync("query.run").AsTask();
        await WaitAsync(() => window.ActiveSession!.Host.IsPrimaryBusy ? "ok" : null);
        Assert.False(context.Commands.StateOf("query.run").CanExecute);

        // The busy tab does not close.
        window.Tabs.CloseQueryTab(first);
        Assert.Contains(first, window.Tabs.QueryTabs);

        // In another query tab (or in the Data tab), Cancel does not touch the first one's run.
        window.Tabs.AddQueryTab();
        Assert.False(context.Commands.StateOf("operation.cancel").CanExecute);
        window.ShowDataTab();
        Assert.False(context.Commands.StateOf("operation.cancel").CanExecute);

        window.Tabs.SelectedItem = first.Item;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(await context.Commands.ExecuteAsync("operation.cancel"));
        await WaitAsync(() => run.IsCompleted ? "ok" : null);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Contains(log.Snapshot(), l => l.Text.Contains("Query execution was cancelled.", StringComparison.Ordinal));
        window.Close();
    }
}
