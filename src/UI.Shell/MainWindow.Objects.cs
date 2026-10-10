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
/// <c>popupDB</c> in the tree (phase 4; appendix C §3.3; appendix B §6): Drop, Empty table(s), Select top N rows, Create
/// database, Edit, Clear data tab filter, and the text inserted in the editor by double click and drag (FAM-T07).
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Replaces the confirmation dialogs (tests): receives the title, the message and the check box.</summary>
    internal Func<string, string, string?, Task<(bool Ok, bool Verified)>>? ConfirmOverride { get; set; }

    /// <summary>Replaces the Create database dialog (tests): receives the collations and the preselected one.</summary>
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

    /// <summary>Edit (Alt+Enter): opens the focused object in the Table tab (the table, view or routine editor).</summary>
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

    /// <summary>Clear data tab filter and sort order: deletes the saved filter and sort order of the chosen tables.</summary>
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

    /// <summary>Node focused in the tree with the open session it belongs to.</summary>
    private (OpenSession Session, TreeNode Node, TreeObjectTag? Tag)? TreeTarget()
    {
        if (_tree.Tree.FocusedNode is not { Session.Tag: Guid id } node || Sessions?.Connector.Find(id) is not { } open || open.Host.ServerServices is null)
        {
            return null;
        }
        return (open, node, node.Tag as TreeObjectTag);
    }

    /// <summary>
    /// Line with the session and the server in the questions of destructive actions (phase 14): someone with several sessions
    /// open sees which server they are about to delete on before confirming.
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

    /// <summary>Target objects: the Database tab list selection, if it has the focus; otherwise the object focused in the tree.</summary>
    private (OpenSession Session, string Database, IReadOnlyList<DbObjectRef> Objects)? ObjectsTarget()
    {
        if (ListTarget() is { } list)
        {
            return list;
        }
        return TreeTarget() is ({ } open, _, { Object: { } obj } tag) ? (open, tag.Database, [obj]) : null;
    }

    /// <summary>Focused object (Database tab list with focus, or the tree).</summary>
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
    /// Before a Drop: the pending navigation to the focused object (waiting for the <c>USE</c>), the definition load in the Table
    /// tab and the Data tab load of an object about to disappear used to end in a "doesn't exist" error dialog after the
    /// Drop (on a slow server, or with the Drop right after choosing the object).
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
            // The sizes changed: reads the database again (names and statistics).
            await ReloadDatabaseObjectsAsync(open, database);
        }
    }

    /// <summary>"Select top N rows": a new query tab with the SELECT, and runs it (appendix C §3.3).</summary>
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
    /// Text a node inserts in the editor (double click or drag, appendix D A12): database, object or column with minimal quoting;
    /// with Shift, qualified with the database (and, for a column, with the table too).
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

    /// <summary>Language of a tree node's session (the default one if the session already closed).</summary>
    private ISqlLanguage LanguageOf(TreeNode node) =>
        node.Session?.Tag is Guid id && Sessions?.Connector.Find(id) is { } open ? open.Language : SqlLanguages.Default;

    /// <summary>Drop on the editor: only accepts tree nodes.</summary>
    private string? TreeDropText(IDataTransfer data, KeyModifiers modifiers) =>
        data.TryGetValue(DatabaseTreeControl.NodeFormat) is { } node ? NodeInsertText(node, modifiers.HasFlag(KeyModifiers.Shift)) : null;
}
