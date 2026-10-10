using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Threading.Channels;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Execution;
using Eunomia.Engine.Execution.Editing;
using Eunomia.Engine.Results;
using Eunomia.Engine.Transfer;

namespace Eunomia.App.Transfer;

/// <summary>SQL export target in the MVP (another database and another server come later, 27.2).</summary>
public enum SqlExportTarget
{
    File,
    Zip,
    Directory,
    Clipboard,
}

/// <summary>A database and the objects checked in the dialog's tree, in tree order (natural, by name).</summary>
public sealed record SqlExportDatabase(string Name, IReadOnlyList<DbObjectInfo> Objects);

public sealed record SqlExportRequest
{
    public const long DefaultClipboardLimit = 64L * 1024 * 1024;

    public required SqlDumpOptions Dump { get; init; }

    public required IReadOnlyList<SqlExportDatabase> Databases { get; init; }

    public SqlExportTarget Target { get; init; } = SqlExportTarget.File;

    /// <summary>File (<c>.sql</c> or <c>.zip</c>) or directory.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>
    /// Consistent snapshot (<c>START TRANSACTION WITH CONSISTENT SNAPSHOT</c>, like <c>mysqldump
    /// --single-transaction</c>): everything on one connection. Without it, tables are exported in parallel (section 13.4).
    /// </summary>
    public bool ConsistentSnapshot { get; init; }

    /// <summary>The session's host for the header.</summary>
    public string Host { get; init; } = string.Empty;

    public string AppVersion { get; init; } = string.Empty;

    /// <summary>Maximum text size for the clipboard (section 13.2: bounded clipboard).</summary>
    public long ClipboardLimit { get; init; } = DefaultClipboardLimit;
}

public enum SqlExportItemState
{
    Waiting,
    Running,
    Done,
    Skipped,
    Failed,
    Cancelled,
}

/// <summary>Row of the progress grid (appendix B §2.3.6: Database, Table, Rows "N / P%", Duration).</summary>
public sealed record SqlExportItemProgress(
    string Database,
    string Name,
    DbObjectKind Kind,
    long Rows,
    long EstimatedRows,
    TimeSpan Duration,
    SqlExportItemState State,
    string? Error);

public sealed record SqlExportProgress(IReadOnlyList<SqlExportItemProgress> Items, long BytesWritten, TimeSpan Elapsed);

/// <summary>End of the export: the text for the clipboard, or the path written.</summary>
public sealed record SqlExportResult(bool Cancelled, long BytesWritten, IReadOnlyList<SqlExportItemProgress> Items, string? ClipboardText, TimeSpan Elapsed);

/// <summary>The text for the clipboard went over the configured limit.</summary>
public sealed class ClipboardLimitException(long limit)
    : Exception($"The export is larger than the clipboard limit ({limit / (1024 * 1024)} MiB). Export to a file instead.");

/// <summary>
/// SQL export job (FAM-E06, PR-13; sections 13.1–13.4; appendix B §2.3): the session's job connections (never the primary),
/// HeidiSQL's object order per database (tables, views as temporary tables, routines and events in tree order; then
/// triggers; then the final views), data through <c>SELECT … ORDER BY</c> key in streaming (no OFFSET), read and written
/// in parallel through a bounded channel. With a consistent snapshot it runs on one connection; without it, objects go
/// to segments in parallel and are joined in the right order. File and ZIP are written to a <c>.partial</c> that is
/// renamed at the end (deleted on cancel or error); the ZIP compresses while streaming; the directory has one
/// file per object in <c>&lt;dir&gt;\&lt;database&gt;\&lt;kind&gt;-&lt;name&gt;.sql</c>, each with a header and a footer.
/// </summary>
public sealed class SqlExportJob
{
    private const int BlockRows = 4096;
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(100);

    private readonly SessionHost _host;
    private readonly SqlExportRequest _request;
    private readonly List<Item> _items;
    private readonly Stopwatch _clock = new();
    private Utf8Output? _main;
    private long _directoryBytes;

    // Current database of each job connection: SHOW CREATE only writes names without the database when the connection is in it.
    private readonly ConcurrentDictionary<IDbConnectionHandle, string> _databases = new();

    public SqlExportJob(SessionHost host, SqlExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(request);
        _host = host;
        _request = request;
        _items = Plan(request);
    }

    /// <summary>Bytes written to the output (for a directory, the sum of the finished files).</summary>
    private long BytesWritten => _main?.BytesWritten ?? Interlocked.Read(ref _directoryBytes);

    private IDialect Dialect => (_host.ServerServices ?? throw new InvalidOperationException("The session is not connected.")).Dialect;

    private SqlDumpOptions Options => _request.Dump;

    /// <summary>Runs the export; a cancel returns <see cref="SqlExportResult.Cancelled"/> and deletes the <c>.partial</c>.</summary>
    public async Task<SqlExportResult> RunAsync(IProgress<SqlExportProgress>? progress, CancellationToken cancellationToken)
    {
        _clock.Restart();
        using var stopReports = new CancellationTokenSource();
        var reporter = progress is null ? Task.CompletedTask : ReportAsync(progress, stopReports.Token);
        string? text = null;
        var cancelled = false;
        try
        {
            if (_request.Target == SqlExportTarget.Directory)
            {
                await RunDirectoryAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                text = await RunSingleAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelled = true;
            foreach (var item in _items.Where(i => i.State is SqlExportItemState.Waiting or SqlExportItemState.Running))
            {
                item.State = SqlExportItemState.Cancelled;
            }
        }
        finally
        {
            await stopReports.CancelAsync().ConfigureAwait(false);
            await reporter.ConfigureAwait(false);
        }
        var result = new SqlExportResult(cancelled, BytesWritten, Snapshot(), text, _clock.Elapsed);
        progress?.Report(new SqlExportProgress(result.Items, result.BytesWritten, result.Elapsed));
        return result;
    }

    // ---------- Plan ----------

    private enum Pass
    {
        Database,
        Object,
        Trigger,
        FinalView,
    }

    private sealed class Item(string database, DbObjectInfo? info, Pass pass)
    {
        private long _rows;
        private long _started;
        private long _ended;

        public string Database { get; } = database;

        public DbObjectInfo? Info { get; } = info;

        public Pass Pass { get; } = pass;

        public bool HasData => Info?.Ref.Kind == DbObjectKind.Table;

        public volatile SqlExportItemState State;

        public string? Error { get; set; }

        public long Rows => Interlocked.Read(ref _rows);

        public void AddRows(int rows) => Interlocked.Add(ref _rows, rows);

        public void Start(Stopwatch clock)
        {
            Interlocked.Exchange(ref _started, clock.ElapsedTicks);
            State = SqlExportItemState.Running;
        }

        public void End(Stopwatch clock, SqlExportItemState state)
        {
            Interlocked.Exchange(ref _ended, clock.ElapsedTicks);
            State = state;
        }

        public TimeSpan Duration(Stopwatch clock)
        {
            var started = Interlocked.Read(ref _started);
            if (started == 0)
            {
                return TimeSpan.Zero;
            }
            var ended = Interlocked.Read(ref _ended);
            return Stopwatch.GetElapsedTime(started, ended == 0 ? clock.ElapsedTicks : ended);
        }

        // Segment written by a connection in parallel, joined to the output in plan order.
        public TaskCompletionSource<Segment?> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record Segment(string? FilePath, byte[]? Bytes);

    /// <summary>HeidiSQL's object order (appendix B §2.3.3).</summary>
    private static List<Item> Plan(SqlExportRequest request)
    {
        var items = new List<Item>();
        foreach (var database in request.Databases)
        {
            if (request.Target != SqlExportTarget.Directory)
            {
                items.Add(new Item(database.Name, null, Pass.Database));
            }
            foreach (var o in database.Objects.Where(o => o.Ref.Kind is DbObjectKind.Table or DbObjectKind.View or DbObjectKind.Procedure or DbObjectKind.Function or DbObjectKind.Event))
            {
                items.Add(new Item(database.Name, o, Pass.Object));
            }
            foreach (var o in database.Objects.Where(o => o.Ref.Kind == DbObjectKind.Trigger))
            {
                items.Add(new Item(database.Name, o, Pass.Trigger));
            }
            // In a directory the view goes in a single file, with both passes.
            if (request.Target != SqlExportTarget.Directory)
            {
                foreach (var o in database.Objects.Where(o => o.Ref.Kind == DbObjectKind.View))
                {
                    items.Add(new Item(database.Name, o, Pass.FinalView));
                }
            }
        }
        return items;
    }

    // ---------- File, ZIP and clipboard ----------

    private async Task<string?> RunSingleAsync(CancellationToken cancellationToken)
    {
        string? partial = null;
        FileStream? file = null;
        ZipArchive? zip = null;
        Stream sink;
        switch (_request.Target)
        {
            case SqlExportTarget.Clipboard:
                sink = new MemoryStream();
                break;
            default:
                partial = _request.Path + ".partial";
                file = OpenPartial(partial);
                if (_request.Target == SqlExportTarget.Zip)
                {
                    zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
                    sink = zip.CreateEntry(System.IO.Path.GetFileNameWithoutExtension(_request.Path) + ".sql", CompressionLevel.Optimal).Open();
                }
                else
                {
                    sink = file;
                }
                break;
        }

        var completed = false;
        try
        {
            using (var output = new Utf8Output(sink, leaveOpen: true))
            {
                _main = output;
                var writer = new SqlDumpWriter(output, Options);
                writer.WriteHeader(await HeaderAsync(cancellationToken).ConfigureAwait(false));
                var parallel = Math.Min(_host.Settings.MaxJobConnections, _items.Count);
                if (_request.ConsistentSnapshot || _request.Target == SqlExportTarget.Clipboard || parallel <= 1)
                {
                    await _host.RunJobAsync(
                        async (handle, ct) =>
                        {
                            await SetupAsync(handle, _request.ConsistentSnapshot, ct).ConfigureAwait(false);
                            try
                            {
                                foreach (var item in _items)
                                {
                                    await ExportItemAsync(handle, item, writer, output, ct).ConfigureAwait(false);
                                }
                            }
                            finally
                            {
                                await RestoreAsync(handle, _request.ConsistentSnapshot).ConfigureAwait(false);
                            }
                            return true;
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await RunParallelAsync(output, parallel, cancellationToken).ConfigureAwait(false);
                }
                writer.WriteFooter();
            }
            if (sink is MemoryStream memory)
            {
                return System.Text.Encoding.UTF8.GetString(memory.GetBuffer(), 0, (int)memory.Length);
            }
            completed = true;
            return null;
        }
        finally
        {
            // The ZIP entry closes before the archive, and the archive before the file.
            if (!ReferenceEquals(sink, file))
            {
                await sink.DisposeAsync().ConfigureAwait(false);
            }
            zip?.Dispose();
            if (file is not null)
            {
                await file.DisposeAsync().ConfigureAwait(false);
            }
            if (partial is not null)
            {
                if (completed)
                {
                    File.Move(partial, _request.Path, overwrite: true);
                }
                else
                {
                    TryDelete(partial);
                }
            }
        }
    }

    /// <summary>
    /// Without a consistent snapshot: <paramref name="connections"/> job connections take objects from the queue in plan
    /// order and write each into a segment; the output joins the segments in the same order, as they finish.
    /// </summary>
    private async Task RunParallelAsync(Utf8Output output, int connections, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var next = -1;
        var workers = Enumerable.Range(0, connections).Select(_ => _host.RunJobAsync(
            async (handle, ct) =>
            {
                await SetupAsync(handle, snapshot: false, ct).ConfigureAwait(false);
                try
                {
                    int index;
                    while ((index = Interlocked.Increment(ref next)) < _items.Count)
                    {
                        var item = _items[index];
                        try
                        {
                            item.Done.TrySetResult(await WriteSegmentAsync(handle, item, index, ct).ConfigureAwait(false));
                        }
                        catch (Exception e)
                        {
                            item.Done.TrySetException(e);
                            throw;
                        }
                    }
                }
                finally
                {
                    await RestoreAsync(handle, snapshot: false).ConfigureAwait(false);
                }
                return true;
            },
            linked.Token)).ToList();

        try
        {
            foreach (var item in _items)
            {
                var segment = await item.Done.Task.WaitAsync(linked.Token).ConfigureAwait(false);
                if (segment?.Bytes is { } bytes)
                {
                    output.Write(bytes);
                }
                else if (segment?.FilePath is { } path)
                {
                    await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
                    {
                        var buffer = new byte[Utf8Output.DefaultBufferSize];
                        int read;
                        while ((read = await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false)) > 0)
                        {
                            output.Write(buffer.AsSpan(0, read));
                        }
                    }
                    TryDelete(path);
                }
            }
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch
        {
            await linked.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(workers).ConfigureAwait(false);
            }
            catch (Exception) when (linked.IsCancellationRequested)
            {
                // The other workers stop with the cancel; the first error is the one that counts.
            }
            foreach (var item in _items)
            {
                if (item.Done.Task.IsCompletedSuccessfully && item.Done.Task.Result?.FilePath is { } path)
                {
                    TryDelete(path);
                }
            }
            throw;
        }
    }

    /// <summary>An object in a segment: tables with data in a temporary file, the rest in memory.</summary>
    private async Task<Segment> WriteSegmentAsync(IDbConnectionHandle handle, Item item, int index, CancellationToken cancellationToken)
    {
        if (item.HasData && Options.Data != DumpDataMode.None)
        {
            var path = _request.Path + ".partial." + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            try
            {
                using (var output = new Utf8Output(OpenPartial(path)))
                {
                    await ExportItemAsync(handle, item, new SqlDumpWriter(output, Options), output, cancellationToken).ConfigureAwait(false);
                }
                return new Segment(path, null);
            }
            catch
            {
                TryDelete(path);
                throw;
            }
        }
        using var memory = new MemoryStream();
        using (var output = new Utf8Output(memory, 64 * 1024, leaveOpen: true))
        {
            await ExportItemAsync(handle, item, new SqlDumpWriter(output, Options), output, cancellationToken).ConfigureAwait(false);
        }
        return new Segment(null, memory.ToArray());
    }

    // ---------- Directory ----------

    private async Task RunDirectoryAsync(CancellationToken cancellationToken)
    {
        var header = await HeaderAsync(cancellationToken).ConfigureAwait(false);
        var connections = _request.ConsistentSnapshot ? 1 : Math.Max(1, Math.Min(_host.Settings.MaxJobConnections, _items.Count));
        var next = -1;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var workers = Enumerable.Range(0, connections).Select(_ => _host.RunJobAsync(
            async (handle, ct) =>
            {
                await SetupAsync(handle, _request.ConsistentSnapshot, ct).ConfigureAwait(false);
                try
                {
                    int index;
                    while ((index = Interlocked.Increment(ref next)) < _items.Count)
                    {
                        var item = _items[index];
                        var directory = System.IO.Path.Combine(_request.Path, SafeFileName(item.Database));
                        Directory.CreateDirectory(directory);
                        var path = System.IO.Path.Combine(directory, SafeFileName(TypeName(item.Info!.Ref.Kind) + "-" + item.Info.Ref.Name) + ".sql");
                        var partial = path + ".partial";
                        try
                        {
                            using (var output = new Utf8Output(OpenPartial(partial)))
                            {
                                var writer = new SqlDumpWriter(output, Options);
                                writer.WriteHeader(header);
                                await ExportItemAsync(handle, item, writer, output, ct).ConfigureAwait(false);
                                if (item.Info.Ref.Kind == DbObjectKind.View)
                                {
                                    await ExportItemAsync(handle, new Item(item.Database, item.Info, Pass.FinalView), writer, output, ct).ConfigureAwait(false);
                                }
                                writer.WriteFooter();
                                Interlocked.Add(ref _directoryBytes, output.BytesWritten);
                            }
                            File.Move(partial, path, overwrite: true);
                        }
                        catch
                        {
                            TryDelete(partial);
                            throw;
                        }
                    }
                }
                finally
                {
                    await RestoreAsync(handle, _request.ConsistentSnapshot).ConfigureAwait(false);
                }
                return true;
            },
            linked.Token)).ToList();
        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch
        {
            await linked.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(workers).ConfigureAwait(false);
            }
            catch (Exception) when (linked.IsCancellationRequested)
            {
                // The other workers stop with the cancel; the first error is the one that counts.
            }
            throw;
        }
    }

    // ---------- One object ----------

    private async Task ExportItemAsync(IDbConnectionHandle handle, Item item, SqlDumpWriter writer, Utf8Output output, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var metadata = handle.Metadata;
        if (!_databases.TryGetValue(handle, out var current) || current != item.Database)
        {
            await handle.ExecuteNonQueryAsync(Dialect.ChangeDatabase(item.Database), cancellationToken).ConfigureAwait(false);
            _databases[handle] = item.Database;
        }
        if (item.Pass == Pass.Database)
        {
            if (Options.CreateDatabases || Options.DropDatabases)
            {
                writer.WriteDatabase(item.Database, await metadata.GetCreateDatabaseAsync(item.Database, cancellationToken).ConfigureAwait(false));
            }
            return;
        }
        var info = item.Info!;
        var name = info.Ref.Name;
        if (item.Pass != Pass.FinalView)
        {
            item.Start(_clock);
        }
        try
        {
            switch (item.Pass, info.Ref.Kind)
            {
                case (Pass.Object, DbObjectKind.Table):
                    if (Options.CreateTables || Options.DropTables)
                    {
                        writer.WriteTable(item.Database, name, await metadata.GetCreateCodeAsync(info.Ref, cancellationToken).ConfigureAwait(false));
                    }
                    await ExportDataAsync(handle, item, writer, output, cancellationToken).ConfigureAwait(false);
                    break;
                case (Pass.Object, DbObjectKind.View):
                    if (Options.CreateTables || Options.DropTables)
                    {
                        var view = await metadata.GetTableAsync(info.Ref, cancellationToken).ConfigureAwait(false);
                        writer.WriteViewPlaceholder(item.Database, name, [.. view.Columns.Select(c => new DumpColumn(c.Name, c.NativeType, c.IsNullable, c.Type.Collation))]);
                    }
                    break;
                case (Pass.FinalView, _):
                    if (Options.CreateTables || Options.DropTables)
                    {
                        writer.WriteView(item.Database, name, await metadata.GetCreateCodeAsync(info.Ref, cancellationToken).ConfigureAwait(false));
                    }
                    return;
                case (Pass.Trigger, _):
                    if (Options.CreateTables || Options.DropTables)
                    {
                        var mode = await metadata.GetTriggerSqlModeAsync(info.Ref, cancellationToken).ConfigureAwait(false);
                        writer.WriteTrigger(item.Database, name, mode, await metadata.GetCreateCodeAsync(info.Ref, cancellationToken).ConfigureAwait(false));
                    }
                    break;
                default:
                    if (Options.CreateTables || Options.DropTables)
                    {
                        writer.WriteRoutine(item.Database, TypeName(info.Ref.Kind), name, await metadata.GetCreateCodeAsync(info.Ref, cancellationToken).ConfigureAwait(false));
                    }
                    break;
            }
            if (item.State == SqlExportItemState.Running)
            {
                item.End(_clock, SqlExportItemState.Done);
            }
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested && _host.StatementError(e) is { } error)
        {
            // Error reading the DDL or the data: it goes into the file as a comment and the export continues (appendix B §2.3.3).
            writer.EndData();
            writer.WriteError(error.Message);
            item.Error = error.Message;
            item.End(_clock, SqlExportItemState.Failed);
        }
        catch (OperationCanceledException)
        {
            item.End(_clock, SqlExportItemState.Cancelled);
            throw;
        }
    }

    private async Task ExportDataAsync(IDbConnectionHandle handle, Item item, SqlDumpWriter writer, Utf8Output output, CancellationToken cancellationToken)
    {
        var info = item.Info!;
        var estimated = info.RowsEstimate ?? 0;
        var approximate = string.Equals(info.Engine, "InnoDB", StringComparison.OrdinalIgnoreCase);
        if (Options.Data == DumpDataMode.None)
        {
            writer.BeginData(item.Database, info.Ref.Name, estimated, approximate, []);
            return;
        }
        if (info.Engine is { } engine && (engine.Equals("MRG_MYISAM", StringComparison.OrdinalIgnoreCase) || engine.Equals("FEDERATED", StringComparison.OrdinalIgnoreCase)))
        {
            writer.WriteDataSkipped(item.Database, info.Ref.Name, engine);
            item.End(_clock, SqlExportItemState.Skipped);
            return;
        }
        var table = await handle.Metadata.GetTableAsync(info.Ref, cancellationToken).ConfigureAwait(false);
        var columns = table.Columns.Where(c => !c.IsGenerated).ToList();
        var select = SelectFor(table, columns);
        writer.BeginData(item.Database, info.Ref.Name, estimated, approximate, [.. columns.Select(c => c.Name)]);

        // Reading and writing in parallel: the bounded channel makes the server wait when writing falls behind (section 13).
        var channel = Channel.CreateBounded<RowBlock>(new BoundedChannelOptions(4) { SingleReader = true, SingleWriter = true });
        var clipboardLimit = _request.Target == SqlExportTarget.Clipboard ? _request.ClipboardLimit : long.MaxValue;
        var consumer = Task.Run(
            async () =>
            {
                try
                {
                    await foreach (var block in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                    {
                        writer.WriteRows(block);
                        item.AddRows(block.RowCount);
                        if (output.BytesWritten > clipboardLimit)
                        {
                            throw new ClipboardLimitException(clipboardLimit);
                        }
                    }
                }
                catch (Exception e)
                {
                    channel.Writer.TryComplete(e);
                    throw;
                }
            },
            cancellationToken);
        try
        {
            var options = new ExecuteOptions(BlockSize: BlockRows, StopOnError: true, MaxResultSets: 1) { CollectWarnings = false };
            await foreach (var e in handle.Executor.ExecuteAsync(SqlBatch.Single(select), options, cancellationToken).ConfigureAwait(false))
            {
                switch (e)
                {
                    case RowBlockReceived received:
                        await channel.Writer.WriteAsync(received.Block, cancellationToken).ConfigureAwait(false);
                        break;
                    case ExecutionError error:
                        throw new DbOperationException(error.Error);
                }
            }
            channel.Writer.TryComplete();
            await consumer.ConfigureAwait(false);
        }
        catch (Exception e)
        {
            channel.Writer.TryComplete(e);
            Exception? consumerError = null;
            try
            {
                await consumer.ConfigureAwait(false);
            }
            catch (Exception c)
            {
                consumerError = c;
            }
            // The channel closed because the consumer failed (writing or the clipboard limit): its error is the one that counts.
            if (e is ChannelClosedException && consumerError is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(consumerError);
            }
            throw;
        }
        finally
        {
            // Also on cancel: the INSERT in progress is closed and the table is left partial (appendix B §2.3.6).
            writer.EndData();
        }
    }

    /// <summary>
    /// <c>SELECT * FROM `db`.`t` ORDER BY key</c>; with generated or invisible columns, the explicit list of the others.
    /// Without a unique key, no ORDER BY (like HeidiSQL).
    /// </summary>
    internal string SelectFor(TableDefinition table, IReadOnlyList<ColumnDefinition> columns)
    {
        var dialect = Dialect;
        var list = columns.Count != table.Columns.Count || table.Columns.Any(c => c.IsInvisible)
            ? string.Join(", ", columns.Select(c => dialect.QuoteIdentifier(c.Name)))
            : "*";
        var sql = "SELECT " + list + " FROM " + dialect.QualifiedName(table.Ref);
        var key = RowKey.Of(table);
        if (key.IsUnique)
        {
            sql += " ORDER BY " + string.Join(", ", key.Columns.Select(c => dialect.QuoteIdentifier(c.Name)));
        }
        return sql;
    }

    // ---------- Job session ----------

    private async Task SetupAsync(IDbConnectionHandle handle, bool snapshot, CancellationToken cancellationToken)
    {
        var sql = Dialect.DumpSession;
        foreach (var statement in sql.Setup)
        {
            await handle.ExecuteNonQueryAsync(statement, cancellationToken).ConfigureAwait(false);
        }
        if (snapshot)
        {
            foreach (var statement in sql.BeginSnapshot)
            {
                await handle.ExecuteNonQueryAsync(statement, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Resets the job connection (which is reused), even after a cancel.</summary>
    private async Task RestoreAsync(IDbConnectionHandle handle, bool snapshot)
    {
        if (!handle.IsOpen)
        {
            return;
        }
        var sql = Dialect.DumpSession;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            if (snapshot && sql.EndSnapshot is { } end)
            {
                await handle.ExecuteNonQueryAsync(end, timeout.Token).ConfigureAwait(false);
            }
            foreach (var statement in sql.Restore)
            {
                await handle.ExecuteNonQueryAsync(statement, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is DbOperationException or OperationCanceledException or InvalidOperationException)
        {
            // A connection that cannot be reset is reopened on its next use.
        }
    }

    private async Task<DumpHeader> HeaderAsync(CancellationToken cancellationToken)
    {
        var server = _host.Server ?? throw new InvalidOperationException("The session is not connected.");
        var os = string.Empty;
        if (Dialect.DumpSession.ServerOsQuery is { } query)
        {
            os = await _host.RunJobAsync(async (handle, ct) => Convert.ToString(await handle.ExecuteScalarAsync(query, ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty, cancellationToken).ConfigureAwait(false);
        }
        var version = string.IsNullOrEmpty(server.VersionComment) ? server.VersionText : server.VersionText + " - " + server.VersionComment;
        return new DumpHeader(_request.Host, version, os, _request.AppVersion);
    }

    // ---------- Progress ----------

    private async Task ReportAsync(IProgress<SqlExportProgress> progress, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(ReportInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                progress.Report(new SqlExportProgress(Snapshot(), BytesWritten, _clock.Elapsed));
            }
        }
        catch (OperationCanceledException)
        {
            // End of the job.
        }
    }

    private List<SqlExportItemProgress> Snapshot() =>
        [.. _items.Where(i => i.Pass is Pass.Object or Pass.Trigger).Select(i => new SqlExportItemProgress(
            i.Database, i.Info!.Ref.Name, i.Info.Ref.Kind, i.Rows, i.Info.RowsEstimate ?? 0, i.Duration(_clock), i.State, i.Error))];

    // ---------- Files ----------

    private static FileStream OpenPartial(string path) =>
        // No FileStream buffer: Utf8Output already writes in 1 MB blocks.
        new(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1, FileOptions.SequentialScan);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    internal static string TypeName(DbObjectKind kind) => kind switch
    {
        DbObjectKind.Procedure => "procedure",
        DbObjectKind.Function => "function",
        DbObjectKind.Trigger => "trigger",
        DbObjectKind.Event => "event",
        DbObjectKind.View => "view",
        _ => "table",
    };

    internal static string SafeFileName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c));
    }
}
