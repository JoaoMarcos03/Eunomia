using System.Globalization;
using Avalonia.Threading;
using Eunomia.App.Commands;
using Eunomia.App.Objects;
using Eunomia.App.Sessions;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Metadata;
using Eunomia.Presentation.Logging;
using Eunomia.Presentation.Objects;
using Eunomia.UI.Controls.Common.Input;
using Eunomia.UI.Controls.Common.TreeList;
using Eunomia.UI.Controls.Tree;
using Eunomia.UI.Features.DatabaseTab;
using Eunomia.UI.Features.HostTab;
using Eunomia.UI.Shell.Sessions;

namespace Eunomia.UI.Shell;

/// <summary>
/// Host tab e Database tab sobre a sessão ativa (Fase 4; secção 4.9): listas carregadas quando o tab é mostrado e
/// atualizadas quando a cache de metadata muda; "Fetch database items", Kill Process(es), rename F2 e status bar.
/// </summary>
public sealed partial class MainWindow
{
    private HostTabView? _hostView;
    private DatabaseTabView? _databaseView;

    /// <summary>Host tab (criado na primeira vez que é preciso).</summary>
    internal HostTabView HostView
    {
        get
        {
            if (_hostView is null)
            {
                _hostView = new HostTabView();
                _tabs.HostTab.Content = _hostView;
                CommandScope.SetContext(_hostView.DatabaseList, CommandContexts.HostList);
                foreach (var list in new[] { _hostView.DatabaseList, _hostView.VariableList, _hostView.StatusList, _hostView.ProcessList, _hostView.CommandList })
                {
                    CommandScope.SetContext(list, CommandContexts.HostList);
                    list.ContextMenu = _ui.BuildContextMenu(MainMenuLayout.HostMenu);
                }
                _hostView.SelectionChanged += (_, _) => _ = RefreshHostAsync();
                ApplyListOptions();
                _hostView.DatabaseActivated += (_, db) => FocusTreeDatabase(db);
                _hostView.DatabaseList.SelectionChanged += (_, _) => _ui.Refresh();
                _hostView.ProcessList.SelectionChanged += (_, _) => _ui.Refresh();
            }
            return _hostView;
        }
    }

    /// <summary>Database tab (criado na primeira vez que é preciso).</summary>
    internal DatabaseTabView DatabaseView
    {
        get
        {
            if (_databaseView is null)
            {
                _databaseView = new DatabaseTabView();
                _tabs.DatabaseTab.Content = _databaseView;
                var list = _databaseView.List;
                CommandScope.SetContext(list, CommandContexts.ObjectList);
                list.ContextMenu = _ui.BuildContextMenu(MainMenuLayout.DatabaseMenu);
                if (_context.Settings.Lists.TryGetValue(DatabaseListKey, out var columns))
                {
                    _databaseView.VisibleColumns = columns;
                }
                _databaseView.ColumnsChanged += (_, _) => _context.Settings.Lists[DatabaseListKey] = [.. _databaseView.VisibleColumns];
                _databaseView.ObjectActivated += (_, obj) => FocusTreeObject(obj.Ref);
                ApplyListOptions();
                list.RenameRequested += (_, e) => _ = RenameObjectAsync(e);
                list.SelectionChanged += (_, _) =>
                {
                    _context.Status.Position = "Selected: " + list.SelectedNodes.Count.ToString(CultureInfo.CurrentCulture);
                    _ui.Refresh();
                };
            }
            return _databaseView;
        }
    }

    private const string DatabaseListKey = "databaseObjects";

    private void InitLists()
    {
        var c = _context.Commands;
        c.SetHandler("host.fetchDatabaseItems", _ => new ValueTask(FetchDatabaseItemsAsync()),
            () => _hostView is { } v && ReferenceEquals(v.SelectedItem, v.DatabasesTab) && v.SelectedDatabases.Count > 0);
        c.SetHandler("host.killProcess", _ => new ValueTask(KillProcessesAsync()),
            () => _hostView is { } v && ReferenceEquals(v.SelectedItem, v.ProcessesTab) && v.SelectedProcesses.Count > 0);
        _tabs.SelectionChanged += (_, _) =>
        {
            if (ReferenceEquals(_tabs.SelectedItem, _tabs.HostTab))
            {
                _ = RefreshHostAsync();
            }
            else if (ReferenceEquals(_tabs.SelectedItem, _tabs.DatabaseTab))
            {
                _ = RefreshDatabaseTabAsync();
            }
        };
    }

    /// <summary>A cache mudou (objetos carregados, estatísticas): atualiza o Host ou o Database tab se estiverem à vista.</summary>
    private void OnMetadataChangedForLists(OpenSession open, MetadataChangedEventArgs e)
    {
        if (!ReferenceEquals(ActiveSession, open))
        {
            return;
        }
        if (ReferenceEquals(_tabs.SelectedItem, _tabs.HostTab) && _hostView is { } host && ReferenceEquals(host.SelectedItem, host.DatabasesTab))
        {
            _ = RefreshHostAsync();
        }
        else if (ReferenceEquals(_tabs.SelectedItem, _tabs.DatabaseTab) && e.Database is { } db && _databaseView?.Database == db
                 && open.Metadata.CachedObjects(db) is { } objects)
        {
            ShowDatabaseObjects(db, objects.Objects);
        }
    }

    /// <summary>Host tab: a lista do sub-tab visível (Databases, Variables, Status, Processes ou Command-Statistics).</summary>
    private async Task RefreshHostAsync()
    {
        if (ActiveSession is not { } open || !ReferenceEquals(_tabs.SelectedItem, _tabs.HostTab))
        {
            return;
        }
        var view = HostView;
        try
        {
            if (ReferenceEquals(view.SelectedItem, view.DatabasesTab))
            {
                var names = await open.Metadata.GetDatabasesAsync(CancellationToken.None);
                IReadOnlyDictionary<string, string>? collations = null;
                try
                {
                    collations = await open.Metadata.GetDatabaseCollationsAsync(CancellationToken.None);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _context.Log.Append(LogCategory.Error, ex.Message);
                }
                view.ShowDatabases(HostDatabases.Build(names, open.Metadata.CachedObjects, collations));
            }
            else if (ReferenceEquals(view.SelectedItem, view.ProcessesTab))
            {
                view.ShowProcesses(await open.Host.RunMetadataAsync((h, ct) => h.Metadata.GetProcessesAsync(ct), CancellationToken.None));
            }
            else if (ReferenceEquals(view.SelectedItem, view.VariablesTab))
            {
                // Os valores da sessão são os da ligação principal (a das queries e do Data tab).
                var session = await open.Host.RunPrimaryAsync((h, ct) => h.Metadata.GetVariablesAsync(session: true, ct), CancellationToken.None, applyQueryTimeout: false);
                var global = await open.Host.RunMetadataAsync((h, ct) => h.Metadata.GetVariablesAsync(session: false, ct), CancellationToken.None);
                view.ShowVariables(HostVariables.Build(session, global));
            }
            else if (ReferenceEquals(view.SelectedItem, view.StatusTab))
            {
                view.ShowStatus(HostStatus.Build(await open.Host.RunMetadataAsync((h, ct) => h.Metadata.GetStatusAsync(ct), CancellationToken.None)));
            }
            else if (ReferenceEquals(view.SelectedItem, view.CommandStatisticsTab))
            {
                view.ShowCommandStatistics(CommandStatistics.Build(await open.Host.RunMetadataAsync((h, ct) => h.Metadata.GetStatusAsync(ct), CancellationToken.None)));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _context.Log.Append(LogCategory.Error, ex.Message);
        }
        _ui.Refresh();
    }

    /// <summary>"Fetch database items": relê os objetos das bases selecionadas (anexo D A7).</summary>
    private async Task FetchDatabaseItemsAsync()
    {
        if (ActiveSession is not { } open || _hostView is not { } view)
        {
            return;
        }
        foreach (var db in view.SelectedDatabases)
        {
            open.Metadata.Invalidate(db);
            await open.Metadata.GetObjectsAsync(db, CancellationToken.None);
        }
        await RefreshHostAsync();
    }

    /// <summary>Kill Process(es) (Del): pergunta e mata os processos selecionados, menos as ligações da própria sessão.</summary>
    private async Task KillProcessesAsync()
    {
        if (ActiveSession is not { } open || _hostView is not { } view || open.Host.ServerServices is not { } services)
        {
            return;
        }
        var ids = ProcessList.ToKill(view.SelectedProcesses.Select(p => p.Id), [.. open.Host.OwnConnectionIds.Select(i => (long?)i), open.Host.PrimaryConnectionId]);
        if (ids.Count == 0 || !(await ConfirmAsync("Confirm", $"Kill {ids.Count.ToString(CultureInfo.CurrentCulture)} Process(es)?" + SessionLine(open))).Ok)
        {
            return;
        }
        try
        {
            await open.Host.RunMetadataAsync(async (h, ct) =>
            {
                foreach (var id in ids)
                {
                    await h.ExecuteNonQueryAsync(services.Dialect.KillConnection(id), ct);
                }
                return true;
            }, CancellationToken.None);
        }
        finally
        {
            await RefreshHostAsync();
        }
    }

    /// <summary>Database tab: os objetos da base do nó focado (carrega a lista, como o HeidiSQL ao pintar a lista).</summary>
    private async Task RefreshDatabaseTabAsync()
    {
        if (ActiveSession is not { } open || _tree.Tree.FocusedNode?.Database is not { } dbNode)
        {
            return;
        }
        try
        {
            var objects = await open.Metadata.GetObjectsAsync(dbNode.Name, CancellationToken.None);
            ShowDatabaseObjects(dbNode.Name, objects.Objects);
            if (open.Metadata.CachedObjects(dbNode.Name) is { } cached)
            {
                SessionTreeProvider.ApplyObjects(dbNode, cached);
                _tree.Tree.InvalidateVisual();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _context.Log.Append(LogCategory.Error, ex.Message);
        }
    }

    private void ShowDatabaseObjects(string database, IReadOnlyList<DbObjectInfo> objects)
    {
        var view = DatabaseView;
        view.Show(database, objects);
        if (_tree.Tree.FocusedNode?.Tag is TreeObjectTag { Object: { } focused })
        {
            view.Focus(focused);
        }
        _context.Status.Info = DatabaseObjectList.StatusText(database, objects);
    }

    /// <summary>Rename F2 na lista: <c>RENAME TABLE</c> em tabelas e views; os outros tipos não se renomeiam aqui.</summary>
    internal async Task RenameObjectAsync(TreeListRenameEventArgs e)
    {
        if (ActiveSession is not { } open || e.Node.Tag is not DbObjectRef obj || open.Host.ServerServices is not { } services)
        {
            return;
        }
        if (obj.Kind is not (DbObjectKind.Table or DbObjectKind.View))
        {
            ShowError(new InvalidOperationException("Cannot rename " + DatabaseObjectList.TypeName(obj.Kind).ToLowerInvariant()));
            return;
        }
        try
        {
            await ObjectActions.RunAsync(open.Host, [services.Dialect.Rename(obj, e.Text)], CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowError(ex);
        }
        finally
        {
            await ReloadDatabaseObjectsAsync(open, obj.Schema);
        }
    }

    /// <summary>Esquece a cache de uma base e atualiza a árvore e o Database tab.</summary>
    private async Task ReloadDatabaseObjectsAsync(OpenSession open, string database)
    {
        open.Metadata.Invalidate(database);
        if (_sessionNodes.TryGetValue(open.Id, out var session) && session.Children?.FirstOrDefault(n => n.Kind == TreeNodeKind.Database && n.Name == database) is { } dbNode)
        {
            await ReloadKeepingFocusAsync(dbNode, _tree.Tree.FocusedNode is { } f && ReferenceEquals(f.Database, dbNode) ? f : dbNode);
        }
        if (ReferenceEquals(_tabs.SelectedItem, _tabs.DatabaseTab))
        {
            await RefreshDatabaseTabAsync();
        }
    }

    /// <summary>Duplo clique numa base do Host tab: foca-a na árvore (o Database tab abre).</summary>
    private void FocusTreeDatabase(string database)
    {
        if (ActiveSession is { } open && _sessionNodes.TryGetValue(open.Id, out var session)
            && session.Children?.FirstOrDefault(n => n.Kind == TreeNodeKind.Database && n.Name == database) is { } dbNode)
        {
            _tree.Tree.FocusedNode = dbNode;
        }
    }

    /// <summary>Enter ou duplo clique na lista de objetos: foca o objeto na árvore (o Table tab abre).</summary>
    private void FocusTreeObject(DbObjectRef obj)
    {
        if (ActiveSession is { } open)
        {
            _ = FocusTreeObjectAsync(open.Id, obj);
        }
    }

    /// <summary>Foca um objeto de uma sessão na árvore (expande a base se for preciso); <c>false</c> se não o encontrar.</summary>
    private async Task<bool> FocusTreeObjectAsync(Guid sessionId, DbObjectRef obj)
    {
        if (!_sessionNodes.TryGetValue(sessionId, out var session)
            || session.Children?.FirstOrDefault(n => n.Kind == TreeNodeKind.Database && n.Name == obj.Schema) is not { } dbNode)
        {
            return false;
        }
        await _model.ExpandAsync(dbNode);
        if (TreeModel.ObjectsOf(dbNode).FirstOrDefault(n => n.Tag is TreeObjectTag { Object: { } r } && r == obj) is not { } node)
        {
            return false;
        }
        _model.Reveal(node);
        _tree.Tree.FocusedNode = node;
        return true;
    }

    /// <summary>Objetos selecionados na lista do Database tab, quando é ela que tem o foco.</summary>
    private (OpenSession Session, string Database, IReadOnlyList<DbObjectRef> Objects)? ListTarget()
    {
        if (_databaseView is { Database: { } db } view && view.List.IsKeyboardFocusWithin && ActiveSession is { Host.ServerServices: not null } open)
        {
            var selected = view.SelectedObjects.Select(o => o.Ref).ToList();
            return selected.Count > 0 ? (open, db, selected) : null;
        }
        return null;
    }

    private void PostListsRefresh(OpenSession open, MetadataChangedEventArgs e) =>
        Dispatcher.UIThread.Post(() => OnMetadataChangedForLists(open, e));
}
