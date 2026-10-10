using Eunomia.App.Objects;
using Eunomia.Drivers.Abstractions;
using Eunomia.UI.Features.Transfer;

namespace Eunomia.UI.Shell;

/// <summary>
/// Create new ▸ Table copy (popupDB; anexo B §5.1): o diálogo com a tabela ou view focada; depois do OK, a pergunta das
/// sessões que a pedem (e a recusa das read-only), o aviso do commit implícito e o CREATE e o INSERT … SELECT na ligação
/// primária (<see cref="ObjectActions.RunAsync"/>, que pára no primeiro erro). No fim, a árvore mostra a tabela nova.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Diálogo aberto (testes).</summary>
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
            // O CREATE pode ter corrido antes de o INSERT falhar: a árvore mostra o que ficou.
            await ReloadDatabaseObjectsAsync(open, request.TargetDatabase);
        }
        if (created)
        {
            await FocusTreeObjectAsync(open.Id, new DbObjectRef(request.TargetDatabase, request.TargetDatabase, request.TargetName.Trim(), DbObjectKind.Table));
        }
    }
}
