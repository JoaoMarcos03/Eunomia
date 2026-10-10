using Avalonia.Media;

namespace Eunomia.UI.Controls.Common.TreeList;

/// <summary>Column of a <see cref="TreeListControl"/> (like the columns of HeidiSQL's VirtualStringTree).</summary>
public sealed class TreeListColumn(string header, double width, TextAlignment alignment = TextAlignment.Left)
{
    public string Header { get; } = header;

    public double Width { get; set; } = width;

    public double MinWidth { get; set; } = 24;

    public bool Visible { get; set; } = true;

    public TextAlignment Alignment { get; } = alignment;

    /// <summary>Comparison for sorting (by default: natural case-insensitive text; numbers if both are numbers).</summary>
    public Comparison<string>? Compare { get; init; }
}

/// <summary>Node of a <see cref="TreeListControl"/>: text per column, icon, colours and children.</summary>
public sealed class TreeListNode
{
    private readonly List<TreeListNode> _children = [];

    public TreeListNode(params string[] cells)
    {
        Cells = cells;
    }

    /// <summary>Text per column (missing = empty).</summary>
    public string[] Cells { get; set; }

    /// <summary>Key in the <see cref="Icons.IconSet"/>.</summary>
    public string? Icon { get; set; }

    /// <summary>Background colour of the whole row 0xRRGGBB (session colour); <c>null</c> = no colour.</summary>
    public uint? Background { get; set; }

    public bool Bold { get; set; }

    /// <summary>Dimmed text (inactive sessions, objects without a cache).</summary>
    public bool Muted { get; set; }

    /// <summary>Folder/group: stays on top when "folders at top" is on.</summary>
    public bool IsGroup { get; set; }

    public bool IsExpanded { get; set; }

    /// <summary>Proportional bar (0–1) drawn behind the text, per column (Size and Time columns of HeidiSQL's lists).</summary>
    public Dictionary<int, double>? Bars { get; set; }

    /// <summary>Highlighted cells (Host tab › Variables: session value different from the global one).</summary>
    public HashSet<int>? Highlighted { get; set; }

    /// <summary>Sort value per column (sizes, numbers, dates); without a value, sorts by the cell text.</summary>
    public Dictionary<int, IComparable>? SortKeys { get; set; }

    public object? Tag { get; set; }

    public TreeListNode? Parent { get; private set; }

    public IReadOnlyList<TreeListNode> Children => _children;

    public int Depth => Parent is null ? 0 : Parent.Depth + 1;

    public string this[int column] => column < Cells.Length ? Cells[column] : string.Empty;

    public TreeListNode Add(TreeListNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        child.Parent = this;
        _children.Add(child);
        return child;
    }

    public void Clear()
    {
        foreach (var c in _children)
        {
            c.Parent = null;
        }
        _children.Clear();
    }

    internal void Sort(Comparison<TreeListNode> comparison)
    {
        _children.Sort(comparison);
        foreach (var c in _children)
        {
            c.Sort(comparison);
        }
    }

    public bool IsAncestorOf(TreeListNode node)
    {
        for (var p = node.Parent; p is not null; p = p.Parent)
        {
            if (ReferenceEquals(p, this))
            {
                return true;
            }
        }
        return false;
    }

    public IEnumerable<TreeListNode> Descendants()
    {
        foreach (var c in _children)
        {
            yield return c;
            foreach (var d in c.Descendants())
            {
                yield return d;
            }
        }
    }
}

/// <summary>Drag &amp; drop request: <see cref="Target"/> is the node it was dropped on (<c>null</c> = root).</summary>
public sealed class TreeListDropEventArgs(TreeListNode source, TreeListNode? target) : EventArgs
{
    public TreeListNode Source { get; } = source;

    public TreeListNode? Target { get; } = target;
}

/// <summary>In-place rename: the handler validates and returns the error message in <see cref="Error"/> (or leaves it <c>null</c>).</summary>
public sealed class TreeListRenameEventArgs(TreeListNode node, string text) : EventArgs
{
    public TreeListNode Node { get; } = node;

    public string Text { get; } = text;

    public string? Error { get; set; }
}
