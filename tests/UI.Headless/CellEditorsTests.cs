using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Eunomia.UI.Controls.Grid;
using MySqlConnector;
using static Eunomia.UI.Headless.Tests.RealServer;

namespace Eunomia.UI.Headless.Tests;

/// <summary>Per-type editors in the Data tab (phase 7; appendix D B15; FAM-G07, G10).</summary>
public sealed class CellEditorsTests
{
    private static async Task<string?> ScalarAsync(string cs, string sql)
    {
        await using var connection = new MySqlConnection(cs);
        await connection.OpenAsync();
        await using var cmd = new MySqlCommand(sql, connection);
        return Convert.ToString(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task EnumSetFkDateLongTextBinaryAndFunction()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "No test server.");
        var db = NewDatabaseName("eunomia_ce_");
        await ExecAsync(cs, $"CREATE DATABASE `{db}`; " +
            $"CREATE TABLE `{db}`.cities (id INT PRIMARY KEY, name VARCHAR(20)) ENGINE=InnoDB; INSERT INTO `{db}`.cities VALUES (1, 'Porto'), (2, 'Braga'); " +
            $"CREATE TABLE `{db}`.t (id INT PRIMARY KEY, e ENUM('a','b'), s SET('x','y','in_progress'), d DATETIME, city INT, note TEXT, bin BLOB, " +
            $"FOREIGN KEY (city) REFERENCES `{db}`.cities (id)) ENGINE=InnoDB; " +
            $"INSERT INTO `{db}`.t VALUES (1, 'a', 'x', '2026-01-01 10:00:00', 1, REPEAT('t', 300), 0xCAFE);");
        try
        {
            var (window, _, session, _, _) = CreateShell(cs, db);
            var popups = new List<GridPopupEditEventArgs>();
            string? popupResult = null;
            window.PopupEditorOverride = request =>
            {
                popups.Add(request);
                return popupResult;
            };
            Assert.True(await window.ConnectAsync(window, session, null));
            var model = window.Tree.Tree.Model!;
            var root = await WaitAsync(() => model.Roots.FirstOrDefault());
            var dbNode = await WaitAsync(() => root.Children?.FirstOrDefault(n => n.Name == db));
            await model.ExpandAsync(dbNode);
            window.ShowDataTab();
            window.Tree.Tree.FocusedNode = dbNode.Children!.Single(n => n.Name == "t");
            var grid = window.DataGrid;
            await WaitAsync(() => grid.RowCount == 1 ? "ok" : null);
            window.CaptureRenderedFrame();

            // ENUM: a list with the values.
            grid.FocusCell(0, 1);
            await grid.BeginEditAsync();
            var combo = Assert.IsType<ComboBox>(grid.EditorControl);
            Assert.Equal(["a", "b"], combo.ItemsSource!.Cast<string>());
            combo.IsDropDownOpen = false;
            combo.SelectedIndex = 1;
            grid.EndEdit(commit: true);

            // SET: one check box per value; a value with "_" shows whole and is saved as it is.
            grid.FocusCell(0, 2);
            await grid.BeginEditAsync();
            var boxes = ((StackPanel)((ScrollViewer)((Border)grid.EditorControl!).Child!).Content!).Children.OfType<CheckBox>().ToList();
            Assert.Equal(["x", "y", "in_progress"], boxes.Select(b => ((TextBlock)b.Content!).Text));
            boxes[2].IsChecked = true;
            grid.EndEdit(commit: true);

            // Date and time: text editor with arrows.
            grid.FocusCell(0, 3);
            await grid.BeginEditAsync();
            Assert.IsType<TextBox>(grid.EditorControl);
            grid.EndEdit(commit: false);

            // Foreign key: "text (id)" sorted by the text.
            grid.FocusCell(0, 4);
            await grid.BeginEditAsync();
            var fk = Assert.IsType<ComboBox>(grid.EditorControl);
            Assert.Equal(["Braga (2)", "Porto (1)"], fk.ItemsSource!.Cast<string>());
            fk.IsDropDownOpen = false;
            fk.SelectedIndex = 0;
            grid.EndEdit(commit: true);

            // Text with 300 characters and binary: window editor.
            popupResult = "curto";
            grid.FocusCell(0, 5);
            await grid.BeginEditAsync();
            popupResult = "0xBEEF";
            grid.FocusCell(0, 6);
            await grid.BeginEditAsync();
            await WaitAsync(() => grid.Edits.PendingValues.Count == 5 ? "ok" : null);
            Assert.Equal([false, true], popups.Select(p => p.IsBinary));
            Assert.Equal(300, popups[0].Value!.Length);

            Assert.True(await grid.PostAsync());
            Assert.Equal("b|x,in_progress|2|curto|beef", await ScalarAsync(cs, $"SELECT CONCAT_WS('|', e, s, city, note, LOWER(HEX(bin))) FROM `{db}`.t"));

            // SQL function (Ctrl+F2): the text goes as an expression.
            grid.Focus();
            grid.FocusCell(0, 3);
            Assert.True(await window.Context.Commands.ExecuteAsync("grid.sqlFunction"));
            await WaitAsync(() => grid.EditorControl as ComboBox);
            var functions = (ComboBox)grid.EditorControl!;
            Assert.Contains("NOW()", functions.ItemsSource!.Cast<string>());
            functions.Text = "'2030-05-06' + INTERVAL 2 HOUR";
            grid.EndEdit(commit: true);
            Assert.True(await grid.PostAsync());
            Assert.Equal("2030-05-06 02:00:00", await ScalarAsync(cs, $"SELECT CAST(d AS CHAR) FROM `{db}`.t"));
            window.Close();
        }
        finally
        {
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`");
        }
    }

    [AvaloniaFact]
    public void WindowEditorShowsTheStatusLine()
    {
        using var culture = new CultureScope(CultureInfo.InvariantCulture);
        var window = new Eunomia.UI.Shell.Views.TextEditorWindow("Edit note", "ab\ncd", 100, binary: false);
        window.Show();
        Assert.Equal("5 characters (max: 100), 2 lines, cursor at 1:1", window.Status);
        window.Text = "abc";
        Assert.StartsWith("3 characters", window.Status, StringComparison.Ordinal);

        // Closing with changes asks; without an answer (the guard closes the question), the window stays open.
        window.Close();
        Assert.Equal(["MessageDialog \"Confirm\": Apply modifications?"], UnexpectedDialogGuardAttribute.TakeOpened());
        Assert.True(window.IsVisible);
        window.Text = "ab\ncd";
        window.Close();
        Assert.False(window.IsVisible);
        Assert.Empty(UnexpectedDialogGuardAttribute.TakeOpened());
    }
}
