using Eunomia.Engine.Sql.Filters;

namespace Eunomia.UI.Controls.Tree;

/// <summary>
/// Database tree model: lazy nodes, a flattened list of visible nodes (section 15.3) and the semantics
/// of the Database filter and the Table filter (section 4.3):
/// <list type="bullet">
/// <item>the Database filter only applies to database nodes; hiding a database hides its children;</item>
/// <item>the Table filter only applies to tables, views, procedures, functions, triggers and events;
/// sessions, groups and columns are never hidden, and databases without matching objects stay visible;</item>
/// <item>with "Show only favorites", an object only shows if it matches the Table filter and is a favourite;</item>
/// <item>the filters act on every session and on nodes loaded later (on expanding).</item>
/// </list>
/// Not thread-safe: use it on the UI thread.
/// </summary>
public sealed class TreeModel
{
    public const string PlaceholderText = "Loading…";

    private readonly List<TreeNode> _roots;
    private readonly ITreeDataProvider _provider;
    private readonly List<TreeNode> _visible = [];
    private readonly Dictionary<TreeNode, Task> _loading = [];
    private int _filterVersion;

    public TreeModel(IEnumerable<TreeNode> roots, ITreeDataProvider provider)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(provider);
        _roots = [.. roots];
        _provider = provider;
        Rebuild();
    }

    public IReadOnlyList<TreeNode> Roots => _roots;

    /// <summary>Adds a root (an opened session, FAM-S07).</summary>
    public void AddRoot(TreeNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        _roots.Add(root);
        Rebuild();
    }

    /// <summary>Removes a root (Disconnect).</summary>
    public void RemoveRoot(TreeNode root)
    {
        if (_roots.Remove(root))
        {
            Rebuild();
        }
    }

    /// <summary>
    /// Refresh (F5, appendix D A9): forgets the node's loaded children and requests them again if it was expanded, expanding
    /// again the descendants that were expanded (databases, groups, tables with columns) and that still exist. A
    /// descendant that fails to load stays closed and goes to <see cref="LoadFailed"/>. Returns when finished.
    /// </summary>
    public async Task ReloadAsync(TreeNode node, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        CollectExpanded(node, string.Empty, expanded);
        var wasExpanded = node.IsExpanded;
        node.Children = null;
        node.IsExpanded = false;
        Rebuild();
        if (wasExpanded)
        {
            await ExpandAsync(node, cancellationToken).ConfigureAwait(true);
            await RestoreExpandedAsync(node, string.Empty, expanded, cancellationToken).ConfigureAwait(true);
        }
    }

    private static string Segment(TreeNode node) => ((int)node.Kind).ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + node.Name;

    private static void CollectExpanded(TreeNode node, string key, HashSet<string> expanded)
    {
        if (!node.IsExpanded || node.Children is null)
        {
            return;
        }
        foreach (var child in node.Children)
        {
            if (child.IsExpanded)
            {
                var childKey = key + "/" + Segment(child);
                expanded.Add(childKey);
                CollectExpanded(child, childKey, expanded);
            }
        }
    }

    private async Task RestoreExpandedAsync(TreeNode node, string key, HashSet<string> expanded, CancellationToken cancellationToken)
    {
        if (node.Children is null)
        {
            return;
        }
        foreach (var child in node.Children.ToArray())
        {
            var childKey = key + "/" + Segment(child);
            if (!expanded.Contains(childKey))
            {
                continue;
            }
            try
            {
                await ExpandAsync(child, cancellationToken).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                LoadFailed?.Invoke(this, ex);
                continue;
            }
            await RestoreExpandedAsync(child, childKey, expanded, cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>Path of a node from the root (kind and name of each level), to find it again after a refresh.</summary>
    public static IReadOnlyList<(TreeNodeKind Kind, string Name)> PathOf(TreeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var path = new List<(TreeNodeKind, string)>();
        for (var n = node; n is not null; n = n.Parent)
        {
            path.Insert(0, (n.Kind, n.Name));
        }
        return path;
    }

    /// <summary>
    /// The deepest loaded node along <paramref name="path"/>. Groups are transparent (the object is
    /// found with and without "Group objects by type"). <c>null</c> if not even the root exists.
    /// </summary>
    public TreeNode? FindDeepest(IReadOnlyList<(TreeNodeKind Kind, string Name)> path)
    {
        ArgumentNullException.ThrowIfNull(path);
        IReadOnlyList<TreeNode>? level = _roots;
        TreeNode? found = null;
        foreach (var (kind, name) in path)
        {
            if (kind == TreeNodeKind.Group)
            {
                continue;
            }
            var next = FindChild(level, kind, name);
            if (next is null)
            {
                break;
            }
            found = next;
            level = next.Children;
            if (level is null)
            {
                break;
            }
        }
        return found;
    }

    private static TreeNode? FindChild(IReadOnlyList<TreeNode>? nodes, TreeNodeKind kind, string name)
    {
        if (nodes is null)
        {
            return null;
        }
        foreach (var node in nodes)
        {
            if (node.Kind == kind && string.Equals(node.Name, name, StringComparison.Ordinal))
            {
                return node;
            }
            if (node.Kind == TreeNodeKind.Group && FindChild(node.Children, kind, name) is { } inside)
            {
                return inside;
            }
        }
        return null;
    }

    /// <summary>Expands the already loaded ancestors of a node, so it becomes visible.</summary>
    public void Reveal(TreeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var changed = false;
        for (var n = node.Parent; n is not null; n = n.Parent)
        {
            if (!n.IsExpanded && n.Children is not null)
            {
                n.IsExpanded = true;
                changed = true;
            }
        }
        if (changed)
        {
            Rebuild();
        }
    }

    /// <summary>Collapse all (appendix D A10): closes every descendant of the node; the node itself stays open.</summary>
    public void CollapseDescendants(TreeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        static void Close(TreeNode n)
        {
            if (n.Children is null)
            {
                return;
            }
            foreach (var child in n.Children)
            {
                child.IsExpanded = false;
                Close(child);
            }
        }
        Close(node);
        Rebuild();
    }

    /// <summary>Recomputes the visible list and notifies the view (nodes changed from outside: sizes, states).</summary>
    public void Refresh()
    {
        _filterVersion++;
        Rebuild();
    }

    /// <summary>A descendant failed to load during a refresh (goes to the SQL log).</summary>
    public event EventHandler<Exception>? LoadFailed;

    /// <summary>An object's star changed (to save the session's favourites).</summary>
    public event EventHandler<TreeNode>? FavoriteChanged;

    /// <summary>Visible nodes in display order (only inside expanded nodes).</summary>
    public IReadOnlyList<TreeNode> Visible => _visible;

    public TreeFilterExpression DatabaseFilter { get; private set; } = TreeFilterExpression.Empty;

    public TreeFilterExpression TableFilter { get; private set; } = TreeFilterExpression.Empty;

    public bool OnlyFavorites { get; private set; }

    /// <summary>The visible list changed.</summary>
    public event EventHandler? Changed;

    /// <summary>A new regex is invalid. Raised once per text (goes to the SQL log).</summary>
    public event EventHandler<string>? FilterError;

    public void SetDatabaseFilter(string text)
    {
        if (text == DatabaseFilter.Text)
        {
            return;
        }
        DatabaseFilter = new TreeFilterExpression(text);
        ReportError(DatabaseFilter);
        _filterVersion++;
        Rebuild();
    }

    public void SetTableFilter(string text)
    {
        if (text == TableFilter.Text)
        {
            return;
        }
        TableFilter = new TreeFilterExpression(text);
        ReportError(TableFilter);
        _filterVersion++;
        Rebuild();
    }

    public void SetOnlyFavorites(bool value)
    {
        if (value == OnlyFavorites)
        {
            return;
        }
        OnlyFavorites = value;
        _filterVersion++;
        Rebuild();
    }

    /// <summary>Toggles an object's star.</summary>
    public void ToggleFavorite(TreeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!node.IsObject)
        {
            return;
        }
        node.IsFavorite = !node.IsFavorite;
        FavoriteChanged?.Invoke(this, node);
        if (OnlyFavorites)
        {
            _filterVersion++;
            Rebuild();
        }
        else
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Whether the node passes the filters, ancestors included.</summary>
    public bool IsVisible(TreeNode node)
    {
        for (var n = node; n is not null; n = n.Parent)
        {
            if (!PassesOwnFilter(n))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Expands the node, loading the children the first time (with a placeholder while loading).</summary>
    public async Task ExpandAsync(TreeNode node, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!node.CanExpand)
        {
            return;
        }
        node.IsExpanded = true;
        if (_loading.TryGetValue(node, out var pending))
        {
            // Already loading (another expand or ExpandAll): waits for the same request.
            await pending.ConfigureAwait(true);
        }
        else if (node.Children is null)
        {
            var load = LoadAsync(node, cancellationToken);
            _loading[node] = load;
            try
            {
                await load.ConfigureAwait(true);
            }
            finally
            {
                _loading.Remove(node);
            }
        }
        Rebuild();
    }

    private async Task LoadAsync(TreeNode node, CancellationToken cancellationToken)
    {
        node.IsLoading = true;
        node.Children = [new TreeNode(TreeNodeKind.Placeholder, PlaceholderText, node)];
        Rebuild();
        try
        {
            node.Children = await _provider.LoadChildrenAsync(node, cancellationToken).ConfigureAwait(true);
            UpdateSizes(node);
        }
        catch
        {
            node.Children = null;
            node.IsExpanded = false;
            Rebuild();
            throw;
        }
        finally
        {
            node.IsLoading = false;
        }
    }

    public void Collapse(TreeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!node.IsExpanded)
        {
            return;
        }
        node.IsExpanded = false;
        Rebuild();
    }

    /// <summary>Expands the node and every descendant down to the objects (loads every database: expensive, as in HeidiSQL).</summary>
    public async Task ExpandAllAsync(TreeNode node, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        await ExpandAsync(node, cancellationToken).ConfigureAwait(true);
        if (node.Children is null)
        {
            return;
        }
        foreach (var child in node.Children.ToArray())
        {
            if (child.Kind is TreeNodeKind.Session or TreeNodeKind.Database or TreeNodeKind.Group)
            {
                await ExpandAllAsync(child, cancellationToken).ConfigureAwait(true);
            }
        }
    }

    /// <summary>
    /// Case-insensitive incremental search: first visible node from <paramref name="start"/>
    /// (inclusive, wrapping around to the start) whose name starts with <paramref name="prefix"/>; -1 if there is none.
    /// </summary>
    public int FindByPrefix(string prefix, int start)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        var count = _visible.Count;
        for (var k = 0; k < count; k++)
        {
            var i = (Math.Max(0, start) + k) % count;
            if (_visible[i].Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    public int IndexOf(TreeNode node) => _visible.IndexOf(node);

    private bool PassesOwnFilter(TreeNode node)
    {
        if (node.MatchVersion == _filterVersion)
        {
            return node.MatchResult;
        }
        var result = node.Kind switch
        {
            TreeNodeKind.Database => DatabaseFilter.IsMatch(node.Name),
            _ when node.IsObject => TableFilter.IsMatch(node.Name) && (!OnlyFavorites || node.IsFavorite),
            _ => true,
        };
        node.MatchVersion = _filterVersion;
        node.MatchResult = result;
        return result;
    }

    private void Rebuild()
    {
        _visible.Clear();
        foreach (var root in _roots)
        {
            Visit(root);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Visit(TreeNode node)
    {
        if (!PassesOwnFilter(node))
        {
            return;
        }
        _visible.Add(node);
        if (node.IsExpanded && node.Children is not null)
        {
            foreach (var child in node.Children)
            {
                Visit(child);
            }
        }
    }

    private void ReportError(TreeFilterExpression expression)
    {
        if (expression.Error is not null)
        {
            FilterError?.Invoke(this, expression.Error);
        }
    }

    /// <summary>
    /// Sums of the Size column (appendix D A8) from the node up. Database: the sum of the objects (inside groups too), blank
    /// while none has a size and the sizes are not known. Session: the sum of the databases, only if all of them have a
    /// size. Groups have no size.
    /// </summary>
    public static void UpdateSizes(TreeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        for (var n = node; n is not null; n = n.Parent)
        {
            if (n.Children is null)
            {
                continue;
            }
            if (n.Kind == TreeNodeKind.Database)
            {
                long sum = 0;
                var any = n.ChildSizesKnown;
                foreach (var obj in ObjectsOf(n))
                {
                    if (obj.SizeBytes is { } size)
                    {
                        sum += size;
                        any = true;
                    }
                }
                n.SizeBytes = any ? sum : null;
            }
            else if (n.Kind == TreeNodeKind.Session)
            {
                long sum = 0;
                var complete = n.Children.Count > 0;
                foreach (var db in n.Children)
                {
                    if (db.SizeBytes is { } size)
                    {
                        sum += size;
                    }
                    else
                    {
                        complete = false;
                    }
                }
                n.SizeBytes = complete ? sum : null;
            }
        }
    }

    /// <summary>Loaded objects of a database, inside or outside groups.</summary>
    public static IEnumerable<TreeNode> ObjectsOf(TreeNode database)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (database.Children is null)
        {
            yield break;
        }
        foreach (var child in database.Children)
        {
            if (child.IsObject)
            {
                yield return child;
            }
            else if (child.Kind == TreeNodeKind.Group && child.Children is not null)
            {
                foreach (var obj in child.Children)
                {
                    yield return obj;
                }
            }
        }
    }
}
