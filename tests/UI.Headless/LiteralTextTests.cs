using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Eunomia.UI.Controls.Common.Text;
using Eunomia.UI.Controls.Grid;

namespace Eunomia.UI.Headless.Tests;

/// <summary>
/// Text from data in menus and check boxes: in the theme's AccessText the first "_" became an access key and disappeared
/// ("prd_products" showed as "prdproducts").
/// </summary>
public sealed class LiteralTextTests
{
    private static double Width(Control control)
    {
        control.Measure(Size.Infinity);
        return control.DesiredSize.Width;
    }

    [AvaloniaFact]
    public void EscapedDesenhaOUnderscoreNoAccessText()
    {
        var literal = Width(new TextBlock { Text = "prd_products" });

        Assert.Equal(literal, Width(new AccessText { Text = LiteralText.Escaped("prd_products") }));
        Assert.NotEqual(literal, Width(new AccessText { Text = "prd_products" }));
        Assert.Equal("a__b__c", LiteralText.Escaped("a_b_c"));
        Assert.Equal("plain", LiteralText.Escaped("plain"));
    }

    [AvaloniaFact]
    public void EditorDeSetLeOValorDoTagENaoDoTextoDesenhado()
    {
        var editor = new SetCellEditor(["in_progress", "done"]);
        var control = editor.Create("in_progress", 12, FontFamily.Default);
        var window = new Window { Content = control };
        window.Show();
        var boxes = window.GetLogicalDescendants().OfType<CheckBox>().ToList();

        Assert.Equal(["in_progress", "done"], boxes.Select(b => ((TextBlock)b.Content!).Text));
        boxes[1].IsChecked = true;
        Assert.Equal("in_progress,done", editor.Read(control, "in_progress")?.Text);
        window.Close();
    }
}
