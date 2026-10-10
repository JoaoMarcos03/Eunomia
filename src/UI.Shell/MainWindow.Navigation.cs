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
/// Seleção na árvore → tabs (FAM-L04, L08; anexo D A11): sessão → Host; base ou grupo → <c>USE</c> e Database; objeto
/// → <c>USE</c>, definição em cache e Table tab só se o tab atual for Host ou Database (senão fica no Data ou na
/// Query). Captions "Host" = sessão, "Database: x", "Table: y" com o ícone do tipo; Data só para tabelas, views e
/// colunas. Um <c>USE</c> que falhe mostra o erro e volta ao nó anterior. O que se estava a carregar para a escolha
/// anterior (dados, definição, editor) é cancelado: com um servidor lento, não fica a ocupar as ligações.
/// </summary>
public sealed partial class MainWindow
{
    private TreeNode? _previousTreeNode;
    private bool _restoringTreeNode;
    private CancellationTokenSource? _editorLoad;

    /// <summary>Trabalho pedido para o nó focado (definição do objeto), cancelado na escolha seguinte.</summary>
    private CancellationTokenSource? _navigation;

    private async Task NavigateAsync(TreeNode node)
    {
        // Antes do USE da base nova, que na ligação primária esperava pelo fim da carga do Data tab.
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
                // Outro nó escolhido enquanto o USE esperava: a navegação seguinte trata das tabs.
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
                // Uma tab que acabou de ser selecionada já carregou (SelectionChanged); a que ficou tem de recarregar.
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
        // O Host e o Database tab carregam ao ser selecionados; se já estavam, mudam de sessão ou de base aqui.
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

    /// <summary>Captions e visibilidade das tabs fixas para o nó (anexo D A11, ponto 4 e 5).</summary>
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

    /// <summary><c>USE</c> só quando a base muda; em erro, diálogo e volta ao nó anterior (anexo D A11).</summary>
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

    /// <summary>Colunas, chaves e FKs de tabelas e views para a cache (o Table tab e o grid usam-nas).</summary>
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
                // Outro nó escolhido entretanto.
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Uma view inválida dá erro ao ler as chaves (o HeidiSQL avisa e continua).
                _context.Log.Append(LogCategory.Error, obj.Kind == DbObjectKind.View ? "This view probably contains an error: " + ex.Message : ex.Message);
            }
        }
    }

    /// <summary>
    /// Table tab: o table editor nas tabelas (Fase 7); nas views, rotinas, triggers e events, o editor por código
    /// (Fase 13).
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
