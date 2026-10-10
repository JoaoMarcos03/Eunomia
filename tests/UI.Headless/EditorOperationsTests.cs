using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Eunomia.UI.Controls.SqlEditor;
using Eunomia.UI.Shell;

namespace Eunomia.UI.Headless.Tests;

/// <summary>Un/comment, Ctrl+U/L, Move line up/down e Editor commands (anexo C §1.7, §2.3; FAM-Q10).</summary>
public sealed class EditorOperationsTests
{
    private static SqlEditorControl Editor(string text)
    {
        var editor = new SqlEditorControl { Width = 400, Height = 200 };
        var window = new Window { Content = editor };
        window.Show();
        editor.Text = text;
        return editor;
    }

    [AvaloniaFact]
    public void ComentaEDescomentaAsLinhasDaSelecao()
    {
        var editor = Editor("SELECT 1;\n  SELECT 2;\n\nSELECT 3;");
        editor.Editor.Select(0, editor.Text.IndexOf("SELECT 3", StringComparison.Ordinal));
        editor.ToggleComment();
        Assert.Equal("-- SELECT 1;\n--   SELECT 2;\n\nSELECT 3;", editor.Text);

        editor.Editor.Select(0, 5);
        editor.Editor.Select(0, editor.Text.IndexOf("SELECT 3", StringComparison.Ordinal));
        editor.ToggleComment();
        Assert.Equal("SELECT 1;\n  SELECT 2;\n\nSELECT 3;", editor.Text);

        // Sem seleção: a linha do cursor; uma só alteração no undo.
        editor.Editor.Select(0, 0);
        editor.Editor.CaretOffset = editor.Text.Length;
        editor.ToggleComment();
        Assert.EndsWith("\n-- SELECT 3;", editor.Text, StringComparison.Ordinal);
        editor.Editor.Undo();
        Assert.EndsWith("\nSELECT 3;", editor.Text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void MaiusculasMinusculasEMoverLinhas()
    {
        var editor = Editor("select name\nfrom people\nwhere 1");
        editor.Editor.CaretOffset = 2;
        editor.ChangeCase(upper: true);
        Assert.StartsWith("SELECT name\n", editor.Text, StringComparison.Ordinal);
        editor.Editor.Select(7, 4);
        editor.ChangeCase(upper: true);
        editor.ChangeCase(upper: false);
        Assert.StartsWith("SELECT name\n", editor.Text, StringComparison.Ordinal);
        Assert.Equal((7, 4), (editor.Editor.SelectionStart, editor.Editor.SelectionLength));

        editor.Editor.Select(0, 0);
        editor.Editor.CaretOffset = 14;
        editor.MoveLines(-1);
        Assert.Equal("from people\nSELECT name\nwhere 1", editor.Text);
        Assert.Equal(2, editor.Editor.CaretOffset);
        editor.MoveLines(-1);
        Assert.Equal("from people\nSELECT name\nwhere 1", editor.Text);
        editor.MoveLines(1);
        editor.MoveLines(1);
        Assert.Equal("SELECT name\nwhere 1\nfrom people", editor.Text);
    }

    [AvaloniaFact]
    public void AutoUppercaseForaDeStringsEComentarios()
    {
        var editor = Editor(string.Empty);
        var window = (Window)TopLevel.GetTopLevel(editor)!;
        window.CaptureRenderedFrame();
        editor.Editor.TextArea.Focus();
        void Type(string text)
        {
            foreach (var c in text)
            {
                window.KeyTextInput(c.ToString());
            }
        }

        Type("select count(id) from t where name = 'select from' -- select x ");
        Assert.Equal("SELECT COUNT(id) FROM t WHERE name = 'select from' -- select x ", editor.Text);

        editor.AutoUppercase = false;
        editor.Text = string.Empty;
        Type("select ");
        Assert.Equal("select ", editor.Text);
    }

    [AvaloniaFact]
    public void PreferencesSqlAplicaAsOpcoesAoEditor()
    {
        var window = new MainWindow(TestShell.CreateContext());
        window.Show();
        window.Tabs.SelectedItem = window.Tabs.QueryTabs[0].Item;
        var editor = window.SqlEditor;
        Assert.Equal(12, editor.Editor.FontSize);
        var prefs = new Eunomia.UI.Shell.Views.PreferencesWindow(window.Context, Eunomia.UI.Shell.Views.PreferencesWindow.SqlPage);
        prefs.Show();
        prefs.CaptureRenderedFrame();
        var font = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(prefs).OfType<ComboBox>().Single(c => c.Name == "editorFont");
        // A segunda fonte da lista do sistema (Courier New no Windows).
        var chosen = Eunomia.UI.Shell.Views.PreferencesWindow.EditorFonts[1];
        font.SelectedItem = chosen;
        window.Context.Settings.Query.EditorFontSize = 9;
        prefs.Apply();
        prefs.Close();
        Assert.Equal(chosen, window.Context.Settings.Query.EditorFont);
        window.Context.Settings.Query.EditorFontSize = 12;
        window.Context.Settings.Query.AutoUppercase = false;
        window.ApplyEditorOptionsToTabs();
        Assert.Equal(16, editor.Editor.FontSize);
        Assert.False(editor.AutoUppercase);
        window.Close();
    }

    [AvaloniaFact]
    public void AtalhosEMenuDoShell()
    {
        var window = new MainWindow(TestShell.CreateContext());
        window.Show();
        window.Tabs.SelectedItem = window.Tabs.QueryTabs[0].Item;
        var editor = window.SqlEditor;
        editor.Text = "select 1";
        editor.Editor.CaretOffset = 3;
        window.CaptureRenderedFrame();
        editor.Editor.TextArea.Focus();
        Assert.Contains("editor", window.FocusContexts);
        window.KeyPress(Key.U, RawInputModifiers.Control, PhysicalKey.U, "u");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal("SELECT 1", editor.Text);
        window.KeyPress(Key.L, RawInputModifiers.Control, PhysicalKey.L, "l");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal("select 1", editor.Text);

        var items = window.EditorCommandItems().OfType<MenuItem>().ToList();
        Assert.Contains(items, i => (string)i.Header! == "Delete Line   (Ctrl+D)");
        Assert.Contains(items, i => ((string)i.Header!).StartsWith("Convert To Uppercase", StringComparison.Ordinal));
        window.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task ReformatSqlComCtrlF8NumSoUndo()
    {
        var window = new MainWindow(TestShell.CreateContext());
        window.Show();
        window.Tabs.SelectedItem = window.Tabs.QueryTabs[0].Item;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.SqlEditor.Text = "select a from t where b=1 and c=2";
        window.SqlEditor.Editor.TextArea.Focus();

        Assert.True(window.TryExecuteShortcut("Ctrl+F8"));
        await Task.Yield();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal("SELECT a\nFROM t\nWHERE b=1\n\tAND c=2", window.SqlEditor.Text.Replace("\r\n", "\n", StringComparison.Ordinal));
        window.SqlEditor.Editor.Undo();
        Assert.Equal("select a from t where b=1 and c=2", window.SqlEditor.Text);
        window.Close();
    }
}
