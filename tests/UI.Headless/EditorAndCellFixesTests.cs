using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Eunomia.UI.Controls.Grid;
using Eunomia.UI.Controls.SqlEditor;

namespace Eunomia.UI.Headless.Tests;

/// <summary>Auto upper case keeps table names, the log colours each line on its own, and the SET editor keeps NULL.</summary>
public sealed class EditorAndCellFixesTests
{
    [AvaloniaFact]
    public void AutoUppercaseKeepsTableAndColumnNames()
    {
        var editor = new SqlEditorControl();
        var window = new Window { Width = 600, Height = 300, Content = editor };
        window.Show();
        window.CaptureRenderedFrame();
        editor.Editor.TextArea.Focus();
        foreach (var c in "select event.id from event join status where t.status = 1 ")
        {
            window.KeyTextInput(c.ToString());
        }

        Assert.Equal("SELECT event.id FROM event JOIN status WHERE t.status = 1 ", editor.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void AutoUppercaseKeepsKeywordsAfterTable()
    {
        var editor = new SqlEditorControl();
        var window = new Window { Width = 600, Height = 300, Content = editor };
        window.Show();
        window.CaptureRenderedFrame();
        editor.Editor.TextArea.Focus();
        foreach (var c in "create table if not exists event (id int) ")
        {
            window.KeyTextInput(c.ToString());
        }

        Assert.Equal("CREATE TABLE IF NOT EXISTS event (id INT) ", editor.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void AnOpenQuoteInTheLogDoesNotColourTheNextLine()
    {
        var log = new SqlLogView();
        var window = new Window { Width = 600, Height = 300, Content = log };
        window.Show();
        log.Reset(1, [("SELECT 'it", LogLineStyle.Error), ("SELECT 1", LogLineStyle.Normal)]);
        window.CaptureRenderedFrame();

        var view = log.Editor.TextArea.TextView;
        var line2 = view.GetOrConstructVisualLine(log.Editor.Document.GetLineByNumber(2));
        Assert.Same(SqlEditorPalette.Light.Keyword, line2.Elements[0].TextRunProperties.ForegroundBrush);
        window.Close();
    }

    [AvaloniaFact]
    public void SetEditorWithNothingCheckedKeepsNull()
    {
        var editor = new SetCellEditor(["a", "b"]);
        var control = editor.Create(null, 12, FontFamily.Default);
        var window = new Window { Content = control };
        window.Show();

        Assert.Null(editor.Read(control, null));
        window.GetLogicalDescendants().OfType<CheckBox>().First().IsChecked = true;
        Assert.Equal("a", editor.Read(control, null)?.Text);
        window.Close();
    }
}
