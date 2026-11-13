using Avalonia.Input.Platform;
using Eunomia.App.Sessions;
using Eunomia.App.Transfer;
using Eunomia.Engine.Execution;
using Eunomia.Presentation.Logging;
using Eunomia.UI.Features.Transfer;

namespace Eunomia.UI.Shell;

/// <summary>
/// Tools › Export database as SQL (phase 8; FAM-E06): the dialog with the database or the object focused in the tree already
/// checked and the tree filters applied, and the job on the session's job connections. The clipboard gets the text
/// at the end; the SQL log records the result. The "Database" and "Server: &lt;session&gt;" outputs run the dump on another
/// database or saved session
/// (<see cref="SqlExportToServerJob"/>); a server that is not open connects when it is chosen.
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
        // As in HeidiSQL: with the Database tab list focused, the objects selected in it come checked.
        if (ListTarget() is { } list && list.Session == open)
        {
            database = list.Database;
            objects = [.. list.Objects.Select(o => o.Name)];
        }
        await SqlExportDialog.ShowAsync(
            this,
            _context.Settings.SqlExport,
            databases,
            async (name, ct) => (await open.Metadata.GetObjectsAsync(name, ct, forceStatistics: true)).Objects,
            (request, progress, ct) => RunSqlExportAsync(open, request, progress, ct),
            database,
            objects,
            ExportTargets(open),
            new SqlExportTreeFilters(_tree.DatabaseFilterBox.Text ?? string.Empty, _tree.TableFilterBox.Text ?? string.Empty));
        _context.SaveSettings();
    }

    /// <summary>The other saved sessions, their databases, the checks and the runner of the database outputs.</summary>
    private SqlExportTargets? ExportTargets(OpenSession open)
    {
        if (Sessions is not { } sessions)
        {
            return null;
        }
        var servers = sessions.Catalog.Sessions
            .Where(s => s.Id != open.Definition.Id)
            .OrderBy(s => s.Path, StringComparer.OrdinalIgnoreCase)
            .Select(s => new SqlExportServer(s.Id, s.Path))
            .ToList();
        var sameServer = new Dictionary<Guid, bool>();
        return new SqlExportTargets(
            servers,
            async (id, ct) =>
            {
                var target = id is { } session ? await TargetSessionAsync(session) : open;
                if (target is null)
                {
                    throw new InvalidOperationException("Not connected.");
                }
                if (id is { } targetId)
                {
                    sameServer[targetId] = await SqlExportToServerJob.SameServerAsync(open.Host, target.Host, ct);
                }
                return await target.Metadata.GetDatabasesAsync(ct);
            },
            request =>
            {
                var target = request.TargetSession is { } id ? sessions.Connector.Find(id) : open;
                if (target is null)
                {
                    return "The target session is not connected.";
                }
                if (target.Definition.ReadOnly)
                {
                    return $"Session \"{target.Name}\" is read-only.";
                }
                var same = request.TargetSession is not { } targetId || !sameServer.TryGetValue(targetId, out var known) || known;
                return SqlExportToServerJob.Validate(request, same);
            },
            (request, exportProgress, importProgress, ct) => RunSqlExportToServerAsync(open, request, exportProgress, importProgress, ct));

        async Task<OpenSession?> TargetSessionAsync(Guid id)
        {
            if (sessions.Connector.Find(id) is { } already)
            {
                return already;
            }
            if (sessions.Catalog.Find(id) is not { } definition)
            {
                return null;
            }
            var owner = OwnedWindows.OfType<SqlExportDialog>().FirstOrDefault() as Avalonia.Controls.Window ?? this;
            return await ConnectAsync(owner, definition, null, showHostTab: false) ? sessions.Connector.Find(id) : null;
        }
    }

    private async Task<SqlExportToServerResult> RunSqlExportToServerAsync(
        OpenSession open, SqlExportRequest request, IProgress<SqlExportProgress> exportProgress, IProgress<SqlFileProgress> importProgress, CancellationToken cancellationToken)
    {
        var target = request.TargetSession is { } id ? Sessions?.Connector.Find(id) ?? throw new InvalidOperationException("The target session is not connected.") : open;
        var directory = _context.SpillDirectory ?? Path.Combine(Path.GetTempPath(), "Eunomia");
        var job = new SqlExportToServerJob(open.Host, target.Host, request with { Host = open.Definition.Host, AppVersion = ShellContext.Version }, directory);
        var result = await Task.Run(() => job.RunAsync(exportProgress, importProgress, cancellationToken), CancellationToken.None);
        var where = (request.TargetDatabase is { } db ? $"database {db} of " : "databases with the same names on ") + target.Name;
        var summary = result.Import is { } import
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"SQL export to {where} {(import.Completed ? "finished" : import.Cancelled ? "cancelled" : "stopped at an error")}: {result.Export.Items.Count} object(s), {import.Statements} queries, {(result.Export.Elapsed + import.Elapsed).TotalSeconds:0.0} s.")
            : $"SQL export to {where}: {result.NotImported}";
        _context.Log.Append(result.Completed ? LogCategory.Info : LogCategory.Error, summary);
        if (result.Import is { Statements: > 0 })
        {
            foreach (var name in request.TargetDatabase is { } single ? [single] : request.Databases.Select(d => d.Name))
            {
                await ReloadDatabaseObjectsAsync(target, name);
            }
        }
        return result;
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
