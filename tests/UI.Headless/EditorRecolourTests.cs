using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Eunomia.Engine.Sql.Formatting;
using Eunomia.UI.Controls.SqlEditor;

namespace Eunomia.UI.Headless.Tests;

/// <summary>
/// An edit that changes the state at the end of its line (an open comment) recolours the following lines; one that does
/// not only redraws its own line.
/// </summary>
public sealed class EditorRecolourTests
{
    [AvaloniaFact]
    public void OpeningACommentInTheSqlEditorRecoloursTheNextLine()
    {
        var editor = new SqlEditorControl { Text = "SELECT 1\nSELECT 2" };
        var window = new Window { Width = 600, Height = 300, Content = editor };
        window.Show();
        window.CaptureRenderedFrame();
        var view = editor.Editor.TextArea.TextView;
        var line2 = editor.Editor.Document.GetLineByNumber(2);
        Assert.Same(SqlEditorPalette.Light.Keyword, view.GetOrConstructVisualLine(line2).Elements[0].TextRunProperties.ForegroundBrush);

        editor.Editor.Document.Insert(0, "/*");
        window.CaptureRenderedFrame();

        Assert.Same(SqlEditorPalette.Light.Comment, view.GetOrConstructVisualLine(line2).Elements[0].TextRunProperties.ForegroundBrush);

        editor.Editor.Document.Remove(0, 2);
        window.CaptureRenderedFrame();

        Assert.Same(SqlEditorPalette.Light.Keyword, view.GetOrConstructVisualLine(line2).Elements[0].TextRunProperties.ForegroundBrush);
        window.Close();
    }

    [AvaloniaFact]
    public void OpeningACommentInTheValueEditorRecoloursTheNextLine()
    {
        var editor = new TextValueEditor { Syntax = TextSyntax.Sql, Text = "SELECT 1\nSELECT 2" };
        var window = new Window { Width = 600, Height = 300, Content = editor };
        window.Show();
        window.CaptureRenderedFrame();
        var view = editor.Editor.TextArea.TextView;
        var line2 = editor.Editor.Document.GetLineByNumber(2);
        Assert.Same(SqlEditorPalette.Light.Keyword, view.GetOrConstructVisualLine(line2).Elements[0].TextRunProperties.ForegroundBrush);

        editor.Editor.Document.Insert(0, "/*");
        window.CaptureRenderedFrame();

        Assert.Same(SqlEditorPalette.Light.Comment, view.GetOrConstructVisualLine(line2).Elements[0].TextRunProperties.ForegroundBrush);
        window.Close();
    }
}
