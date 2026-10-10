using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Eunomia.App.Settings;
using Eunomia.UI.Features.Transfer;

namespace Eunomia.UI.Headless.Tests;

/// <summary>Export grid rows dialog (appendix B §1.4; FAM-C04, C05).</summary>
public sealed class GridExportDialogTests
{
    [AvaloniaFact]
    public void DefaultsEOpcoesDoClipboardSeparadas()
    {
        var settings = new ExportOptions();
        var dialog = new GridExportDialog(settings, selectedRows: 3, allRows: 1000, focusedColumn: "name", expandFileName: s => s);
        dialog.Show();

        var radios = dialog.GetLogicalDescendants().OfType<RadioButton>().ToList();
        Assert.Contains(radios, r => r.Content as string == "Copy to clipboard" && r.IsChecked == true);
        Assert.Contains(radios, r => r.Content as string == "Complete (1,000 rows)" || r.Content as string == "Complete (1.000 rows)" || (r.Content as string)!.StartsWith("Complete (", StringComparison.Ordinal));
        var combos = dialog.GetLogicalDescendants().OfType<ComboBox>().ToList();
        Assert.Contains(combos, c => c.SelectedItem as string == "Excel CSV");
        Assert.Contains(combos, c => c.SelectedItem as string == "UTF-8" && !c.IsEnabled);
        var boxes = dialog.GetLogicalDescendants().OfType<CheckBox>().ToList();
        Assert.Contains(boxes, b => b.Content as string == "Only focused column (name)" && b.IsEnabled);

        // Save clipboard settings keeps the Copy as options, apart from the dialog's.
        boxes.Single(b => b.Content as string == "Include column names").IsChecked = false;
        var save = dialog.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == "Save clipboard settings");
        save.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.False(settings.ClipboardColumnNames);
        Assert.True(settings.ColumnNames);
        dialog.Close();
    }
}
