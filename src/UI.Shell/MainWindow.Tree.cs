using Avalonia.Threading;
using Eunomia.App.Sessions;
using Eunomia.Engine.Metadata;
using Eunomia.Presentation.Logging;
using Eunomia.UI.Controls.Tree;
using Eunomia.UI.Shell.Sessions;

namespace Eunomia.UI.Shell;

/// <summary>
/// Database tree on the real sessions (phase 4; FAM-T01–T09): tree style options, refresh (F5, Shift+F5) that
/// keeps databases and groups open and the focused object, favourites saved per session, statistics arriving in
/// the background, Expand/Collapse all.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>"Doubleclick inserts node text" tree style option (section 4.2). On by default.</summary>
    public bool DoubleClickInsertsNodeText
    {
        get => _context.Settings.Tree.DoubleClickInsertsNodeText;
        set => _context.Settings.Tree.DoubleClickInsertsNodeText = value;
    }

    private TreeLayout CurrentTreeLayout() => new(_context.Settings.Tree.GroupObjects, _context.Settings.Display.ColumnsInTree);

    private void InitTree()
    {
        var c = _context.Commands;
        var settings = _context.Settings;
        _tree.Tree.AutoExpand = settings.Tree.AutoExpand;
        _tree.Tree.ExpandFailed += (_, ex) => _context.Log.Append(LogCategory.Error, ex.Message);
        _model.LoadFailed += (_, ex) => _context.Log.Append(LogCategory.Error, ex.Message);
        _model.FavoriteChanged += (_, node) => OnFavoriteChanged(node);

        Toggle("tree.groupObjects", () => settings.Tree.GroupObjects, v =>
        {
            settings.Tree.GroupObjects = v;
            _ = RebuildTreeLayoutAsync();
        });
        Toggle("display.columnsInTree", () => settings.Display.ColumnsInTree, v =>
        {
            settings.Display.ColumnsInTree = v;
            _ = RebuildTreeLayoutAsync();
        });
        Toggle("tree.autoExpand", () => settings.Tree.AutoExpand, v =>
        {
            settings.Tree.AutoExpand = v;
            _tree.Tree.AutoExpand = v;
        });
        Toggle("tree.doubleClickInsertsNodeText", () => DoubleClickInsertsNodeText, v => DoubleClickInsertsNodeText = v);
        c.SetHandler("tree.expandAll", _ => new ValueTask(ExpandAllAsync()), () => _model.Roots.Count > 0);
        // Collapse all only acts on the first session (appendix D A10).
        c.SetHandler("tree.collapseAll", () => _model.CollapseDescendants(_model.Roots[0]), () => _model.Roots.Count > 0);
        c.SetHandler("view.refresh", _ => new ValueTask(RefreshAsync(fullStatus: false)), () => _tree.Tree.FocusedNode is { Session: not null } || ActiveDiagram() is not null);
        c.SetHandler("view.fullRefresh", _ => new ValueTask(RefreshAsync(fullStatus: true)), () => _tree.Tree.FocusedNode is { Session: not null });
    }

    private async Task ExpandAllAsync()
    {
        try
        {
            foreach (var root in _model.Roots.ToArray())
            {
                await _model.ExpandAllAsync(root);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _context.Log.Append(LogCategory.Error, ex.Message);
        }
    }

    /// <summary>
    /// F5 with the focus outside the Data tab: refresh of the focused node's session (appendix D A9). Forgets the metadata cache,
    /// reads the databases again, reopens the databases, groups and tables that were open and focuses the object again (or its
    /// closest ancestor that still exists). Shift+F5 also requests the statistics of the databases it reopens.
    /// </summary>
    private async Task RefreshAsync(bool fullStatus)
    {
        // F5 in the diagram tab reloads its schema (SV-16).
        if (!fullStatus && ActiveDiagram() is { } diagram)
        {
            await RefreshDiagramAsync(diagram);
            return;
        }
        if (!fullStatus && ReferenceEquals(_tabs.SelectedItem, _tabs.DataTab) && FocusedGrid() is not null)
        {
            LoadDataForFocusedNode(force: true);
            return;
        }
        // F5 in a Host tab list reloads only that list; in the Database tab list, the database's objects (like HeidiSQL).
        if (!fullStatus && ReferenceEquals(_tabs.SelectedItem, _tabs.HostTab) && _hostView is { IsKeyboardFocusWithin: true })
        {
            await RefreshHostAsync();
            return;
        }
        if (!fullStatus && ReferenceEquals(_tabs.SelectedItem, _tabs.DatabaseTab) && _databaseView is { IsKeyboardFocusWithin: true }
            && ActiveSession is { } active && _tree.Tree.FocusedNode?.Database is { } database)
        {
            active.Metadata.Invalidate(database.Name);
            await RefreshDatabaseTabAsync();
            return;
        }
        if (_tree.Tree.FocusedNode is not { Session: { } sessionNode } focused)
        {
            return;
        }
        if (_sessionTree is not null && sessionNode.Tag is Guid id && Sessions?.Connector.Find(id) is { } open)
        {
            open.Metadata.Invalidate();
            _sessionTree.ForceStatistics = fullStatus;
        }
        await ReloadKeepingFocusAsync(sessionNode, focused);
    }

    /// <summary>Tree style options that change the tree's shape: rebuilds every session from the cache (no round trips).</summary>
    private async Task RebuildTreeLayoutAsync()
    {
        if (_sessionTree is null)
        {
            return;
        }
        var focused = _tree.Tree.FocusedNode;
        foreach (var root in _model.Roots.ToArray())
        {
            await ReloadKeepingFocusAsync(root, focused is not null && ReferenceEquals(focused.Session, root) ? focused : null);
        }
    }

    private async Task ReloadKeepingFocusAsync(TreeNode root, TreeNode? focused)
    {
        var path = focused is null ? null : TreeModel.PathOf(focused);
        var focusBefore = _tree.Tree.FocusedNode;
        try
        {
            await _model.ReloadAsync(root);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _context.Log.Append(LogCategory.Error, ex.Message);
        }
        finally
        {
            if (_sessionTree is not null)
            {
                _sessionTree.ForceStatistics = false;
            }
        }
        // If the focus changed during the reload (a click, an object just saved) and the node is still in the tree, it stays where it is:
        // restoring the old path would take the focus backwards.
        if (_tree.Tree.FocusedNode is { } now && !ReferenceEquals(now, focusBefore) && _model.IndexOf(now) >= 0)
        {
            return;
        }
        if (path is not null && _model.FindDeepest(path) is { } again)
        {
            _model.Reveal(again);
            _tree.Tree.FocusedNode = again;
        }
    }

    /// <summary>Saves the star in <c>app.db</c> right after the click (appendix D A5).</summary>
    private void OnFavoriteChanged(TreeNode node)
    {
        if (node.Tag is not TreeObjectTag { Object: { } obj } tag || Sessions?.Connector.Find(tag.Session) is not { } open)
        {
            return;
        }
        var path = ObjectPath.Of(obj);
        if (node.IsFavorite)
        {
            open.Favorites.Add(path);
        }
        else
        {
            open.Favorites.Remove(path);
        }
        _context.AppDatabase?.SetFavorite(open.Id.ToString("D"), path, node.IsFavorite);
    }

    /// <summary>Opened session: favourites from <c>app.db</c> and metadata cache notifications (statistics in the background).</summary>
    private void AttachTree(OpenSession open, TreeNode sessionNode)
    {
        if (_context.AppDatabase is { } db)
        {
            open.Favorites.UnionWith(db.GetFavorites(open.Id.ToString("D")));
        }
        // "Show only favorites" goes back to off on every new connection (appendix D A5).
        _tree.FavoritesButton.IsChecked = false;
        if (!string.IsNullOrWhiteSpace(open.Definition.HideDatabasePattern))
        {
            try
            {
                _ = new System.Text.RegularExpressions.Regex(open.Definition.HideDatabasePattern, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(200));
            }
            catch (ArgumentException ex)
            {
                // The cache ignores the invalid pattern; HeidiSQL logs it and carries on.
                _context.Log.Append(LogCategory.Error, $"Invalid regular expression in \"Hide database pattern\": {ex.Message}");
            }
        }
        open.Metadata.Changed += (_, e) =>
        {
            PostListsRefresh(open, e);
            if (e.Scope == MetadataScope.Objects && e.Database is { } database)
            {
                Dispatcher.UIThread.Post(() => OnObjectsChanged(open, sessionNode, database));
            }
        };
    }

    private void OnObjectsChanged(OpenSession open, TreeNode sessionNode, string database)
    {
        if (open.Metadata.CachedObjects(database) is not { } objects
            || sessionNode.Children?.FirstOrDefault(n => n.Kind == TreeNodeKind.Database && n.Name == database) is not { } dbNode)
        {
            return;
        }
        if (!SessionTreeProvider.ApplyObjects(dbNode, objects) && dbNode.IsExpanded)
        {
            _ = ReloadKeepingFocusAsync(dbNode, _tree.Tree.FocusedNode is { } f && ReferenceEquals(f.Database, dbNode) ? f : null);
            return;
        }
        _tree.Tree.InvalidateVisual();
    }

    /// <summary>Focused object: no longer dimmed (appendix D A2); marks the database in <c>USE</c>.</summary>
    private void MarkTreeSelection(TreeNode node)
    {
        if (node.Tag is TreeObjectTag tag && Sessions?.Connector.Find(tag.Session) is { } open)
        {
            if (node.IsObject && tag.Object is { } obj)
            {
                open.SelectedObjects.Add(ObjectPath.Of(obj));
                node.IsGhosted = false;
            }
            UpdateCurrentDatabase(open);
        }
    }

    /// <summary>Marker of the database in <c>USE</c> on the primary connection.</summary>
    private void UpdateCurrentDatabase(OpenSession open)
    {
        if (!_sessionNodes.TryGetValue(open.Id, out var sessionNode) || sessionNode.Children is null)
        {
            return;
        }
        var current = open.Host.CurrentDatabase;
        foreach (var db in sessionNode.Children)
        {
            db.IsCurrent = string.Equals(db.Name, current, StringComparison.Ordinal);
        }
        _tree.Tree.InvalidateVisual();
    }
}
