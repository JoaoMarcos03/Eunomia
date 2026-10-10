using Eunomia.App.Sessions;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Metadata;
using Eunomia.UI.Controls.Tree;

namespace Eunomia.UI.Shell.Sessions;

/// <summary>Reference kept in the tree's database, object and column nodes.</summary>
public sealed record TreeObjectTag(Guid Session, string Database, DbObjectRef? Object, string? Column = null);

/// <summary>Tree style options that change the tree's shape (appendix D A3): groups by kind and columns below tables.</summary>
public sealed record TreeLayout(bool GroupObjects, bool ColumnsInTree);

/// <summary>
/// Tree children from the open sessions and each one's metadata cache (section 4.2; appendix D A1–A8):
/// databases in the server's order (whitelist and Hide database pattern in the cache), objects in natural order with kinds
/// mixed or in the 6 groups, columns below tables, favourites and states (database dimmed until cached, object
/// dimmed until selected, overlay for the database in <c>USE</c> and for the engine).
/// </summary>
public sealed class SessionTreeProvider(SessionConnector connector, Func<TreeLayout> layout) : ITreeDataProvider
{
    private static readonly NaturalComparer Natural = new();

    /// <summary>Fixed groups, always the 6 of them, even without triggers or events on the server (appendix D A1).</summary>
    public static readonly IReadOnlyList<(TreeNodeKind Kind, string Caption)> Groups =
    [
        (TreeNodeKind.Table, "Tables"),
        (TreeNodeKind.View, "Views"),
        (TreeNodeKind.Procedure, "Procedures"),
        (TreeNodeKind.Function, "Functions"),
        (TreeNodeKind.Trigger, "Triggers"),
        (TreeNodeKind.Event, "Events"),
    ];

    /// <summary>Engines with an overlay on the table icon (appendix D A2).</summary>
    private static readonly HashSet<string> OverlayEngines = new(StringComparer.OrdinalIgnoreCase)
    {
        "FEDERATED", "MEMORY", "ARIA", "CSV", "PERFORMANCE_SCHEMA", "BLACKHOLE", "MRG_MYISAM",
    };

    /// <summary>Shift+F5: the next object load also requests the statistics.</summary>
    public bool ForceStatistics { get; set; }

    public async Task<IReadOnlyList<TreeNode>> LoadChildrenAsync(TreeNode parent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parent);
        switch (parent.Kind)
        {
            case TreeNodeKind.Session when parent.Tag is Guid id && connector.Find(id) is { } session:
                var databases = await session.Metadata.GetDatabasesAsync(cancellationToken).ConfigureAwait(true);
                return databases.Select(db => CreateDatabaseNode(parent, session, db)).ToList();

            case TreeNodeKind.Database when parent.Tag is TreeObjectTag tag && connector.Find(tag.Session) is { } session:
                var objects = await session.Metadata.GetObjectsAsync(tag.Database, cancellationToken, ForceStatistics).ConfigureAwait(true);
                var nodes = BuildObjectNodes(parent, session, objects, layout());
                parent.IsGhosted = false;
                parent.ChildSizesKnown = objects.HasStatistics;
                return nodes;

            case TreeNodeKind.Group when parent.Parent?.Tag is TreeObjectTag tag && connector.Find(tag.Session) is { } session:
                var all = await session.Metadata.GetObjectsAsync(tag.Database, cancellationToken).ConfigureAwait(true);
                return ObjectNodes(parent, session, all.Objects.Where(o => KindOf(o.Ref.Kind) == parent.GroupOf));

            case TreeNodeKind.Table when parent.Tag is TreeObjectTag { Object: { } table } tag && connector.Find(tag.Session) is { } session:
                var definition = await session.Metadata.GetTableAsync(table, cancellationToken).ConfigureAwait(true);
                return ColumnNodes(parent, tag, definition);

            default:
                return [];
        }
    }

    private static TreeNode CreateDatabaseNode(TreeNode parent, OpenSession session, string db)
    {
        var cached = session.Metadata.CachedObjects(db);
        var node = new TreeNode(TreeNodeKind.Database, db, parent)
        {
            Tag = new TreeObjectTag(session.Id, db, null),
            IsGhosted = cached is null,
            IsCurrent = string.Equals(session.Host.CurrentDatabase, db, StringComparison.Ordinal),
        };
        if (cached is { HasStatistics: true })
        {
            node.SizeBytes = cached.Objects.Sum(o => KindOf(o.Ref.Kind) == TreeNodeKind.Table ? o.SizeBytes ?? 0 : 0);
            node.ChildSizesKnown = true;
        }
        return node;
    }

    /// <summary>Objects of a database: with groups, the 6 groups already with their children (and the count); without groups, all mixed.</summary>
    private List<TreeNode> BuildObjectNodes(TreeNode database, OpenSession session, DatabaseObjects objects, TreeLayout options)
    {
        if (!options.GroupObjects)
        {
            return ObjectNodes(database, session, objects.Objects, options);
        }
        var groups = new List<TreeNode>(Groups.Count);
        foreach (var (kind, caption) in Groups)
        {
            var group = new TreeNode(TreeNodeKind.Group, caption, database) { GroupOf = kind, Tag = database.Tag };
            var children = ObjectNodes(group, session, objects.Objects.Where(o => KindOf(o.Ref.Kind) == kind), options);
            group.SetChildren(children);
            group.IsGhosted = children.Count == 0;
            groups.Add(group);
        }
        return groups;
    }

    private List<TreeNode> ObjectNodes(TreeNode parent, OpenSession session, IEnumerable<DbObjectInfo> objects, TreeLayout? options = null)
    {
        var columns = (options ?? layout()).ColumnsInTree;
        var tag = (TreeObjectTag)(parent.Kind == TreeNodeKind.Group ? parent.Parent!.Tag! : parent.Tag!);
        return objects
            .OrderBy(o => o.Ref.Name, Natural)
            .Select(o =>
            {
                var kind = KindOf(o.Ref.Kind);
                var path = ObjectPath.Of(o.Ref);
                return new TreeNode(kind, o.Ref.Name, parent)
                {
                    Tag = tag with { Object = o.Ref },
                    // Only tables show a size (appendix D A8).
                    SizeBytes = kind == TreeNodeKind.Table ? o.SizeBytes : null,
                    IsFavorite = session.Favorites.Contains(path),
                    IsGhosted = !session.SelectedObjects.Contains(path),
                    EngineOverlay = o.Engine is { } engine && OverlayEngines.Contains(engine) ? engine.ToUpperInvariant() : null,
                    MayHaveChildren = columns && kind == TreeNodeKind.Table,
                };
            })
            .ToList();
    }

    private static List<TreeNode> ColumnNodes(TreeNode table, TreeObjectTag tag, TableDefinition definition) =>
        definition.Columns
            .Select(c => new TreeNode(TreeNodeKind.Column, c.Name, table)
            {
                Tag = tag with { Column = c.Name },
                KeyMarker = KeyMarkerOf(definition, c.Name),
                TextCategory = (int)c.Type.Category,
            })
            .ToList();

    /// <summary>First key that contains the column (appendix D A2).</summary>
    private static ColumnKeyMarker KeyMarkerOf(TableDefinition definition, string column)
    {
        foreach (var index in definition.Indexes)
        {
            if (index.Columns.Contains(column, StringComparer.OrdinalIgnoreCase))
            {
                return index.Kind switch
                {
                    IndexKind.Primary => ColumnKeyMarker.Primary,
                    IndexKind.Unique => ColumnKeyMarker.Unique,
                    IndexKind.FullText => ColumnKeyMarker.FullText,
                    IndexKind.Spatial => ColumnKeyMarker.Spatial,
                    _ => ColumnKeyMarker.Key,
                };
            }
        }
        return ColumnKeyMarker.None;
    }

    /// <summary>
    /// The statistics arrived (or the cache changed) for a database: updates the sizes and engines of the loaded objects, the
    /// dimmed state and the sums. Returns <c>false</c> if the object list changed (the database must be reloaded).
    /// </summary>
    public static bool ApplyObjects(TreeNode database, DatabaseObjects objects)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(objects);
        database.IsGhosted = false;
        database.ChildSizesKnown = objects.HasStatistics;
        if (database.Children is null)
        {
            if (objects.HasStatistics)
            {
                database.SizeBytes = objects.Objects.Sum(o => KindOf(o.Ref.Kind) == TreeNodeKind.Table ? o.SizeBytes ?? 0 : 0);
                TreeModel.UpdateSizes(database.Parent!);
            }
            return true;
        }
        var byKey = objects.Objects.ToDictionary(o => (KindOf(o.Ref.Kind), o.Ref.Name));
        var loaded = TreeModel.ObjectsOf(database).ToList();
        if (loaded.Count != byKey.Count)
        {
            return false;
        }
        foreach (var node in loaded)
        {
            if (!byKey.TryGetValue((node.Kind, node.Name), out var info))
            {
                return false;
            }
            if (node.Kind == TreeNodeKind.Table)
            {
                node.SizeBytes = info.SizeBytes;
                node.EngineOverlay = info.Engine is { } engine && OverlayEngines.Contains(engine) ? engine.ToUpperInvariant() : null;
            }
        }
        TreeModel.UpdateSizes(database);
        return true;
    }

    public static TreeNodeKind KindOf(DbObjectKind kind) => kind switch
    {
        DbObjectKind.View or DbObjectKind.MaterializedView => TreeNodeKind.View,
        DbObjectKind.Procedure => TreeNodeKind.Procedure,
        DbObjectKind.Function => TreeNodeKind.Function,
        DbObjectKind.Trigger => TreeNodeKind.Trigger,
        DbObjectKind.Event => TreeNodeKind.Event,
        _ => TreeNodeKind.Table,
    };
}
