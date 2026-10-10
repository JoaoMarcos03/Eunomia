namespace Eunomia.UI.Controls.Tree;

/// <summary>Levels of the database tree (section 4.2).</summary>
public enum TreeNodeKind
{
    Session,
    Database,

    /// <summary>Optional group (Tables, Views, ...) when "Group objects by type" is on.</summary>
    Group,
    Table,
    View,
    Procedure,
    Function,
    Trigger,
    Event,
    Column,

    /// <summary>"Loading…" node while the children are requested.</summary>
    Placeholder,
}

/// <summary>Icon of a column node: the first key that contains it, or a plain field (appendix D, A2).</summary>
public enum ColumnKeyMarker
{
    None,
    Primary,
    Unique,
    Key,
    FullText,
    Spatial,
}

/// <summary>
/// Tree node. Children are loaded when the node is expanded for the first time (lazy);
/// <see cref="Children"/> is <c>null</c> until then.
/// </summary>
public sealed class TreeNode
{
    internal int MatchVersion = -1;
    internal bool MatchResult;

    public TreeNode(TreeNodeKind kind, string name, TreeNode? parent = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        Kind = kind;
        Name = name;
        Parent = parent;
        Depth = parent is null ? 0 : parent.Depth + 1;
        Session = kind == TreeNodeKind.Session ? this : parent?.Session;
    }

    public TreeNodeKind Kind { get; }

    /// <summary>Column 0 text: the object's name, without the database name. It is the text the filters compare.</summary>
    public string Name { get; }

    /// <summary>Drawn text: for groups, "Tables (12)" when the children already exist (appendix D, A1).</summary>
    public string DisplayText => Kind == TreeNodeKind.Group && !IsLoading && Children is { } children
        ? $"{Name} ({children.Count})"
        : Name;

    public TreeNode? Parent { get; }

    public TreeNode? Session { get; }

    public int Depth { get; }

    /// <summary>Application data attached to the node (session id, object reference).</summary>
    public object? Tag { get; init; }

    /// <summary>
    /// Dimmed icon (section 5.7): session without a connection, database not cached yet, object not yet selected in this
    /// session, empty group.
    /// </summary>
    public bool IsGhosted { get; set; }

    /// <summary>Database in <c>USE</c> on the primary connection: a marker on the icon (appendix D, A2).</summary>
    public bool IsCurrent { get; set; }

    /// <summary>Table engine when it has an overlay (MEMORY, CSV, FEDERATED, ARIA, ...); <c>null</c> for the rest.</summary>
    public string? EngineOverlay { get; set; }

    /// <summary>Kind of the objects in a group (the group's icon is the kind's).</summary>
    public TreeNodeKind GroupOf { get; init; } = TreeNodeKind.Group;

    /// <summary>Columns: the key that gives the icon.</summary>
    public ColumnKeyMarker KeyMarker { get; init; }

    /// <summary>Columns: type category for the text colour (the grid colours); -1 = normal colour.</summary>
    public int TextCategory { get; init; } = -1;

    /// <summary>Session colour in ARGB (session nodes only). 0 = no colour.</summary>
    public uint SessionColor { get; init; }

    /// <summary>Size in bytes (tables: Data_length + Index_length). Databases and sessions: the sum (see <see cref="TreeModel.UpdateSizes"/>).</summary>
    public long? SizeBytes { get; set; }

    /// <summary>Database: the objects' sizes have arrived (the sum shows even if it is 0).</summary>
    public bool ChildSizesKnown { get; set; }

    public bool IsFavorite { get; set; }

    public bool IsExpanded { get; internal set; }

    public bool IsLoading { get; internal set; }

    /// <summary>Has children still to load (tables with "Columns below tables in tree").</summary>
    public bool MayHaveChildren { get; init; }

    /// <summary>May have children (shows the expand button even before loading).</summary>
    public bool CanExpand => Kind is TreeNodeKind.Session or TreeNodeKind.Database
        || (Kind == TreeNodeKind.Group && Children is not { Count: 0 })
        || (MayHaveChildren && Children is not { Count: 0 })
        || (Kind == TreeNodeKind.Table && Children is { Count: > 0 });

    public IReadOnlyList<TreeNode>? Children { get; internal set; }

    public bool IsObject => Kind is TreeNodeKind.Table or TreeNodeKind.View or TreeNodeKind.Procedure
        or TreeNodeKind.Function or TreeNodeKind.Trigger or TreeNodeKind.Event;

    /// <summary>Database the node belongs to (the node itself, if it is a database).</summary>
    public TreeNode? Database
    {
        get
        {
            for (var n = this; n is not null; n = n.Parent)
            {
                if (n.Kind == TreeNodeKind.Database)
                {
                    return n;
                }
            }
            return null;
        }
    }

    /// <summary>Sets the children already known (groups built with the database's objects).</summary>
    public void SetChildren(IReadOnlyList<TreeNode> children) => Children = children;

    public override string ToString() => $"{Kind} {Name}";
}

/// <summary>Provides the children of a node (in the product: <c>SHOW DATABASES</c>, <c>SHOW TABLE STATUS</c>, ...).</summary>
public interface ITreeDataProvider
{
    Task<IReadOnlyList<TreeNode>> LoadChildrenAsync(TreeNode parent, CancellationToken cancellationToken);
}
