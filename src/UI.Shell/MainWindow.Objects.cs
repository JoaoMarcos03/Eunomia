using System.Globalization;
using Avalonia.Input;
using Eunomia.App.Commands;
using Eunomia.App.Objects;
using Eunomia.App.Sessions;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Sql;
using Eunomia.UI.Controls.Common.Dialogs;
using Eunomia.UI.Controls.Tree;
using Eunomia.UI.Shell.Sessions;
using Eunomia.UI.Shell.Views;

namespace Eunomia.UI.Shell;

/// <summary>
/// <c>popupDB</c> na árvore (Fase 4; anexo C §3.3; anexo B §6): Drop, Empty table(s), Select top N rows, Create
/// database, Edit, Clear data tab filter, e o texto inserido no editor por duplo clique e por drag (FAM-T07).
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Substitui os diálogos de confirmação (testes): recebe título, mensagem e caixa de verificação.</summary>
    internal Func<string, string, string?, Task<(bool Ok, bool Verified)>>? ConfirmOverride { get; set; }

    /// <summary>Substitui o diálogo Create database (testes): recebe as collations e a pré-selecionada.</summary>
    internal Func<IReadOnlyList<string>, string?, Task<CreateDatabaseRequest?>>? CreateDatabaseOverride { get; set; }

    private void InitObjectCommands()
    {
        var c = _context.Commands;
        _tree.Tree.ContextMenu = _ui.BuildContextMenu(MainMenuLayout.DatabaseMenu);
        c.SetHandler("object.drop", _ => new ValueTask(DropAsync()),
            () => ListTarget() is not null || TreeTarget() is { } t && (t.Node.IsObject || t.Node.Kind == TreeNodeKind.Database));
        c.SetHandler("object.emptyTables", _ => new ValueTask(EmptyTablesAsync()), () => ObjectsTarget() is { } t && t.Objects.Any(IsTableOrView));
        c.SetHandler("object.selectTopRows", _ => new ValueTask(SelectTopRowsAsync()), () => FocusedObject() is { } o && IsTableOrView(o.Object));
        c.SetHandler("object.createDatabase", _ => new ValueTask(CreateDatabaseAsync()), () => TreeTarget() is { Node.Kind: TreeNodeKind.Session });
        c.SetHandler("object.edit", _ => new ValueTask(EditObjectAsync()), () => FocusedObject() is not null);
        c.SetHandler("object.clearDataTabFilter", ClearDataTabFilter, () => ObjectsTarget() is { } t && t.Objects.Any(IsTableOrView));
    }

    /// <summary>Edit (Alt+Enter): abre o objeto focado no Table tab (o editor da tabela, view ou rotina).</summary>
    private async Task EditObjectAsync()
    {
        if (FocusedObject() is not ({ } open, { } obj))
        {
            return;
        }
        if (_tree.Tree.FocusedNode?.Tag is not TreeObjectTag { Object: { } current } || current != obj)
        {
            await FocusTreeObjectAsync(open.Id, obj);
        }
        _tabs.SelectedItem = _tabs.TableTab;
    }

    /// <summary>Clear data tab filter and sort order: apaga o filtro e a ordenação gravados das tabelas escolhidas.</summary>
    private void ClearDataTabFilter()
    {
        if (ObjectsTarget() is not ({ } open, _, { } objects))
        {
            return;
        }
        foreach (var table in objects.Where(IsTableOrView))
        {
            var key = KeyOf(open, table);
            SetTableState(key, "Filter", null);
            SetTableState(key, "Sort", null);
            if (_dataTarget is { } target && target.Session == open.Id && target.Table == table)
            {
                _dataView.FilterEditor.Text = string.Empty;
                LoadDataForFocusedNode(force: true);
            }
        }
    }

    private static bool IsTableOrView(DbObjectRef obj) => obj.Kind is DbObjectKind.Table or DbObjectKind.View;

    /// <summary>Nó focado na árvore com a sessão aberta a que pertence.</summary>
    private (OpenSession Session, TreeNode Node, TreeObjectTag? Tag)? TreeTarget()
    {
        if (_tree.Tree.FocusedNode is not { Session.Tag: Guid id } node || Sessions?.Connector.Find(id) is not { } open || open.Host.ServerServices is null)
        {
            return null;
        }
        return (open, node, node.Tag as TreeObjectTag);
    }

    /// <summary>
    /// Linha com a sessão e o servidor nas perguntas de ações destrutivas (Fase 14): quem tem várias sessões abertas vê em
    /// que servidor vai apagar antes de confirmar.
    /// </summary>
    private static string SessionLine(OpenSession session) =>
        $"\n\nSession: {session.Name} ({session.Definition.Host}:{session.Definition.Port})";

    private async Task<(bool Ok, bool Verified)> ConfirmAsync(string title, string message, string? verification = null)
    {
        if (ConfirmOverride is { } confirm)
        {
            return await confirm(title, message, verification);
        }
        if (verification is null)
        {
            return (await MessageDialog.ShowAsync(this, title, message, MessageDialogButtons.OkCancel) == MessageDialogResult.Ok, false);
        }
        var (result, verified) = await MessageDialog.ShowWithVerificationAsync(this, title, message, MessageDialogButtons.OkCancel, verification);
        return (result == MessageDialogResult.Ok, verified);
    }

    /// <summary>Objetos alvo: a seleção da lista do Database tab, se tiver o foco; senão o objeto focado na árvore.</summary>
    private (OpenSession Session, string Database, IReadOnlyList<DbObjectRef> Objects)? ObjectsTarget()
    {
        if (ListTarget() is { } list)
        {
            return list;
        }
        return TreeTarget() is ({ } open, _, { Object: { } obj } tag) ? (open, tag.Database, [obj]) : null;
    }

    /// <summary>Objeto focado (lista do Database tab com foco, ou árvore).</summary>
    private (OpenSession Session, DbObjectRef Object)? FocusedObject()
    {
        if (_databaseView is { } view && view.List.IsKeyboardFocusWithin)
        {
            return view.List.FocusedNode?.Tag is DbObjectRef obj && ActiveSession is { Host.ServerServices: not null } open ? (open, obj) : null;
        }
        return TreeTarget() is ({ } session, _, { Object: { } focused }) ? (session, focused) : null;
    }

    private async Task DropAsync()
    {
        if (ListTarget() is null && TreeTarget() is ({ } open, { Kind: TreeNodeKind.Database } node, _))
        {
            if (!await ConfirmImplicitCommitAsync(open) || !(await ConfirmAsync("Confirm", ObjectActions.DropDatabaseQuestion(node.Name) + SessionLine(open))).Ok)
            {
                return;
            }
            var siblings = node.Parent?.Children?.Select(n => n.Name).ToList() ?? [];
            CancelLoadsOf(open, obj => string.Equals(obj.Schema, node.Name, StringComparison.Ordinal));
            try
            {
                await ObjectActions.DropDatabaseAsync(open.Host, node.Name, siblings, CancellationToken.None);
            }
            finally
            {
                open.Metadata.Invalidate();
                await ReloadKeepingFocusAsync(node.Session!, node);
            }
            return;
        }
        if (ObjectsTarget() is not ({ } session, { } database, { } objects))
        {
            return;
        }
        if (!await ConfirmImplicitCommitAsync(session) || !(await ConfirmAsync("Confirm", ObjectActions.DropObjectsQuestion(database, objects) + SessionLine(session))).Ok)
        {
            return;
        }
        CancelLoadsOf(session, objects.Contains);
        try
        {
            await ObjectActions.RunAsync(session.Host, ObjectActions.DropObjects(session.Host.ServerServices!, objects), CancellationToken.None);
        }
        finally
        {
            await ReloadDatabaseObjectsAsync(session, database);
        }
    }

    /// <summary>
    /// Antes de um Drop: a navegação pendente para o objeto focado (à espera do <c>USE</c>), a carga da definição no Table
    /// tab e a do Data tab de um objeto que vai deixar de existir acabavam num erro "doesn't exist" em diálogo depois do
    /// Drop (num servidor lento, ou com o Drop logo a seguir a escolher o objeto).
    /// </summary>
    private void CancelLoadsOf(OpenSession session, Func<DbObjectRef, bool> dropped)
    {
        if (_tree.Tree.FocusedNode?.Tag is TreeObjectTag { Object: { } focused } tag && tag.Session == session.Id && dropped(focused))
        {
            _navigation?.Cancel();
            _editorLoad?.Cancel();
        }
        if (_dataTarget is { } target && target.Session == session.Id && dropped(target.Table))
        {
            AbandonDataLoad();
        }
    }

    private async Task EmptyTablesAsync()
    {
        if (ObjectsTarget() is not ({ } open, { } database, { } objects))
        {
            return;
        }
        var tables = objects.Where(IsTableOrView).ToList();
        if (!await ConfirmImplicitCommitAsync(open))
        {
            return;
        }
        var (ok, disableForeignKeys) = await ConfirmAsync("Confirm", ObjectActions.EmptyTablesQuestion(tables) + SessionLine(open), "Disable foreign key checks");
        if (!ok)
        {
            return;
        }
        try
        {
            await ObjectActions.RunAsync(open.Host, ObjectActions.EmptyTables(open.Host.ServerServices!, tables, disableForeignKeys), CancellationToken.None);
        }
        finally
        {
            // Os tamanhos mudaram: relê a base (nomes e estatísticas).
            await ReloadDatabaseObjectsAsync(open, database);
        }
    }

    /// <summary>"Select top N rows": query tab nova com o SELECT e corre-o (anexo C §3.3).</summary>
    private async Task SelectTopRowsAsync()
    {
        if (FocusedObject() is not ({ } open, { } table))
        {
            return;
        }
        var sql = ObjectActions.SelectTopRows(open.Host.ServerServices!.Dialect, table, _context.Settings.Data.RowsPerStep, qualified: false);
        if (!string.Equals(open.Host.CurrentDatabase, table.Schema, StringComparison.Ordinal))
        {
            await open.Host.UseDatabaseAsync(table.Schema, CancellationToken.None);
            UpdateCurrentDatabase(open);
        }
        var tab = _tabs.AddQueryTab();
        QueryEditorOf(tab).Text = sql;
        await RunQueryAsync(RunScope.All);
    }

    private async Task CreateDatabaseAsync()
    {
        if (TreeTarget() is not ({ } open, { } node, _))
        {
            return;
        }
        var services = open.Host.ServerServices!;
        var collations = (await open.Host.RunMetadataAsync((h, ct) => h.Metadata.GetCollationsAsync(ct), CancellationToken.None))
            .Select(c => c.Name)
            .ToList();
        var serverDefault = await open.Host.RunMetadataAsync(
            async (h, ct) => Convert.ToString(await h.ExecuteScalarAsync("SELECT @@collation_server", ct), CultureInfo.InvariantCulture),
            CancellationToken.None);
        var dialogs = _context.Settings.Dialogs;
        var preselected = ObjectActions.PreselectCollation(collations, dialogs.CreateDatabaseCollation, serverDefault);
        var request = CreateDatabaseOverride is { } create
            ? await create(collations, preselected)
            : await CreateDatabaseDialog.ShowAsync(this, collations, preselected, serverDefault, services.Ddl.CreateDatabase);
        if (request is null)
        {
            return;
        }
        dialogs.CreateDatabaseCollation = request.Collation;
        var session = node.Session!;
        try
        {
            await ObjectActions.RunAsync(open.Host, [services.Ddl.CreateDatabase(request.Name, request.Collation)], CancellationToken.None);
        }
        finally
        {
            open.Metadata.Invalidate();
            await ReloadKeepingFocusAsync(session, node);
        }
        if (session.Children?.FirstOrDefault(n => n.Name == request.Name) is { } created)
        {
            _tree.Tree.FocusedNode = created;
        }
    }

    /// <summary>
    /// Texto que um nó insere no editor (duplo clique ou drag, anexo D A12): base, objeto ou coluna com quoting mínimo;
    /// com Shift, qualificado com a base (e, numa coluna, também com a tabela).
    /// </summary>
    private string? NodeInsertText(TreeNode node, bool qualified)
    {
        var db = node.Database;
        var language = LanguageOf(node);
        if (node.Kind == TreeNodeKind.Database)
        {
            return language.EditorIdentifier(node.Name);
        }
        if (node.IsObject)
        {
            return qualified && db is not null ? language.EditorQualifiedName(db.Name, node.Name) : language.EditorIdentifier(node.Name);
        }
        if (node.Kind == TreeNodeKind.Column)
        {
            return qualified && db is not null && node.Parent is { } table
                ? language.EditorQualifiedName(db.Name, table.Name) + "." + language.EditorIdentifier(node.Name)
                : language.EditorIdentifier(node.Name);
        }
        return null;
    }

    /// <summary>Linguagem da sessão de um nó da árvore (a de omissão se a sessão já fechou).</summary>
    private ISqlLanguage LanguageOf(TreeNode node) =>
        node.Session?.Tag is Guid id && Sessions?.Connector.Find(id) is { } open ? open.Language : SqlLanguages.Default;

    /// <summary>Drop no editor: só aceita nós da árvore.</summary>
    private string? TreeDropText(IDataTransfer data, KeyModifiers modifiers) =>
        data.TryGetValue(DatabaseTreeControl.NodeFormat) is { } node ? NodeInsertText(node, modifiers.HasFlag(KeyModifiers.Shift)) : null;
}
