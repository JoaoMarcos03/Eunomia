using Avalonia.Threading;
using Eunomia.App.Sessions;
using Eunomia.Engine.Metadata;
using Eunomia.Presentation.Logging;
using Eunomia.UI.Controls.Tree;
using Eunomia.UI.Shell.Sessions;

namespace Eunomia.UI.Shell;

/// <summary>
/// Árvore de bases sobre as sessões reais (Fase 4; FAM-T01–T09): tree style options, refresh (F5, Shift+F5) que
/// mantém bases e grupos abertos e o objeto focado, favoritos gravados por sessão, estatísticas que chegam em
/// background, Expand/Collapse all.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Tree style option "Doubleclick inserts node text" (secção 4.2). Ligada por omissão.</summary>
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
        // Collapse all só atua na primeira sessão (anexo D A10).
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
    /// F5 com o foco fora do Data tab: refresh da sessão do nó focado (anexo D A9). Esquece a cache de metadata, relê
    /// as bases, volta a abrir as bases, grupos e tabelas que estavam abertos e volta a focar o objeto (ou o
    /// antepassado mais próximo que ainda exista). Shift+F5 pede também as estatísticas das bases que reabre.
    /// </summary>
    private async Task RefreshAsync(bool fullStatus)
    {
        // F5 no tab de diagrama relê o schema dele (SV-16).
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
        // F5 numa lista do Host tab relê só essa lista; na lista do Database tab, os objetos da base (como o HeidiSQL).
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

    /// <summary>Tree style options que mudam a forma da árvore: reconstrói todas as sessões a partir da cache (sem idas ao servidor).</summary>
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
        // Se o foco mudou durante o reload (clique, objeto acabado de gravar) e o nó continua na árvore, fica onde está:
        // repor o caminho antigo levaria o foco para trás.
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

    /// <summary>Grava a estrela no <c>app.db</c> logo a seguir ao clique (anexo D A5).</summary>
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

    /// <summary>Sessão aberta: favoritos do <c>app.db</c> e avisos da cache de metadata (estatísticas em background).</summary>
    private void AttachTree(OpenSession open, TreeNode sessionNode)
    {
        if (_context.AppDatabase is { } db)
        {
            open.Favorites.UnionWith(db.GetFavorites(open.Id.ToString("D")));
        }
        // "Show only favorites" volta a off em cada ligação nova (anexo D A5).
        _tree.FavoritesButton.IsChecked = false;
        if (!string.IsNullOrWhiteSpace(open.Definition.HideDatabasePattern))
        {
            try
            {
                _ = new System.Text.RegularExpressions.Regex(open.Definition.HideDatabasePattern, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(200));
            }
            catch (ArgumentException ex)
            {
                // A cache ignora o padrão inválido; o HeidiSQL regista e continua.
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

    /// <summary>Objeto focado: deixa de estar esbatido (anexo D A2); marca a base em <c>USE</c>.</summary>
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

    /// <summary>Marcador da base em <c>USE</c> na ligação primária.</summary>
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
