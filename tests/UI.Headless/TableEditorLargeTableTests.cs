using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Eunomia.App.Objects;
using Eunomia.Drivers.Abstractions;
using Eunomia.UI.Features.TableEditor;

namespace Eunomia.UI.Headless.Tests;

/// <summary>
/// Table structure of wide tables (user feedback, 2 Nov 2026: "the structure view is slow to open and the app stops
/// responding on bigger tables"). Every column used to get its full set of editors (two drop-downs with every data type,
/// the default value panel, 15 controls in all): hundreds of columns meant tens of thousands of controls built and laid
/// out on the UI thread. Only the selected column has editors now; the others are plain text.
/// </summary>
public sealed class TableEditorLargeTableTests
{
    private static TableDesign WideTable(int columns)
    {
        var design = new TableDesign { Database = "shop", Name = "wide" };
        for (var i = 0; i < columns; i++)
        {
            design.Columns.Add(new ColumnDesign
            {
                OriginalName = "c" + i.ToString(CultureInfo.InvariantCulture),
                Name = "c" + i.ToString(CultureInfo.InvariantCulture),
                DataType = (i % 3) switch { 0 => "INT", 1 => "VARCHAR", _ => "DATETIME" },
                LengthSet = i % 3 == 1 ? "120" : string.Empty,
                AllowNull = i % 2 == 0,
                Comment = "Column " + i.ToString(CultureInfo.InvariantCulture),
                Collation = i % 3 == 1 ? "utf8mb4_unicode_ci" : string.Empty,
            });
        }
        design.Indexes.Add(new IndexDesign { Name = "PRIMARY", Kind = IndexKind.Primary, Columns = "c0" });
        return design;
    }

    private static readonly TableEditorLists Lists = new(
        [.. Enumerable.Range(0, 300).Select(i => "collation_" + i.ToString(CultureInfo.InvariantCulture))], ["InnoDB", "MyISAM"], ["orders"]);

    [AvaloniaFact]
    public void OnlyTheSelectedColumnGetsEditors()
    {
        var view = new TableEditorView(WideTable(300), Lists, _ => "CREATE", _ => string.Empty);
        var window = new Window { Content = view, Width = 1200, Height = 700 };
        var clock = Stopwatch.StartNew();
        window.Show();
        window.CaptureRenderedFrame();
        clock.Stop();
        try
        {
            var controls = view.GetLogicalDescendants().OfType<Control>().Count();
            Assert.True(controls < 300 * 20, $"{controls} controls for 300 columns ({clock.ElapsedMilliseconds} ms).");
            Assert.DoesNotContain(view.GetLogicalDescendants().OfType<ComboBox>(), c => Avalonia.Automation.AutomationProperties.GetName(c) == "Datatype");

            view.SelectedColumn = 5;
            Assert.Single(view.GetLogicalDescendants().OfType<ComboBox>(), c => Avalonia.Automation.AutomationProperties.GetName(c) == "Datatype");
            view.SelectedColumn = 8;
            var types = view.GetLogicalDescendants().OfType<ComboBox>().Where(c => Avalonia.Automation.AutomationProperties.GetName(c) == "Datatype").ToList();
            Assert.Single(types);
            Assert.Equal("DATETIME", (types[0].SelectedItem as ComboBoxItem)?.Content);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void EditsInTheSelectedColumnShowAfterMovingOn()
    {
        var design = WideTable(4);
        var view = new TableEditorView(design, Lists, d => string.Join(",", d.Columns.Select(c => c.Name)), _ => string.Empty);
        var window = new Window { Content = view, Width = 1200, Height = 500 };
        window.Show();
        try
        {
            view.SelectedColumn = 1;
            var name = view.GetLogicalDescendants().OfType<TextBox>().Single(t => t.Text == "c1");
            name.Text = "renamed";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("renamed", design.Columns[1].Name);
            Assert.True(view.IsModified);

            view.SelectedColumn = 2;
            Assert.Contains(view.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "renamed");
            Assert.Equal(["c0", "renamed", "c2", "c3"], view.VisibleColumnNames);
        }
        finally
        {
            window.Close();
        }
    }
}
