using Eunomia.App.Objects;
using Eunomia.Drivers.Abstractions;
using Eunomia.UI.Features.Transfer;

namespace Eunomia.UI.Shell;

/// <summary>
/// Create new ▸ Table copy (popupDB; appendix B §5.1): the dialog with the focused table or view; after OK, the question for
/// sessions that ask for it (and the refusal in read-only ones), the implicit commit warning and the CREATE and the
/// INSERT … SELECT on the primary connection (<see cref="ObjectActions.RunAsync"/>, which stops at the first error). At the
/// end, the tree shows the new table.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Open dialog (tests).</summary>
    internal CopyTableDialog? CopyTableWindow { get; private set; }

    private void InitCopyTable()
    {
        _context.Commands.SetHandler("object.copyTable", _ => new ValueTask(CopyTableAsync()), () => FocusedObject() is { } o && IsTableOrView(o.Object));
    }

    private async Task CopyTableAsync()
    {
        if (FocusedObject() is not ({ Host.ServerServices: { } services } open, { } obj))
        {
            return;
        }
        TableDefinition source;
        IReadOnlyList<string> databases;
        try
        {
            source = await open.Metadata.GetTableAsync(obj, CancellationToken.None);
            databases = await open.Metadata.GetDatabasesAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is DbOperationException or Engine.Execution.SessionOperationException)
        {
            ShowError(ex);
            return;
        }
        var rows = open.Metadata.CachedObjects(obj.Schema)?.Objects.FirstOrDefault(o => o.Ref == obj)?.RowsEstimate;
        var dialog = new CopyTableDialog(new CopyTableServices(source, rows, databases, async (db, name, ct) =>
            (await open.Metadata.GetObjectsAsync(db, ct)).Objects.Any(o => string.Equals(o.Ref.Name, name, StringComparison.OrdinalIgnoreCase))));
        CopyTableWindow = dialog;
        TableCopyRequest? request;
        try
        {
            request = await dialog.ShowDialog<TableCopyRequest?>(this);
        }
        finally
        {
            CopyTableWindow = null;
        }
        if (request is null
            || !await ConfirmWritesAsync(open, $"Copy \"{obj.Name}\" to {request.TargetDatabase}.{request.TargetName}?")
            || !await ConfirmImplicitCommitAsync(open))
        {
            return;
        }
        var created = false;
        try
        {
            await ObjectActions.RunAsync(open.Host, TableCopy.Statements(services, source, request), CancellationToken.None);
            created = true;
        }
        catch (Exception ex) when (ex is DbOperationException or Engine.Execution.SessionOperationException or InvalidOperationException)
        {
            ShowError(ex);
        }
        finally
        {
            // The CREATE may have run before the INSERT failed: the tree shows what is left.
            await ReloadDatabaseObjectsAsync(open, request.TargetDatabase);
        }
        if (created)
        {
            await FocusTreeObjectAsync(open.Id, new DbObjectRef(request.TargetDatabase, request.TargetDatabase, request.TargetName.Trim(), DbObjectKind.Table));
        }
    }
}
