using Avalonia.Controls;
using Eunomia.App.Sessions;
using Eunomia.Drivers.Abstractions;
using Eunomia.Presentation.Logging;
using Eunomia.UI.Controls.SqlEditor;
using Eunomia.UI.Controls.Tree;
using Eunomia.UI.Shell.Sessions;
using Eunomia.UI.Shell.Views;

namespace Eunomia.UI.Shell;

/// <summary>
/// Tree selection → tabs (FAM-L04, L08; appendix D A11): session → Host; database or group → <c>USE</c> and Database; object
/// → <c>USE</c>, cached definition and Table tab only if the current tab is Host or Database (otherwise it stays on Data or
/// Query). Captions "Host" = session, "Database: x", "Table: y" with the kind's icon; Data only for tables, views and
/// columns. A failed <c>USE</c> shows the error and goes back to the previous node. Whatever was loading for the previous
/// choice (data, definition, editor) is cancelled: with a slow server, it does not keep the connections busy.
/// </summary>
public sealed partial class MainWindow
{
    private TreeNode? _previousTreeNode;
    private bool _restoringTreeNode;
    private CancellationTokenSource? _editorLoad;

    /// <summary>Work requested for the focused node (the object's definition), cancelled on the next choice.</summary>
    private CancellationTokenSource? _navigation;

    private async Task NavigateAsync(TreeNode node)
    {
        // Before the new database's USE, which on the primary connection waited for the Data tab load to finish.
        _navigation?.Cancel();
        var navigation = new CancellationTokenSource();
        _navigation = navigation;
        _editorLoad?.Cancel();
        if (!IsDataTarget(node))
        {
            AbandonDataLoad();
        }
        if (node.Session?.Tag is not Guid id || Sessions?.Connector.Find(id) is not { } open)
        {
            return;
        }
        var before = _tabs.SelectedItem;
        MarkTreeSelection(node);
        ApplyTabsFor(node, open);
        var queryActive = _tabs.ActiveQueryTab is not null;
        var tag = node.Tag as TreeObjectTag;

        if (node.Kind == TreeNodeKind.Session)
        {
            if (!queryActive)
            {
                _tabs.SelectedItem = _tabs.HostTab;
            }
        }
        else if (tag is not null && await UseDatabaseAsync(open, tag.Database, node))
        {
            if (navigation.IsCancellationRequested)
            {
                // Another node chosen while the USE was waiting: the next navigation takes care of the tabs.
                return;
            }
            if (tag.Object is null)
            {
                if (!queryActive)
                {
                    _tabs.SelectedItem = _tabs.DatabaseTab;
                }
            }
            else
            {
                if (ReferenceEquals(_tabs.SelectedItem, _tabs.HostTab) || ReferenceEquals(_tabs.SelectedItem, _tabs.DatabaseTab)
                    || _tabs.SelectedItem is TabItem { IsVisible: false })
                {
                    _tabs.SelectedItem = _tabs.TableTab;
                }
                LoadObjectDefinition(open, tag.Object, navigation.Token);
                // A tab that was just selected already loaded (SelectionChanged); the one that stayed has to reload.
                if (ReferenceEquals(before, _tabs.SelectedItem))
                {
                    if (ReferenceEquals(_tabs.SelectedItem, _tabs.TableTab))
                    {
                        LoadObjectEditor();
                    }
                    else if (ReferenceEquals(_tabs.SelectedItem, _tabs.DataTab))
                    {
                        LoadDataForFocusedNode();
                    }
                }
            }
        }
        // The Host and Database tabs load when selected; if they already were, they switch session or database here.
        if (ReferenceEquals(before, _tabs.SelectedItem))
        {
            if (ReferenceEquals(_tabs.SelectedItem, _tabs.HostTab))
            {
                _ = RefreshHostAsync();
            }
            else if (ReferenceEquals(_tabs.SelectedItem, _tabs.DatabaseTab))
            {
                _ = RefreshDatabaseTabAsync();
            }
        }
        if (!_restoringTreeNode)
        {
            _previousTreeNode = node;
        }
        UpdateStatusBar();
        _ui.Refresh();
    }

    /// <summary>Captions and visibility of the fixed tabs for the node (appendix D A11, points 4 and 5).</summary>
    private void ApplyTabsFor(TreeNode node, OpenSession open)
    {
        MainTabsView.SetFixedCaption(_tabs.HostTab, open.Name);
        var db = node.Database;
        _tabs.DatabaseTab.IsVisible = db is not null;
        if (db is not null)
        {
            MainTabsView.SetFixedCaption(_tabs.DatabaseTab, "Database: " + db.Name);
        }
        var obj = node.Kind == TreeNodeKind.Column ? node.Parent : node;
        var isObject = obj is { IsObject: true };
        _tabs.TableTab.IsVisible = isObject;
        if (isObject)
        {
            var (caption, icon) = obj!.Kind switch
            {
                TreeNodeKind.View => ("View", "view"),
                TreeNodeKind.Procedure => ("Procedure", "procedure"),
                TreeNodeKind.Function => ("Function", "function"),
                TreeNodeKind.Trigger => ("Trigger", "trigger"),
                TreeNodeKind.Event => ("Event", "event"),
                _ => ("Table", "table"),
            };
            MainTabsView.SetFixedHeader(_tabs.TableTab, caption + ": " + obj.Name, icon);
        }
        _tabs.DataTab.IsVisible = obj is { Kind: TreeNodeKind.Table or TreeNodeKind.View };
        if (_tabs.SelectedItem is TabItem { IsVisible: false })
        {
            _tabs.SelectedItem = isObject ? _tabs.TableTab : db is not null ? _tabs.DatabaseTab : _tabs.HostTab;
        }
    }

    /// <summary><c>USE</c> only when the database changes; on error, a dialog and back to the previous node (appendix D A11).</summary>
    private async Task<bool> UseDatabaseAsync(OpenSession open, string database, TreeNode node)
    {
        if (string.Equals(open.Host.CurrentDatabase, database, StringComparison.Ordinal))
        {
            return true;
        }
        try
        {
            await open.Host.UseDatabaseAsync(database, CancellationToken.None);
            open.Definition.LastUsedDatabase = database;
            UpdateCurrentDatabase(open);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowError(ex);
            if (_previousTreeNode is { } previous && !ReferenceEquals(previous, node) && _model.IndexOf(previous) >= 0)
            {
                _restoringTreeNode = true;
                try
                {
                    _tree.Tree.FocusedNode = previous;
                }
                finally
                {
                    _restoringTreeNode = false;
                }
            }
            return false;
        }
    }

    /// <summary>Columns, keys and FKs of tables and views for the cache (the Table tab and the grid use them).</summary>
    private void LoadObjectDefinition(OpenSession open, DbObjectRef obj, CancellationToken cancellationToken)
    {
        if (obj.Kind is not (DbObjectKind.Table or DbObjectKind.View))
        {
            return;
        }
        _ = LoadDefinitionAsync();

        async Task LoadDefinitionAsync()
        {
            try
            {
                await open.Metadata.GetTableAsync(obj, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Another node chosen in the meantime.
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // An invalid view gives an error when reading its keys (HeidiSQL warns and carries on).
                _context.Log.Append(LogCategory.Error, obj.Kind == DbObjectKind.View ? "This view probably contains an error: " + ex.Message : ex.Message);
            }
        }
    }

    /// <summary>
    /// Table tab: the table editor for tables (phase 7); for views, routines, triggers and events, the code editor
    /// (phase 13).
    /// </summary>
    private void LoadObjectEditor()
    {
        var node = _tree.Tree.FocusedNode;
        if (node?.Tag is not TreeObjectTag { Object: { } obj } tag || Sessions?.Connector.Find(tag.Session) is not { } open)
        {
            return;
        }
        _editorLoad?.Cancel();
        var cts = new CancellationTokenSource();
        _editorLoad = cts;
        if (obj.Kind == DbObjectKind.Table)
        {
            _ = OpenTableEditorAsync(open, tag.Database, obj, cts.Token);
            return;
        }
        _ = OpenObjectCodeEditorAsync(open, tag.Database, obj, cts.Token);
    }
}
