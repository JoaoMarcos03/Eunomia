using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Eunomia.App.Search;
using Eunomia.UI.Shell;
using Eunomia.UI.Shell.Views;
using static Eunomia.UI.Headless.Tests.RealServer;

namespace Eunomia.UI.Headless.Tests;

/// <summary>Find, Replace e Find again no editor e no grid (anexo C §4.7; FAM-Q10).</summary>
public sealed class SearchTests
{
    private static MainWindow Window()
    {
        var window = new MainWindow(TestShell.CreateContext());
        window.Show();
        window.Tabs.SelectedItem = window.Tabs.QueryTabs[0].Item;
        return window;
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task FindReplaceEFindAgainNoEditor()
    {
        var window = Window();
        var commands = window.Context.Commands;
        var editor = window.SqlEditor;
        editor.Text = "SELECT name FROM people;\nSELECT * FROM people WHERE name = 'people';";
        editor.Editor.CaretOffset = 8;
        editor.Editor.TextArea.Focus();
        var prefills = new List<string>();
        SearchOptions? options = null;
        var replaceAll = false;
        window.SearchDialogOverride = (find, _, targets, target) =>
        {
            prefills.Add(find);
            Assert.Equal("SQL editor", targets[target].Caption);
            return options is null ? null : new SearchRequest(options, target, replaceAll);
        };

        // Prefill com a palavra no cursor; Entire scope encontra a primeira.
        options = new SearchOptions { Find = "people", WholeWords = true };
        Assert.True(await commands.ExecuteAsync("search.find"));
        Assert.Equal("name", prefills[^1]);
        Assert.Equal((17, 6), (editor.Editor.SelectionStart, editor.Editor.SelectionLength));

        // F3 continua a partir da seleção; no fim, "not found" na status bar.
        Assert.True(await commands.ExecuteAsync("search.findAgain"));
        Assert.Equal(39, editor.Editor.SelectionStart);
        Assert.True(await commands.ExecuteAsync("search.findAgain"));
        Assert.Equal(60, editor.Editor.SelectionStart);
        Assert.True(await commands.ExecuteAsync("search.findAgain"));
        Assert.Equal(60, editor.Editor.SelectionStart);
        Assert.Equal("Search string \"people\" not found", window.Context.Status.Info);

        // Replace all sem perguntar: uma só alteração no undo.
        options = new SearchOptions { Find = "people", Replace = "persons", PromptOnReplace = false };
        replaceAll = true;
        Assert.True(await commands.ExecuteAsync("search.replace"));
        Assert.Equal("SELECT name FROM persons;\nSELECT * FROM persons WHERE name = 'persons';", editor.Text);
        Assert.Equal("3 occurrence(s) replaced", window.Context.Status.Info);
        editor.Editor.Undo();
        Assert.Contains("FROM people;", editor.Text, StringComparison.Ordinal);

        // Replace (OK) troca só a próxima, a partir do cursor.
        options = new SearchOptions { Find = "name", Replace = "id", PromptOnReplace = false, EntireScope = false };
        replaceAll = false;
        editor.Editor.Select(0, 0);
        editor.Editor.CaretOffset = 20;
        Assert.True(await commands.ExecuteAsync("search.replace"));
        Assert.Equal("SELECT name FROM people;\nSELECT * FROM people WHERE id = 'people';", editor.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void DialogoMostraOsCamposEDesligaReplaceNoGrid()
    {
        var settings = new Eunomia.App.Settings.SearchSettings { FindHistory = ["antigo"] };
        var dialog = new SearchReplaceDialog("novo", replace: true, [new SearchTarget("Grid", false, false), new SearchTarget("SQL editor", true, true)], 0, settings);
        dialog.Show();
        dialog.CaptureRenderedFrame();
        var boxes = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(dialog).OfType<Avalonia.Controls.ComboBox>().ToList();
        Assert.Equal("novo", boxes[0].Text);
        Assert.False(boxes[1].IsEnabled);
        boxes[2].SelectedIndex = 1;
        Assert.True(boxes[1].IsEnabled);
        dialog.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task FindNoGrid()
    {
        var window = new MainWindow(TestShell.CreateContext());
        window.Show();
        var model = window.Tree.Tree.Model!;
        await model.ExpandAsync(model.Roots[0]);
        var db = model.Roots[0].Children![5];
        await model.ExpandAsync(db);
        window.ShowDataTab();
        window.Tree.Tree.FocusedNode = db.Children![0];
        var grid = window.DataGrid;
        await WaitAsync(() => grid.Source is not null && grid.GetDisplayText(40, 1) is { } t ? t : null);
        var wanted = grid.GetDisplayText(40, 1)!;
        grid.Focus();
        window.CaptureRenderedFrame();
        window.SearchDialogOverride = (_, _, targets, target) =>
        {
            Assert.Equal("Grid", targets[target].Caption);
            Assert.False(targets[target].CanReplace);
            return new SearchRequest(new SearchOptions { Find = "^" + System.Text.RegularExpressions.Regex.Escape(wanted) + "$", RegularExpression = true }, target, false);
        };

        Assert.True(await window.Context.Commands.ExecuteAsync("search.find"));
        await WaitAsync(() => grid.Selection.FocusedRow > 0 ? "ok" : null);
        Assert.Equal(wanted, grid.GetDisplayText(grid.Selection.FocusedRow, grid.Selection.FocusedColumn));
        window.Close();
    }
}
