using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using AvaloniaEdit;

namespace Eunomia.UI.Headless.Tests;

/// <summary>
/// Criterion "AvaloniaEdit works on Avalonia 12 without a fork" (section 20.2): the published package
/// (12.0.0) loads, applies its template, draws lines and accepts input with Avalonia 12.1.3.
/// </summary>
public sealed class AvaloniaEditCompatibilityTests
{
    [AvaloniaFact]
    public void TextEditorRenderizaEAceitaInput()
    {
        var editor = new TextEditor { Text = "SELECT 1;\nSELECT 2;", ShowLineNumbers = true };
        var window = new Window { Width = 400, Height = 200, Content = editor };
        window.Show();

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.True(editor.TextArea.TextView.VisualLinesValid);
        Assert.Equal(2, editor.TextArea.TextView.VisualLines.Count);

        editor.TextArea.Focus();
        editor.CaretOffset = editor.Text.Length;
        window.KeyTextInput(" -- ok");
        Assert.EndsWith(" -- ok", editor.Text, StringComparison.Ordinal);

        // Undo: Ctrl+Z, or ⌘Z on macOS (AvaloniaEdit's editing shortcuts per platform).
        window.KeyPress(Key.Z, OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control, PhysicalKey.Z, "z");
        Assert.Equal("SELECT 1;\nSELECT 2;", editor.Text);
        window.Close();
    }
}
