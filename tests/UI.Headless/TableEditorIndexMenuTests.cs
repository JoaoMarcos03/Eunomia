using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Eunomia.App.Objects;
using Eunomia.Drivers.Abstractions;
using Eunomia.UI.Features.TableEditor;

namespace Eunomia.UI.Headless.Tests;

/// <summary>Table editor column menu (phase 13): Create new index ▸ and Add to index ▸, as in HeidiSQL.</summary>
public sealed class TableEditorIndexMenuTests
{
    [AvaloniaFact]
    public void CreatesIndexesAndAddsColumns()
    {
        var design = new TableDesign { Database = "shop", Name = "people" };
        design.Columns.Add(new ColumnDesign { Name = "id", DataType = "INT" });
        design.Columns.Add(new ColumnDesign { Name = "name", DataType = "VARCHAR", LengthSet = "20" });
        design.Columns.Add(new ColumnDesign { Name = "email", DataType = "VARCHAR", LengthSet = "50" });
        var view = new TableEditorView(design, new TableEditorLists([], ["InnoDB"], []), _ => "CREATE", null);
        var window = new Window { Content = view, Width = 900, Height = 400 };
        window.Show();

        view.CreateIndex(design.Columns[0], IndexKind.Primary);
        view.CreateIndex(design.Columns[1], IndexKind.Key);
        view.CreateIndex(design.Columns[2], IndexKind.Key);
        Assert.Equal([("PRIMARY", IndexKind.Primary, "id"), ("name", IndexKind.Key, "name"), ("email", IndexKind.Key, "email")],
            design.Indexes.Select(i => (i.Name, i.Kind, i.Columns)));

        // Another PRIMARY joins the existing one; Add to index does not repeat columns; the name does not repeat.
        view.CreateIndex(design.Columns[1], IndexKind.Primary);
        view.AddToIndex(design.Indexes[1], design.Columns[2]);
        view.AddToIndex(design.Indexes[1], design.Columns[2]);
        view.CreateIndex(design.Columns[1], IndexKind.Unique);
        Assert.Equal("id, name", design.Indexes[0].Columns);
        Assert.Equal("name, email", design.Indexes[1].Columns);
        Assert.Equal("name_2", design.Indexes[^1].Name);
        Assert.Equal("Indexes (4)", view.IndexesTab.Header);
        window.Close();
    }
}
