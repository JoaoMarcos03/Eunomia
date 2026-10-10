using Avalonia.Input.Platform;
using Eunomia.App.Sessions;
using Eunomia.App.Transfer;
using Eunomia.Engine.Execution;
using Eunomia.Presentation.Logging;
using Eunomia.UI.Features.Transfer;

namespace Eunomia.UI.Shell;

/// <summary>
/// Tools › Export database as SQL (phase 8; FAM-E06): the dialog with the database or the object focused in the tree already
/// checked, and the job on the session's job connections. The clipboard gets the text at the end; the SQL log records the result.
/// </summary>
public sealed partial class MainWindow
{
    private void InitSqlExport() =>
        _context.Commands.SetHandler("export.databaseAsSql", _ => new ValueTask(ExportDatabaseAsSqlAsync()), () => ActiveSession is { Host.State: SessionState.Ready });

    private async Task ExportDatabaseAsSqlAsync()
    {
        if (ActiveSession is not { Host.State: SessionState.Ready } open)
        {
            return;
        }
        IReadOnlyList<string> databases;
        try
        {
            databases = await open.Metadata.GetDatabasesAsync(CancellationToken.None);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _context.Log.Append(LogCategory.Error, e.Message);
            return;
        }
        var focused = _tree.Tree.FocusedNode;
        var database = focused?.Database?.Name;
        IReadOnlyCollection<string>? objects = focused is { IsObject: true } ? [focused.Name] : null;
        await SqlExportDialog.ShowAsync(
            this,
            _context.Settings.SqlExport,
            databases,
            async (name, ct) => (await open.Metadata.GetObjectsAsync(name, ct, forceStatistics: true)).Objects,
            (request, progress, ct) => RunSqlExportAsync(open, request, progress, ct),
            database,
            objects);
        _context.SaveSettings();
    }

    private async Task<SqlExportResult> RunSqlExportAsync(OpenSession open, SqlExportRequest request, IProgress<SqlExportProgress> progress, CancellationToken cancellationToken)
    {
        var job = new SqlExportJob(open.Host, request with { Host = open.Definition.Host, AppVersion = ShellContext.Version });
        // Off the UI thread: writing the file is synchronous inside the job.
        var result = await Task.Run(() => job.RunAsync(progress, cancellationToken), CancellationToken.None);
        if (result.ClipboardText is { } text && Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
        var where = request.Target == SqlExportTarget.Clipboard ? "clipboard" : request.Path;
        _context.Diagnostics.Write(Eunomia.App.Diagnostics.DiagnosticLevel.Info, "export",
            $"SQL export ({request.Target}) to {where}: {result.Items.Count} object(s), {result.BytesWritten} bytes, cancelled: {result.Cancelled}");
        var errors = result.Items.Count(i => i.State == SqlExportItemState.Failed);
        _context.Log.Append(
            result.Cancelled || errors > 0 ? LogCategory.Error : LogCategory.Info,
            string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"SQL export to {where} {(result.Cancelled ? "cancelled" : "finished")}: {result.Items.Count} object(s), {result.BytesWritten} bytes, {result.Elapsed.TotalSeconds:0.0} s{(errors > 0 ? $", {errors} error(s)" : string.Empty)}."));
        return result;
    }
}
