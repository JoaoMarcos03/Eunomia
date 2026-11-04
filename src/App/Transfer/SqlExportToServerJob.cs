using Eunomia.Engine.Execution;

namespace Eunomia.App.Transfer;

/// <summary>
/// End of an export to a database: the export, and the run on the target (<c>null</c> when it did not start: the export
/// was cancelled or had objects with errors, see <see cref="NotImported"/>).
/// </summary>
public sealed record SqlExportToServerResult(SqlExportResult Export, SqlFileResult? Import, string? NotImported)
{
    /// <summary>Everything was exported and every statement ran on the target.</summary>
    public bool Completed => !Export.Cancelled && Import is { Completed: true, Errors: 0 };
}

/// <summary>
/// Export database as SQL with the output "Database" or "Server: &lt;session&gt;" (appendix B §2.3.1, §2.3.5): the dump
/// of the source session is written to a temporary <c>.sql</c> file in the app's spill directory (streaming, bounded
/// memory, the same code as a file export) and then run on the target session with <see cref="SqlFileJob"/> (the same
/// code as Run SQL file), stopping at the first error. The file is deleted at the end, also after a cancel or an error.
/// <para>
/// Safety (AGENTS.md section 5): the target can never be one of the exported databases on the same server, or the
/// "Delete + insert" and "Drop" options would destroy the source (<see cref="Validate"/>); an export with errors is not
/// run on the target, so a half-read table is never written over the target one. With a target database every object
/// goes there (no <c>CREATE DATABASE</c> or <c>USE</c> in the dump); without one, each source database is used by name
/// on the target server.
/// </para>
/// </summary>
public sealed class SqlExportToServerJob
{
    private readonly SessionHost _source;
    private readonly SessionHost _target;
    private readonly SqlExportRequest _request;
    private readonly string _workDirectory;

    /// <param name="workDirectory">Where the temporary dump goes (the spill directory: user-only permissions).</param>
    public SqlExportToServerJob(SessionHost source, SessionHost target, SqlExportRequest request, string workDirectory)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(workDirectory);
        if (request.Target is not (SqlExportTarget.Database or SqlExportTarget.Server))
        {
            throw new ArgumentException("Only exports to a database or a server run here.", nameof(request));
        }
        _source = source;
        _target = target;
        _request = request;
        _workDirectory = workDirectory;
    }

    /// <summary>
    /// Why the export cannot run, or <c>null</c>. <paramref name="sameServer"/>: the target session reaches the same
    /// server as the source one (always true for <see cref="SqlExportTarget.Database"/>).
    /// </summary>
    public static string? Validate(SqlExportRequest request, bool sameServer)
    {
        ArgumentNullException.ThrowIfNull(request);
        var target = request.TargetDatabase;
        if (request.Target == SqlExportTarget.Database && string.IsNullOrEmpty(target))
        {
            return "Please select a target database.";
        }
        if (request.Target == SqlExportTarget.Server && request.TargetSession is null)
        {
            return "Please select a target server.";
        }
        if (!sameServer && request.Target == SqlExportTarget.Server)
        {
            return null;
        }
        if (string.IsNullOrEmpty(target))
        {
            return "The target is the same server as the source: please select a target database other than the exported ones.";
        }
        if (request.Databases.FirstOrDefault(d => string.Equals(d.Name, target, StringComparison.OrdinalIgnoreCase)) is { } source)
        {
            return $"The target database \"{source.Name}\" is one of the exported databases. Please select another one.";
        }
        return null;
    }

    /// <summary>
    /// The two sessions reach the same server instance (the same session, or the same identity from
    /// <c>IDialect.ServerInstanceQuery</c>). When that cannot be told, they count as the same server: <see cref="Validate"/>
    /// then refuses the source databases as targets.
    /// </summary>
    public static async Task<bool> SameServerAsync(SessionHost source, SessionHost target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (ReferenceEquals(source, target))
        {
            return true;
        }
        var a = await InstanceAsync(source, cancellationToken).ConfigureAwait(false);
        var b = await InstanceAsync(target, cancellationToken).ConfigureAwait(false);
        return a is null || b is null || string.Equals(a, b, StringComparison.Ordinal);

        static async Task<string?> InstanceAsync(SessionHost host, CancellationToken ct)
        {
            if (host.ServerServices?.Dialect.ServerInstanceQuery is not { } query)
            {
                return null;
            }
            var value = await host.RunMetadataAsync((h, c) => h.ExecuteScalarAsync(query, c), ct).ConfigureAwait(false);
            return value is null or DBNull ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Temporary dumps left by a crash: only old ones, another instance may be exporting right now.</summary>
    private static void CleanOrphans(string directory, DateTime olderThanUtc)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "export-*.sql*"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < olderThanUtc)
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use: stays.
            }
        }
    }

    public async Task<SqlExportToServerResult> RunAsync(
        IProgress<SqlExportProgress>? exportProgress,
        IProgress<SqlFileProgress>? importProgress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_workDirectory);
        CleanOrphans(_workDirectory, DateTime.UtcNow - TimeSpan.FromDays(1));
        var path = Path.Combine(_workDirectory, "export-" + Guid.NewGuid().ToString("N") + ".sql");
        try
        {
            var toDatabase = !string.IsNullOrEmpty(_request.TargetDatabase);
            var dump = _request.Dump with
            {
                CreateDatabases = !toDatabase && _request.Dump.CreateDatabases,
                DropDatabases = !toDatabase && _request.Dump.DropDatabases,
                UseDatabases = !toDatabase,
            };
            var export = await new SqlExportJob(_source, _request with { Target = SqlExportTarget.File, Path = path, Dump = dump })
                .RunAsync(exportProgress, cancellationToken).ConfigureAwait(false);
            if (export.Cancelled)
            {
                return new SqlExportToServerResult(export, null, "Cancelled before anything was written to the target.");
            }
            var failed = export.Items.Count(i => i.State == SqlExportItemState.Failed);
            if (failed > 0)
            {
                return new SqlExportToServerResult(export, null, $"{failed} object(s) could not be exported: nothing was written to the target.");
            }
            var import = await new SqlFileJob(_target, new SqlFileRequest
            {
                Path = path,
                Encoding = "UTF-8",
                Database = toDatabase ? _request.TargetDatabase : null,
                StopOnErrors = true,
            }).RunAsync(importProgress, cancellationToken).ConfigureAwait(false);
            return new SqlExportToServerResult(export, import, null);
        }
        finally
        {
            foreach (var file in new[] { path, path + ".partial" })
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Still open somewhere: CleanOrphans takes it on a later export.
                }
            }
        }
    }
}
