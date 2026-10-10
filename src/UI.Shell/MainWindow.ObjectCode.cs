using Eunomia.App.Objects;
using Eunomia.App.Sessions;
using Eunomia.Drivers.Abstractions;
using Eunomia.Presentation.Logging;
using Eunomia.UI.Features.TableEditor;
using Eunomia.UI.Shell.Sessions;

namespace Eunomia.UI.Shell;

/// <summary>
/// Views, procedures, functions, triggers and events in the Table tab, edited through their <c>CREATE</c> (phase 13): opening
/// the object shows the server's code; Save drops and creates it again (with the old one back if the new one fails); "Create
/// new ▸" opens a template. After saving, the tree reloads the database and focuses the object.
/// </summary>
public sealed partial class MainWindow
{
    private OpenSession? _codeEditorSession;
    private string? _codeEditorDatabase;
    private DbObjectRef? _codeEditorObject;
    private string? _codeEditorOriginal;
    private string _codeEditorInitial = string.Empty;

    /// <summary>Code editor open in the Table tab (tests).</summary>
    internal ObjectCodeEditorView? CodeEditor => _tabs.TableTab.Content as ObjectCodeEditorView;

    private void InitObjectCode()
    {
        var c = _context.Commands;
        foreach (var (id, kind) in new[]
                 {
                     ("object.createView", DbObjectKind.View), ("object.createProcedure", DbObjectKind.Procedure),
                     ("object.createFunction", DbObjectKind.Function), ("object.createTrigger", DbObjectKind.Trigger),
                     ("object.createEvent", DbObjectKind.Event),
                 })
        {
            c.SetHandler(id, _ => new ValueTask(CreateCodeObjectAsync(kind)), () => DatabaseOfFocus() is { Session.Host.ServerServices: not null });
        }
    }

    private static (string Caption, string Icon) CodeCaption(DbObjectKind kind) => kind switch
    {
        DbObjectKind.View => ("View", "view"),
        DbObjectKind.Procedure => ("Procedure", "procedure"),
        DbObjectKind.Function => ("Function", "function"),
        DbObjectKind.Trigger => ("Trigger", "trigger"),
        _ => ("Event", "event"),
    };

    /// <summary>Create new ▸ View/Stored procedure/…: the template in the Table tab (a trigger uses the focused table).</summary>
    private async Task CreateCodeObjectAsync(DbObjectKind kind)
    {
        if (DatabaseOfFocus() is not { } target || target.Session.Host.ServerServices is not { } services)
        {
            return;
        }
        if (HasUnsavedObjectEditor && !await ConfirmLeaveObjectEditorAsync())
        {
            return;
        }
        var table = kind == DbObjectKind.Trigger && _tree.Tree.FocusedNode?.Tag is TreeObjectTag { Object: { Kind: DbObjectKind.Table } focused } ? focused.Name : null;
        ShowCodeEditor(target.Session, target.Database, null, null, ObjectCode.Template(kind, services.Dialect, table), kind);
        _tabs.TableTab.IsVisible = true;
        _tabs.SelectedItem = _tabs.TableTab;
    }

    /// <summary>Opens an existing object with the server's code.</summary>
    private async Task OpenObjectCodeEditorAsync(OpenSession session, string database, DbObjectRef obj, CancellationToken cancellationToken)
    {
        try
        {
            var code = await session.Host.RunMetadataAsync((h, ct) => h.Metadata.GetCreateCodeAsync(obj, ct), cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
            {
                ShowCodeEditor(session, database, obj, code, code, obj.Kind);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _context.Log.Append(LogCategory.Error, ex.Message);
        }
    }

    private void ShowCodeEditor(OpenSession session, string database, DbObjectRef? obj, string? original, string code, DbObjectKind kind)
    {
        var view = new ObjectCodeEditorView(kind, code, obj is null);
        view.SaveRequested += (_, _) => _ = SaveObjectCodeAsync(view);
        view.DiscardRequested += (_, _) =>
        {
            view.Editor.Text = _codeEditorInitial;
            view.MarkSaved();
        };
        _codeEditorSession = session;
        _codeEditorDatabase = database;
        _codeEditorObject = obj;
        _codeEditorOriginal = original;
        _codeEditorInitial = code;
        _tabs.TableTab.Content = view;
        var (caption, icon) = CodeCaption(kind);
        Views.MainTabsView.SetFixedHeader(_tabs.TableTab, caption + ": " + (obj?.Name ?? "[Untitled]"), icon);
    }

    /// <summary>Save: checks it is a CREATE, saves, reloads the database in the tree and focuses the saved object.</summary>
    internal async Task<bool> SaveObjectCodeAsync(ObjectCodeEditorView view)
    {
        if (_codeEditorSession is not { } session || _codeEditorDatabase is not { } database)
        {
            return false;
        }
        if (ObjectCode.Parse(view.Code) is not { } created)
        {
            ShowEditError("The code must be a CREATE VIEW, PROCEDURE, FUNCTION, TRIGGER or EVENT statement.");
            return false;
        }
        if (!await ConfirmImplicitCommitAsync(session))
        {
            return false;
        }
        _context.Status.Busy = "Saving " + CodeCaption(created.Kind).Caption.ToLowerInvariant() + " ...";
        try
        {
            await ObjectCode.SaveAsync(session, database, _codeEditorObject, _codeEditorOriginal, view.Code, CancellationToken.None, _context.RecoveryDirectory);
        }
        catch (Exception ex) when (ex is DbOperationException or Engine.Execution.SessionOperationException or InvalidOperationException)
        {
            ShowEditError(ex.Message);
            return false;
        }
        finally
        {
            _context.Status.SetIdle();
        }
        var saved = new DbObjectRef(database, database, created.Name, created.Kind);
        view.MarkSaved();
        _codeEditorObject = saved;
        _codeEditorOriginal = view.Code;
        _codeEditorInitial = view.Code;
        _context.Log.Append(LogCategory.Info, $"{CodeCaption(created.Kind).Caption} \"{created.Name}\" saved.");
        session.Metadata.Invalidate(database);
        await ReloadDatabaseObjectsAsync(session, database);
        await FocusTreeObjectAsync(session.Id, saved);
        return true;
    }
}
