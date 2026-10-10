using Eunomia.App.Objects;
using Eunomia.App.Sessions;
using Eunomia.Drivers.Abstractions;
using Eunomia.Presentation.Logging;
using Eunomia.UI.Controls.Common.Dialogs;
using Eunomia.UI.Features.TableEditor;
using Eunomia.UI.Shell.Sessions;

namespace Eunomia.UI.Shell;

/// <summary>
/// Table editor no Table tab (anexo C §5; FAM-E01–E03): abre com a definição da tabela focada (ou vazio no "Create new
/// ▸ Table"), mostra o CREATE e o ALTER ao vivo e grava com Save (CREATE ou ALTER na ligação primária); depois de gravar,
/// a árvore recarrega a base e o editor relê a tabela. Discard volta à definição do servidor. Sair com alterações por
/// gravar (outro nó na árvore, fechar a janela) pergunta antes, aqui e no editor por código das views e rotinas.
/// </summary>
public sealed partial class MainWindow
{
    private OpenSession? _tableEditorSession;
    private TableDefinition? _tableEditorBaseline;
    private DbObjectRef? _tableEditorTable;
    private bool _keepTableEditor;

    /// <summary>Substitui a pergunta "Save modified …" (testes): recebe a pergunta e devolve a resposta.</summary>
    internal Func<string, Task<MessageDialogResult>>? SaveTableQuestionOverride { get; set; }

    /// <summary>Editor de tabelas aberto no Table tab (testes).</summary>
    internal TableEditorView? TableEditor => _tabs.TableTab.Content as TableEditorView;

    /// <summary>Há um editor (de tabela ou por código) com alterações por gravar no Table tab.</summary>
    private bool HasUnsavedObjectEditor => TableEditor is { IsModified: true } || CodeEditor is { IsModified: true };

    private void InitTableEditor()
    {
        _context.Commands.SetHandler("object.createTable", _ => new ValueTask(CreateTableAsync()), () => TreeTarget() is { Node.Kind: not Controls.Tree.TreeNodeKind.Session } || DatabaseOfFocus() is not null);
        // Fechar a janela com um objeto por gravar: a mesma pergunta; Cancel não fecha.
        _closeGuards.Add((() => HasUnsavedObjectEditor, ConfirmLeaveObjectEditorAsync));
    }

    /// <summary>
    /// DDL da aplicação (gravar tabela ou objeto, apagar, esvaziar) numa sessão com alterações por confirmar: o servidor
    /// faz commit implícito da transação antes do DDL (14.5), por isso pergunta primeiro. Devolve se pode seguir.
    /// </summary>
    internal async Task<bool> ConfirmImplicitCommitAsync(OpenSession session)
    {
        if (!session.Host.Transactions.HasChanges)
        {
            return true;
        }
        return (await ConfirmAsync("Confirm",
            $"Session \"{session.Name}\" has uncommitted changes in an open transaction. This change is DDL: the server COMMITS the open transaction before running it.\n\nContinue?")).Ok;
    }

    /// <summary>O nó é o objeto aberto no editor (ou uma coluna dele): mudar para ele não sai do editor.</summary>
    private bool IsEditedObject(Controls.Tree.TreeNode node)
    {
        var obj = node.Kind == Controls.Tree.TreeNodeKind.Column ? node.Parent : node;
        var edited = TableEditor is not null ? _tableEditorTable : CodeEditor is not null ? _codeEditorObject : null;
        return edited is { } current && obj?.Tag is TreeObjectTag { Object: { } focused } && focused == current;
    }

    /// <summary>
    /// Sair de um editor com alterações por gravar (anexo C §5): "Save modified table "x"?" (ou "Save new table?", e o
    /// mesmo para views, rotinas, triggers e events) com Yes (grava; se falhar, fica), No (descarta) e Cancel (fica).
    /// Devolve se pode sair.
    /// </summary>
    internal async Task<bool> ConfirmLeaveObjectEditorAsync()
    {
        string question;
        if (TableEditor is { IsModified: true })
        {
            question = _tableEditorTable is null ? "Save new table?" : $"Save modified table \"{_tableEditorTable.Name}\"?";
        }
        else if (CodeEditor is { IsModified: true } code)
        {
            var kind = code.Kind.ToString().ToLowerInvariant();
            question = _codeEditorObject is null ? $"Save new {kind}?" : $"Save modified {kind} \"{_codeEditorObject.Name}\"?";
        }
        else
        {
            return true;
        }
        var answer = SaveTableQuestionOverride is { } ask
            ? await ask(question)
            : await MessageDialog.ShowAsync(this, "Confirm", question, MessageDialogButtons.YesNoCancel);
        switch (answer)
        {
            case MessageDialogResult.Yes:
                return TableEditor is { } table ? await SaveTableAsync(table) : await SaveObjectCodeAsync(CodeEditor!);
            case MessageDialogResult.No:
                TableEditor?.MarkSaved();
                CodeEditor?.MarkSaved();
                return true;
            default:
                return false;
        }
    }

    /// <summary>Outro nó na árvore com um objeto por gravar: pergunta primeiro; com Cancel a árvore volta ao objeto.</summary>
    private async Task LeaveObjectEditorThenNavigateAsync(Controls.Tree.TreeNode node)
    {
        var previous = _previousTreeNode;
        if (await ConfirmLeaveObjectEditorAsync())
        {
            if (ReferenceEquals(_tree.Tree.FocusedNode, node))
            {
                await NavigateAsync(node);
                SyncDiagramsWithTree(node);
            }
            return;
        }
        if (previous is not null && _model.IndexOf(previous) >= 0)
        {
            _keepTableEditor = true;
            try
            {
                _tree.Tree.FocusedNode = previous;
            }
            finally
            {
                _keepTableEditor = false;
            }
        }
    }

    /// <summary>Base do nó focado (a própria base, ou a do objeto).</summary>
    private (OpenSession Session, string Database)? DatabaseOfFocus() =>
        _tree.Tree.FocusedNode?.Tag is TreeObjectTag tag && Sessions?.Connector.Find(tag.Session) is { } open ? (open, tag.Database) : null;

    private async Task CreateTableAsync()
    {
        if (DatabaseOfFocus() is not { } target)
        {
            return;
        }
        _tabs.TableTab.IsVisible = true;
        _tabs.SelectedItem = _tabs.TableTab;
        await OpenTableEditorAsync(target.Session, target.Database, null);
    }

    /// <summary>
    /// Abre o editor: a tabela <paramref name="table"/>, ou uma tabela nova em <paramref name="database"/>. Cancelado (outra
    /// escolha na árvore), não mexe no Table tab.
    /// </summary>
    internal async Task OpenTableEditorAsync(OpenSession session, string database, DbObjectRef? table, CancellationToken cancellationToken = default)
    {
        if (session.Host.ServerServices is not { } services)
        {
            return;
        }
        try
        {
            TableDefinition? original = null;
            if (table is not null)
            {
                session.Metadata.InvalidateTable(table);
                original = await session.Metadata.GetTableAsync(table, cancellationToken);
            }
            var (collations, engines, tables) = await TableEditing.ListsAsync(session, database, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var design = original is null ? TableDesign.New(database) : TableDesign.From(original);
            var baseline = original is null ? null : TableDesign.From(original).ToDefinition(services.Types, services.Dialect);
            var view = new TableEditorView(
                design,
                new TableEditorLists(collations, engines, tables),
                d => string.Join(";\n", services.Ddl.Create(d.ToDefinition(services.Types, services.Dialect))) + ";",
                baseline is null ? null : d => string.Join(";\n", TableEditing.Statements(services, baseline, d)) + (TableEditing.Statements(services, baseline, d).Count > 0 ? ";" : string.Empty));
            view.SaveRequested += (_, _) => _ = SaveTableAsync(view);
            view.DiscardRequested += (_, _) => _ = OpenTableEditorAsync(session, database, baseline is null ? null : table);
            _tableEditorSession = session;
            _tableEditorBaseline = baseline;
            _tableEditorTable = original?.Ref;
            _tabs.TableTab.Content = view;
            Views.MainTabsView.SetFixedHeader(_tabs.TableTab, "Table: " + (original is null ? "[Untitled]" : original.Ref.Name), "table");
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested
                                   && ex is OperationCanceledException or DbOperationException or Engine.Execution.SessionOperationException)
        {
            // Cancelado (outra escolha na árvore, Drop do objeto): o erro de um pedido que já ia a caminho também não interessa.
        }
        catch (Exception ex) when (ex is DbOperationException or Engine.Execution.SessionOperationException or InvalidOperationException)
        {
            ShowError(ex);
        }
    }

    /// <summary>Save: valida, corre o CREATE ou o ALTER, recarrega a base na árvore e relê a tabela gravada.</summary>
    internal async Task<bool> SaveTableAsync(TableEditorView view)
    {
        if (_tableEditorSession is not { Host.ServerServices: { } services } session)
        {
            return false;
        }
        var design = view.Design;
        if (design.Validate() is { Count: > 0 } errors)
        {
            ShowEditError(string.Join(Environment.NewLine, errors));
            return false;
        }
        var statements = TableEditing.Statements(services, _tableEditorBaseline, design);
        if (statements.Count == 0)
        {
            view.MarkSaved();
            return true;
        }
        if (!await ConfirmImplicitCommitAsync(session))
        {
            return false;
        }
        var losses = TableEditing.DataLossWarnings(_tableEditorBaseline, design.ToDefinition(services.Types, services.Dialect));
        if (losses.Count > 0 && !(await ConfirmAsync("Confirm",
                $"Saving table \"{design.Name}\" can lose data:\n\n" + string.Join("\n", losses.Select(l => "• " + l)) +
                "\n\nValues that do not fit the new definition make the change fail instead of being cut. Continue?")).Ok)
        {
            return false;
        }
        _context.Status.Busy = "Saving table ...";
        try
        {
            await TableEditing.ApplyAsync(session, statements, CancellationToken.None, TableEditing.RestoreDroppedForeignKeys(services, _tableEditorBaseline, design));
        }
        catch (Exception ex) when (ex is DbOperationException or Engine.Execution.SessionOperationException)
        {
            ShowEditError(ex.Message);
            return false;
        }
        finally
        {
            _context.Status.SetIdle();
        }
        session.Metadata.Invalidate(design.Database);
        var saved = new DbObjectRef(design.Database, design.Database, design.Name, DbObjectKind.Table);
        await OpenTableEditorAsync(session, design.Database, saved);
        _context.Log.Append(LogCategory.Info, $"Table \"{design.Name}\" saved.");
        return true;
    }
}
