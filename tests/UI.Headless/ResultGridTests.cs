using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Eunomia.Engine.Results;
using Eunomia.UI.Controls.Grid;
using Eunomia.UI.Features.QueryTab;
using static Eunomia.UI.Headless.Tests.RealServer;

namespace Eunomia.UI.Headless.Tests;

/// <summary>
/// Query tab result tabs: right click with Copy, Copy as and Export grid rows as in the Data tab; large values (JSON from
/// APIs) shown by their start in the grid and whole when copying, exporting and editing.
/// </summary>
public sealed class ResultGridTests
{
    private const int BigLength = 300_000;

    private static async Task<string> ClipboardTextAsync(Window window) => await window.Clipboard!.TryGetTextAsync() ?? string.Empty;

    private static void RightClick(Window window, DataGridControl grid, long row, int column)
    {
        var point = grid.TranslatePoint(grid.CellBounds(row, column).Center, window)!.Value;
        window.MouseDown(point, MouseButton.Right, RawInputModifiers.None);
        window.MouseUp(point, MouseButton.Right, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task RightClickCopyAsAndExportOnResultsWithLargeValues()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "No test server.");
        using var culture = new CultureScope(CultureInfo.InvariantCulture);
        var db = NewDatabaseName("eunomia_rg_");
        await ExecAsync(cs, $"CREATE DATABASE `{db}`; CREATE TABLE `{db}`.docs (id INT PRIMARY KEY, doc LONGTEXT); " +
            $"INSERT INTO `{db}`.docs VALUES (1, CONCAT('{{\"items\":\"', REPEAT('x', {BigLength}), '\"}}')), (2, 'small');");
        var file = Path.Combine(Path.GetTempPath(), $"eunomia-results-{Guid.NewGuid():N}.csv");
        try
        {
            var (window, context, session, _, _) = CreateShell(cs, db);
            Assert.True(await window.ConnectAsync(window, session, null));
            await WaitAsync(() => window.Tree.Tree.Model!.Roots.FirstOrDefault());
            window.Tabs.SelectedItem = window.Tabs.QueryTabs[0].Item;
            var view = Assert.IsType<QueryTabView>(window.Tabs.QueryTabs[0].Content);
            window.SqlEditor.Text = $"SELECT id, doc FROM `{db}`.docs ORDER BY id";
            Assert.True(await context.Commands.ExecuteAsync("query.run"));
            var grid = view.ActiveGrid!;
            await grid.PendingAutoSize;
            window.CaptureRenderedFrame();

            // The grid keeps and draws only the start of the large value; the whole value stays in the result.
            var shown = grid.GetDisplayText(0, 1)!;
            Assert.Equal(DisplayFormatter.MaxDisplayChars + DisplayFormatter.TruncatedSuffix.Length, shown.Length);
            Assert.EndsWith(DisplayFormatter.TruncatedSuffix, shown, StringComparison.Ordinal);
            Assert.Equal("small", grid.GetDisplayText(1, 1));

            // Right click on a cell: the grid menu with Copy, Copy as and Export grid rows (without the Data tab-only items).
            RightClick(window, grid, 0, 1);
            var menu = Assert.IsType<ContextMenu>(window.LastResultGridMenu);
            Assert.True(menu.IsOpen);
            var headers = menu.Items.OfType<MenuItem>().Where(i => i.IsVisible).Select(i => i.Header as string).ToList();
            Assert.Contains("Copy", headers);
            Assert.Contains("Copy as", headers);
            Assert.Contains(headers, h => h?.StartsWith("Export grid rows", StringComparison.Ordinal) == true);
            Assert.DoesNotContain("Quick Filter", headers);
            var copyAs = menu.Items.OfType<MenuItem>().Single(i => i.Header as string == "Copy as");
            Assert.Equal(16, copyAs.Items.OfType<MenuItem>().Count(i => i.IsVisible && i.IsEnabled));
            menu.Close();
            grid.Focus();

            // Copy (focused cell) and Copy as: the whole value, from the source table.
            Assert.True(await context.Commands.ExecuteAsync("edit.copy"));
            Assert.Equal(BigLength + 12, (await ClipboardTextAsync(window)).Length);
            Assert.True(await context.Commands.ExecuteAsync("copyAs.sqlInserts"));
            var inserts = await ClipboardTextAsync(window);
            Assert.StartsWith("INSERT INTO `docs` (`id`, `doc`) VALUES (1, '{\"items\":\"xxx", inserts, StringComparison.Ordinal);
            Assert.Contains(new string('x', BigLength), inserts, StringComparison.Ordinal);
            Assert.DoesNotContain("small", inserts, StringComparison.Ordinal);

            // Export grid rows of every result row to a file.
            window.ExportDialogOverride = () => new UI.Features.Transfer.GridExportRequest(
                App.Export.GridExportFormat.ExcelCsv, false, file, "UTF-8", false, new App.Export.GridExportOptions { ToClipboard = false }, false, false, false);
            Assert.True(await context.Commands.ExecuteAsync("export.gridRows"));
            var csv = await File.ReadAllTextAsync(file);
            Assert.Contains(new string('x', BigLength), csv, StringComparison.Ordinal);
            Assert.Contains("small", csv, StringComparison.Ordinal);

            // No menu on the header (left click sorts there).
            var opened = menu;
            var header = grid.TranslatePoint(new Point(grid.CellBounds(0, 1).Center.X, grid.HeaderHeight / 2), window)!.Value;
            window.MouseDown(header, MouseButton.Right, RawInputModifiers.None);
            window.MouseUp(header, MouseButton.Right, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(opened, window.LastResultGridMenu);
            window.Close();
        }
        finally
        {
            File.Delete(file);
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`");
        }
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task LongValueInEditableResultOpensTheWindowEditorAndSaves()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "No test server.");
        using var culture = new CultureScope(CultureInfo.InvariantCulture);
        var db = NewDatabaseName("eunomia_rp_");
        await ExecAsync(cs, $"CREATE DATABASE `{db}`; CREATE TABLE `{db}`.docs (id INT PRIMARY KEY, doc LONGTEXT); " +
            $"INSERT INTO `{db}`.docs VALUES (1, CONCAT('{{\"a\":\"', REPEAT('y', 5000), '\"}}'));");
        try
        {
            var (window, context, session, _, _) = CreateShell(cs, db);
            var popups = new List<GridPopupEditEventArgs>();
            window.PopupEditorOverride = request =>
            {
                popups.Add(request);
                return request.Value!.Replace("\"a\"", "\"b\"", StringComparison.Ordinal);
            };
            Assert.True(await window.ConnectAsync(window, session, null));
            await WaitAsync(() => window.Tree.Tree.Model!.Roots.FirstOrDefault());
            window.Tabs.SelectedItem = window.Tabs.QueryTabs[0].Item;
            var view = Assert.IsType<QueryTabView>(window.Tabs.QueryTabs[0].Content);
            window.SqlEditor.Text = $"SELECT id, doc FROM `{db}`.docs";
            Assert.True(await context.Commands.ExecuteAsync("query.run"));
            var grid = view.ActiveGrid!;
            await WaitAsync(() => grid.IsReadOnly ? null : "ok");

            // Before, only the Data tab opened the window editor: a long value in a result tab could not be edited.
            grid.FocusCell(0, 1);
            await grid.BeginEditAsync();
            var request = Assert.Single(popups);
            Assert.Equal(5008, request.Value!.Length);
            Assert.True(await grid.PostAsync());
            Assert.Equal("{\"b\":\"" + new string('y', 5000) + "\"}", await ScalarTextAsync(cs, $"SELECT doc FROM `{db}`.docs WHERE id = 1"));
            window.Close();
        }
        finally
        {
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`");
        }
    }

    private static async Task<string?> ScalarTextAsync(string cs, string sql)
    {
        await using var connection = new MySqlConnector.MySqlConnection(cs);
        await connection.OpenAsync();
        await using var cmd = new MySqlConnector.MySqlCommand(sql, connection);
        return (await cmd.ExecuteScalarAsync())?.ToString();
    }
}
