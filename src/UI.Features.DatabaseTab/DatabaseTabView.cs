using Avalonia.Controls;
using Eunomia.Drivers.Abstractions;
using Eunomia.Presentation.Objects;
using Eunomia.UI.Controls.Common.TreeList;

namespace Eunomia.UI.Features.DatabaseTab;

/// <summary>
/// Database tab (secção 4.9; anexo C §1.8): lista dos objetos da base com Name, Rows, Size, Created, Updated, Engine,
/// Comment e Type visíveis e 11 colunas ocultas (menu do cabeçalho), multi-seleção, rename in-place com F2 e Enter ou
/// duplo clique para abrir o objeto. A lista ordena pelo nome por omissão, como a árvore.
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

    /// <summary>Base mostrada (ou <c>null</c> antes da primeira).</summary>
    public string? Database { get; private set; }

    public IReadOnlyList<DbObjectInfo> Objects { get; private set; } = [];

    /// <summary>Objetos selecionados pela ordem da lista.</summary>
    public IReadOnlyList<DbObjectInfo> SelectedObjects
    {
        get
        {
            var byRef = Objects.ToDictionary(o => o.Ref);
            return List.SelectedNodes.Select(n => byRef.GetValueOrDefault((DbObjectRef)n.Tag!)).OfType<DbObjectInfo>().ToList();
        }
    }

    /// <summary>Enter ou duplo clique: abre o objeto.</summary>
    public event EventHandler<DbObjectInfo>? ObjectActivated;

    /// <summary>Uma coluna foi mostrada ou escondida (para gravar).</summary>
    public event EventHandler? ColumnsChanged;

    /// <summary>Cabeçalhos das colunas visíveis.</summary>
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

    /// <summary>Mostra os objetos de uma base; mantém a seleção quando é a mesma base.</summary>
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

    /// <summary>Seleciona e foca o objeto com este nome (a árvore focou um objeto da mesma base).</summary>
    public void Focus(DbObjectRef obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        if (List.AllNodes().FirstOrDefault(n => Equals(n.Tag, obj)) is { } node)
        {
            List.FocusedNode = node;
        }
    }
}
