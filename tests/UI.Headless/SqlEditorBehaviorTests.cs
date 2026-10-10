using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Rendering;
using Eunomia.UI.Controls.SqlEditor;

namespace Eunomia.UI.Headless.Tests;

public sealed class SqlEditorBehaviorTests
{
    private static (Window Window, SqlEditorControl Editor) Show(string text)
    {
        var editor = new SqlEditorControl { Text = text };
        var window = new Window { Width = 600, Height = 300, Content = editor };
        window.Show();
        window.CaptureRenderedFrame();
        editor.Editor.TextArea.Focus();
        return (window, editor);
    }

    [AvaloniaFact]
    public void CtrlSpaceOpensCompletionWithThePrefix()
    {
        var (window, editor) = Show("SEL");
        editor.Editor.CaretOffset = 3;

        window.KeyPress(Key.Space, RawInputModifiers.Control, PhysicalKey.Space, " ");

        Assert.NotNull(editor.CompletionWindow);
        Assert.Contains(editor.CompletionWindow!.CompletionList.CompletionData, d => d.Text == "SELECT");
        Assert.Equal(0, editor.CompletionWindow.StartOffset);
        window.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task DotOpensCompletionAfter500ms()
    {
        var (window, editor) = Show("SELECT t");
        editor.Editor.CaretOffset = editor.Text.Length;

        window.KeyTextInput(".");
        Assert.Null(editor.CompletionWindow);
        await Task.Delay(SqlEditorControl.CompletionDelayAfterDot + TimeSpan.FromMilliseconds(300));

        Assert.NotNull(editor.CompletionWindow);
        window.Close();
    }

    [AvaloniaFact]
    public void ColorizerPaintsKeywordsStringsAndComments()
    {
        var (window, editor) = Show("SELECT 'a' -- c\n/* x\ny */ 42");
        var view = editor.Editor.TextArea.TextView;

        IBrush? BrushAt(int line, int column)
        {
            var visual = view.GetOrConstructVisualLine(editor.Editor.Document.GetLineByNumber(line));
            var element = visual.Elements.First(e => e.RelativeTextOffset <= column && column < e.RelativeTextOffset + e.DocumentLength);
            return element.TextRunProperties.ForegroundBrush;
        }

        var palette = SqlEditorPalette.Light;
        Assert.Same(palette.Keyword, BrushAt(1, 0));
        Assert.Same(palette.String, BrushAt(1, 7));
        Assert.Same(palette.Comment, BrushAt(1, 11));
        // A block comment opened on line 2 continues on line 3.
        Assert.Same(palette.Comment, BrushAt(3, 0));
        Assert.Same(palette.Number, BrushAt(3, 5));
        window.Close();
    }

    [AvaloniaFact]
    public void OpeningACommentChangesTheFollowingLines()
    {
        var (window, editor) = Show("SELECT 1\nFROM t");
        var view = editor.Editor.TextArea.TextView;
        editor.Editor.Document.Insert(0, "/* ");
        window.CaptureRenderedFrame();

        var line2 = view.GetOrConstructVisualLine(editor.Editor.Document.GetLineByNumber(2));
        Assert.Same(SqlEditorPalette.Light.Comment, line2.Elements[0].TextRunProperties.ForegroundBrush);
        window.Close();
    }
}
