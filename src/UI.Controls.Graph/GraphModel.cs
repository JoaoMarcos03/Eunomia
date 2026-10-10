using Avalonia;

namespace Eunomia.UI.Controls.Graph;

[Flags]
public enum GraphRowFlags
{
    None = 0,
    PrimaryKey = 1,
    Unique = 2,
    ForeignKey = 4,
    Nullable = 8,
}

/// <summary>A column drawn in the node (SV-01): name, type, PK/UNIQUE/FK icons and NULL; the colour comes from the type category.</summary>
/// <param name="Category">Type category (the <c>TypeCategory</c> order): the canvas asks <see cref="SchemaCanvas.TypeColor"/> for the colour.</param>
public sealed record GraphRow(string Name, string Type, GraphRowFlags Flags, int Category = 6);

/// <summary>A table in the diagram. The position is the top-left corner, in diagram coordinates.</summary>
public sealed class GraphNode(string id, string title, IReadOnlyList<GraphRow> rows)
{
    public string Id { get; } = id;

    public string Title { get; } = title;

    public IReadOnlyList<GraphRow> Rows { get; } = rows;

    public Point Position { get; set; }

    /// <summary>Size computed by the canvas (header and one row per column).</summary>
    public Size Size { get; internal set; }

    public Rect Bounds => new(Position, Size);

    /// <summary>Hidden by the user (SV-10).</summary>
    public bool IsHidden { get; set; }

    /// <summary>New since the last saved layout (SV-16: highlighted until it is moved or the diagram is reloaded).</summary>
    public bool IsNew { get; set; }

    /// <summary>Search result (SV-06): <c>null</c> without a search; <c>false</c> is dimmed.</summary>
    public bool? IsMatch { get; set; }

    public object? Tag { get; set; }
}

public enum GraphCardinality
{
    OneToMany,
    OneToOne,
}

/// <summary>
/// FK relation (SV-02, SV-03): from <see cref="From"/> (the table with the FK, the "many" side) to <see cref="To"/> (the
/// referenced one), attached to the column rows.
/// </summary>
public sealed record GraphEdge(string Name, GraphNode From, IReadOnlyList<int> FromRows, GraphNode To, IReadOnlyList<int> ToRows, GraphCardinality Cardinality);

public sealed class GraphNodeEventArgs(GraphNode node, Point position) : EventArgs
{
    public GraphNode Node { get; } = node;

    /// <summary>Position in the control (to open menus).</summary>
    public Point Position { get; } = position;
}
