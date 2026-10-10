using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaEdit.Folding;
using Eunomia.UI.Controls.SqlEditor;

namespace Eunomia.UI.Headless.Tests;

/// <summary>Code folding (Query ▸ Code folding; section 6: only by <c>#region</c>/<c>#endregion</c>).</summary>
public sealed class CodeFoldingTests
{
    private static readonly string Nl = Environment.NewLine;

    private static (Window Window, SqlEditorControl Editor) Open(string text)
    {
        var editor = new SqlEditorControl();
        var window = new Window { Content = editor, Width = 600, Height = 400 };
        window.Show();
        editor.Text = text;
        editor.CodeFolding = true;
        return (window, editor);
    }

    private static bool HasMargin(SqlEditorControl editor) => editor.Editor.TextArea.LeftMargins.OfType<FoldingMargin>().Any();

    [AvaloniaFact]
    public void NestedRegionsAndMarkersOnlyOutsideStringsAndComments()
    {
        var text = string.Join(Nl,
            "#region outer",
            "SELECT 1;",
            "  #REGION inner",
            "SELECT 2;",
            "  #endregion",
            "/* #region in comment",
            "#endregion still comment */",
            "SELECT '",
            "#region in string",
            "';",
            "#regional is not a marker",
            "#endregion",
            "#endregion without start");
        var (window, editor) = Open(text);

        var foldings = editor.Foldings;
        Assert.Equal(2, foldings.Count);
        Assert.Equal(0, foldings[0].Start);
        Assert.Equal(text.IndexOf("#endregion" + Nl + "#endregion without", StringComparison.Ordinal) + "#endregion".Length, foldings[0].End);
        Assert.Equal(text.IndexOf("#REGION inner", StringComparison.Ordinal), foldings[1].Start);
        Assert.True(HasMargin(editor));
        window.Close();
    }

    [AvaloniaFact]
    public void WithoutRegionsOrWhenOffThereIsNoMargin()
    {
        var (window, editor) = Open("SELECT 1;" + Nl + "SELECT 2;");
        Assert.Empty(editor.Foldings);
        Assert.False(HasMargin(editor));

        editor.Text = "#region a" + Nl + "SELECT 1;" + Nl + "#endregion";
        editor.UpdateFoldings();
        Assert.True(HasMargin(editor));

        editor.CodeFolding = false;
        Assert.Empty(editor.Foldings);
        Assert.False(HasMargin(editor));
        window.Close();
    }

    [AvaloniaFact]
    public void FoldSelectionWritesTheMarkersFoldsAndIsASingleUndo()
    {
        var original = "SELECT 1;" + Nl + "SELECT 2;" + Nl + "SELECT 3;";
        var (window, editor) = Open(original);
        var second = original.IndexOf("SELECT 2", StringComparison.Ordinal);
        editor.Editor.Select(second + 3, original.Length - second - 5);

        editor.FoldSelection();

        Assert.Equal("SELECT 1;" + Nl + "#region" + Nl + "SELECT 2;" + Nl + "SELECT 3;" + Nl + "#endregion", editor.Text);
        Assert.True(Assert.Single(editor.Foldings).Folded);
        editor.Editor.Undo();
        Assert.Equal(original, editor.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void MarkersInsertedOnTheCursorLine()
    {
        var (window, editor) = Open("SELECT 1;" + Nl + "SELECT 2;");
        editor.Editor.CaretOffset = 2;

        editor.InsertRegionStart();
        editor.Editor.Document.Insert(editor.Editor.CaretOffset, "batch");
        editor.Editor.CaretOffset = editor.Text.Length;
        editor.InsertRegionEnd();
        editor.UpdateFoldings();

        Assert.Equal("#region batch" + Nl + "SELECT 1;" + Nl + "SELECT 2;" + Nl + "#endregion", editor.Text);
        Assert.Single(editor.Foldings);
        window.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task TypingRecomputesAfterAPause()
    {
        var (window, editor) = Open("SELECT 1;");
        editor.Editor.Document.Insert(0, "#region typed" + Nl);
        editor.Editor.Document.Insert(editor.Text.Length, Nl + "#endregion");

        await RealServer.WaitAsync(() => editor.Foldings.Count == 1 ? "ok" : null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(HasMargin(editor));
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Snapshot(string theme)
    {
        var text = string.Join(Nl,
            "#region Reports",
            "SELECT id, name FROM customers WHERE active = 1;",
            "SELECT COUNT(*) FROM orders;",
            "#endregion",
            "#region Maintenance",
            "OPTIMIZE TABLE orders;",
            "#endregion",
            "SELECT NOW();");
        var editor = new SqlEditorControl();
        var window = new Window { Content = editor, Width = 520, Height = 200, RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light };
        window.Show();
        editor.Text = text;
        editor.CodeFolding = true;
        Assert.Equal(2, editor.Foldings.Count);
        // Folds the first region; the second stays open.
        editor.FoldAt(0);
        window.CaptureRenderedFrame();

        Tests.Snapshot.Match(window.CaptureRenderedFrame(), $"editor-code-folding-{theme}");
        window.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task QueryMenuCommands()
    {
        var window = new Eunomia.UI.Shell.MainWindow(TestShell.CreateContext());
        window.Show();
        var commands = window.Context.Commands;
        window.Tabs.SelectedItem = window.Tabs.QueryTabs[0].Item;
        window.SqlEditor.Text = "SELECT 1;";
        Assert.True(commands.StateOf("query.codeFolding").IsChecked);
        Assert.True(commands.StateOf("query.codeFoldingStartRegion").CanExecute);

        Assert.True(await commands.ExecuteAsync("query.codeFoldingStartRegion"));
        Assert.StartsWith("#region ", window.SqlEditor.Text, StringComparison.Ordinal);

        Assert.True(await commands.ExecuteAsync("query.codeFolding"));
        Assert.False(window.Context.Settings.Query.CodeFolding);
        Assert.False(window.SqlEditor.CodeFolding);
        Assert.False(commands.StateOf("query.codeFoldingStartRegion").CanExecute);
        window.Close();
    }
}
