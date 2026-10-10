using Avalonia.Controls;
using Eunomia.Drivers.Abstractions;
using Eunomia.Presentation.Objects;
using Eunomia.UI.Controls.Common.TreeList;

namespace Eunomia.UI.Features.DatabaseTab;

/// <summary>
/// Database tab (section 4.9; appendix C §1.8): list of the database's objects with Name, Rows, Size, Created, Updated, Engine,
/// Comment and Type visible and 11 hidden columns (header menu), multi-selection, in-place rename with F2, and Enter or
/// double click to open the object. The list sorts by name by default, like the tree.
/// </summary>
public sealed class DatabaseTabView : DockPanel
{
    private static readonly CellFormat[] Formats = DatabaseObjectList.Columns
        .Select((c, i) => i switch
        {
            2 => CellFormat.Size,
            3 or 4 => CellFormat.DateTime,
            _ => c.Numeric ? CellFormat.Number : CellFormat.Text,
        })
        .ToArray();

    private readonly TreeListView _view = new();

    public DatabaseTabView()
    {
        var list = _view.List;
        list.MultiSelect = true;
        list.AllowRename = true;
        list.GroupsFirst = false;
        foreach (var column in DatabaseObjectList.Columns)
        {
            list.Columns.Add(new TreeListColumn(column.Header, column.Width, column.Numeric ? Avalonia.Media.TextAlignment.Right : Avalonia.Media.TextAlignment.Left)
            {
                Visible = column.VisibleByDefault,
            });
        }
        list.SortBy(0, false);
        list.HeaderContextRequested += (_, _) => TreeListHeaderMenu.Show(list, () => ColumnsChanged?.Invoke(this, EventArgs.Empty));
        list.NodeActivated += (_, node) =>
        {
            if (node.Tag is DbObjectRef obj && Objects.FirstOrDefault(o => o.Ref == obj) is { } info)
            {
                ObjectActivated?.Invoke(this, info);
            }
        };
        Children.Add(_view);
    }

    public TreeListControl List => _view.List;

    /// <summary>Database shown (or <c>null</c> before the first one).</summary>
    public string? Database { get; private set; }

    public IReadOnlyList<DbObjectInfo> Objects { get; private set; } = [];

    /// <summary>Selected objects in list order.</summary>
    public IReadOnlyList<DbObjectInfo> SelectedObjects
    {
        get
        {
            var byRef = Objects.ToDictionary(o => o.Ref);
            return List.SelectedNodes.Select(n => byRef.GetValueOrDefault((DbObjectRef)n.Tag!)).OfType<DbObjectInfo>().ToList();
        }
    }

    /// <summary>Enter or double click: opens the object.</summary>
    public event EventHandler<DbObjectInfo>? ObjectActivated;

    /// <summary>A column was shown or hidden (to save it).</summary>
    public event EventHandler? ColumnsChanged;

    /// <summary>Headers of the visible columns.</summary>
    public IReadOnlyList<string> VisibleColumns
    {
        get => List.Columns.Where(c => c.Visible).Select(c => c.Header).ToList();
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            foreach (var column in List.Columns.Skip(1))
            {
                column.Visible = value.Contains(column.Header);
            }
            List.InvalidateVisual();
        }
    }

    /// <summary>Shows the objects of a database; keeps the selection when it is the same database.</summary>
    public void Show(string database, IReadOnlyList<DbObjectInfo> objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        var sameDatabase = database == Database;
        Database = database;
        Objects = objects;
        var nodes = objects
            .Select(o => TreeListRows.Create(
                Enumerable.Range(0, DatabaseObjectList.Columns.Count).Select(i => DatabaseObjectList.Value(o, i)).ToList(),
                Formats,
                tag: o.Ref))
            .ToList();
        foreach (var (node, obj) in nodes.Zip(objects))
        {
            node.Icon = obj.Ref.Kind switch
            {
                DbObjectKind.View or DbObjectKind.MaterializedView => "view",
                DbObjectKind.Procedure => "procedure",
                DbObjectKind.Function => "function",
                DbObjectKind.Trigger => "trigger",
                DbObjectKind.Event => "event",
                _ => "table",
            };
        }
        TreeListRows.ApplyBars(nodes, [2]);
        if (!sameDatabase)
        {
            List.SetRoots([]);
        }
        List.SetRoots(nodes);
    }

    /// <summary>Selects and focuses the object with this name (the tree focused an object of the same database).</summary>
    public void Focus(DbObjectRef obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        if (List.AllNodes().FirstOrDefault(n => Equals(n.Tag, obj)) is { } node)
        {
            List.FocusedNode = node;
        }
    }
}
