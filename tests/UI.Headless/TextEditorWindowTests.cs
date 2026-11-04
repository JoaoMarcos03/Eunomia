using System.Globalization;
using System.Text;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Eunomia.Engine.Sql.Formatting;
using Eunomia.UI.Shell.Views;

namespace Eunomia.UI.Headless.Tests;

/// <summary>
/// Window cell editor: word wrap on by default, syntax picked (detected from the column or the value), Format only when
/// asked, large values without freezing the window, and the returned text equal to what came in while it is not edited.
/// </summary>
public sealed class TextEditorWindowTests
{
    private static string BigJson(int bytes)
    {
        var sb = new StringBuilder("{\"items\":[");
        for (var i = 0; sb.Length < bytes; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{{\"id\":{i},\"name\":\"item {i}\",\"ok\":true}},");
        }
        return sb.Append("{}]}").ToString();
    }

    [AvaloniaFact]
    public void WrapIsOnAndSyntaxIsDetected()
    {
        using var culture = new CultureScope(CultureInfo.InvariantCulture);
        const string value = "{\"tier_1\": {\"min\": \"10\"}}";
        var json = new TextEditorWindow("Edit settings", value, null, binary: false);
        json.Show();
        Assert.True(json.ValueEditor.WordWrap);
        Assert.Equal(TextSyntax.Json, json.Syntax);
        Assert.False(json.SyntaxChosen);
        Assert.Equal($"{value.Length} characters (max: unknown), 1 lines, cursor at 1:1", json.Status);
        json.Close();

        // By the column (JSON) even with a value that is not JSON yet; the highlighting saved for the column wins over detection.
        var byColumn = new TextEditorWindow("Edit doc", string.Empty, null, binary: false, nativeType: "json");
        Assert.Equal(TextSyntax.Json, byColumn.Syntax);
        var saved = new TextEditorWindow("Edit doc", "{\"a\":1}", null, binary: false, syntax: TextSyntax.Text);
        Assert.Equal(TextSyntax.Text, saved.Syntax);
        Assert.Equal(TextSyntax.Xml, new TextEditorWindow("Edit x", "<a><b/></a>", null, binary: false).Syntax);
        Assert.Equal(TextSyntax.Text, new TextEditorWindow("Edit x", "plain text", null, binary: false).Syntax);

        // A change in the combo is flagged so it can be saved for the column.
        saved.SelectSyntax(TextSyntax.Json);
        Assert.True(saved.SyntaxChosen);
        Assert.Equal(TextSyntax.Json, saved.ValueEditor.Syntax);
    }

    [AvaloniaFact]
    public void TextComesBackUnchangedWithLineBreaksTabsAndControlCharacters()
    {
        var value = "line 1\r\nline 2\n\twith a tab\0null and 😀 é\rend\n";
        var window = new TextEditorWindow("Edit note", value, null, binary: false);
        window.Show();
        window.SelectSyntax(TextSyntax.Xml);
        window.ValueEditor.WordWrap = false;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(value, window.Text);

        // Without changes, closing asks nothing.
        window.Close();
        Assert.False(window.IsVisible);
        Assert.Empty(UnexpectedDialogGuardAttribute.TakeOpened());
    }

    [AvaloniaFact]
    public void FormatOnlyWhenAskedAndCtrlZUndoesIt()
    {
        using var culture = new CultureScope(CultureInfo.InvariantCulture);
        const string original = "{\"tier_1\":{\"min\":\"10\",\"max\":\"20\"},\"tier_2\":[1,2.50]}";
        var window = new TextEditorWindow("Edit settings", original, null, binary: false);
        window.Show();
        Assert.Equal(original, window.Text);

        window.Format();
        Assert.Equal("{\n  \"tier_1\": {\n    \"min\": \"10\",\n    \"max\": \"20\"\n  },\n  \"tier_2\": [\n    1,\n    2.50\n  ]\n}", window.Text);
        Assert.Contains("10 lines", window.Status, StringComparison.Ordinal);
        window.ValueEditor.Editor.Undo();
        Assert.Equal(original, window.Text);

        // Invalid JSON stays the same and the status line says why.
        window.Text = "{\"a\": 1,}";
        window.Format();
        Assert.Equal("{\"a\": 1,}", window.Text);
        Assert.StartsWith("The text is not valid JSON", window.Status, StringComparison.Ordinal);
        window.Text = original;
        window.Close();
    }

    [AvaloniaFact]
    public void LargeSingleLineValueOpensAndFormatSplitsTheLines()
    {
        using var culture = new CultureScope(CultureInfo.InvariantCulture);
        var json = BigJson(1_000_000);
        var window = new TextEditorWindow("Edit response", json, null, binary: false);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();
        Assert.Equal(json, window.Text);
        Assert.StartsWith(json.Length.ToString("N0", CultureInfo.InvariantCulture) + " characters (max: unknown), 1 lines", window.Status, StringComparison.Ordinal);
        Assert.EndsWith("Long lines are not highlighted (Format splits them)", window.Status, StringComparison.Ordinal);

        window.Format();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();
        Assert.DoesNotContain("Long lines", window.Status, StringComparison.Ordinal);
        Assert.True(window.ValueEditor.Editor.Document.LineCount > 10_000);

        // Ctrl+Enter applies: the formatted text is what the user saw and chose.
        string? result = null;
        window.Closed += (_, _) => result = window.Text;
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsVisible);
        Assert.NotNull(result);
    }

    [AvaloniaFact]
    public void BinaryHasNoSyntaxNorFormat()
    {
        var window = new TextEditorWindow("Edit bin", "0xCAFE", null, binary: true);
        window.Show();
        Assert.Equal(TextSyntax.Text, window.Syntax);
        Assert.True(window.ValueEditor.WordWrap);
        window.Format();
        Assert.Equal("0xCAFE", window.Text);
        window.Close();
    }
}
