using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Eunomia.App.Query;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Execution;
using Eunomia.Engine.Transfer;

namespace Eunomia.App.Transfer;

/// <summary>Who splits the fields (appendix B §3.1; default: the server).</summary>
public enum CsvImportMethod
{
    /// <summary><c>LOAD DATA LOCAL INFILE</c> fed by the client's stream.</summary>
    Server,

    /// <summary>The client reads the CSV and sends multi-row INSERTs.</summary>
    Client,
}

public sealed record CsvImportRequest
{
    public required string Path { get; init; }

    public string Encoding { get; init; } = SqlFile.AutoDetect;

    public required string Database { get; init; }

    public required string Table { get; init; }

    /// <summary>Checked columns, in list order: field k goes to the k-th one (positional mapping).</summary>
    public required IReadOnlyList<CsvTargetColumn> Columns { get; init; }

    /// <summary>Separators already without the backslash sequences (<see cref="CsvOptions.Unescape"/>).</summary>
    public CsvOptions Csv { get; init; } = new();

    public CsvDuplicates Duplicates { get; init; } = CsvDuplicates.Replace;

    public bool LowPriority { get; init; } = true;

    /// <summary>"Input file contains local formatted numbers": the format is <see cref="NumberFormat"/>'s.</summary>
    public bool LocalNumbers { get; init; }

    public NumberFormatInfo NumberFormat { get; init; } = CultureInfo.CurrentCulture.NumberFormat;

    public bool Truncate { get; init; }

    public CsvImportMethod Method { get; init; } = CsvImportMethod.Server;
}

public sealed record CsvImportProgress(long BytesRead, long TotalBytes, long Rows, TimeSpan Elapsed);

/// <summary>The server does not accept <c>LOAD DATA LOCAL</c>: the message says how to proceed.</summary>
public sealed class CsvLocalInfileDisabledException(string serverMessage, Exception inner)
    : Exception(serverMessage + "\n\nThe server does not accept LOAD DATA LOCAL (local_infile is OFF, the default in MySQL 8). "
        + "Use \"Client parses file contents\", or enable it on the server with SET GLOBAL local_infile = 1.", inner);

public sealed record CsvImportResult(bool Cancelled, long Rows, long Warnings, long BytesRead, long TotalBytes, TimeSpan Elapsed);

/// <summary>
/// CSV import (FAM-E07, PR-15; section 13.2; appendix B §3): in server mode, <c>LOAD DATA LOCAL INFILE</c> on a dedicated
/// connection (the only one with <c>AllowLoadLocalInfile</c>, closed at the end), fed by the file in streaming and
/// converted to UTF-8 on the way (<c>LOAD DATA</c> does not accept UTF-16); with local numbers, the numeric columns
/// go through <c>@variables</c> and <c>REPLACE</c>, as in HeidiSQL. In client mode, the <see cref="CsvReader"/> reads in
/// parallel with the sending (bounded channel) and the <see cref="CsvInsertBatcher"/> runs multi-row INSERTs on a job
/// connection with <c>SQL_MODE=''</c>; the first error stops, with the earlier batches saved. Progress by bytes and
/// cancel in both modes.
/// </summary>
public sealed class CsvImportJob(SessionHost host, CsvImportRequest request)
{
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(100);

    private readonly Stopwatch _clock = new();
    private CountingStream? _counter;
    private long _rows;
    private long _total;

    private IDialect Dialect => (host.ServerServices ?? throw new InvalidOperationException("The session is not connected.")).Dialect;

    public async Task<CsvImportResult> RunAsync(IProgress<CsvImportProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Columns.Count == 0)
        {
            throw new InvalidOperationException("No columns selected.");
        }
        _clock.Restart();
        _total = new FileInfo(request.Path).Length;
        using var stopReports = new CancellationTokenSource();
        var reporter = progress is null ? Task.CompletedTask : ReportAsync(progress, stopReports.Token);
        var cancelled = false;
        long warnings = 0;
        try
        {
            warnings = request.Method == CsvImportMethod.Server
                ? await RunServerAsync(cancellationToken).ConfigureAwait(false)
                : await RunClientAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelled = true;
        }
        finally
        {
            await stopReports.CancelAsync().ConfigureAwait(false);
            await reporter.ConfigureAwait(false);
        }
        var result = new CsvImportResult(cancelled, Interlocked.Read(ref _rows), warnings, _counter?.BytesRead ?? 0, _total, _clock.Elapsed);
        progress?.Report(new CsvImportProgress(result.BytesRead, _total, result.Rows, result.Elapsed));
        return result;
    }

    // ---------- Server ----------

    private async Task<long> RunServerAsync(CancellationToken cancellationToken)
    {
        var (file, encodingName) = await TextFileEncoding.OpenAsync(request.Path, request.Encoding, cancellationToken).ConfigureAwait(false);
        _counter = new CountingStream(file, cancellationToken);
        var encoding = TextFileEncoding.For(encodingName);
        Stream source = encoding is UTF8Encoding
            ? _counter
            : System.Text.Encoding.CreateTranscodingStream(_counter, encoding, new UTF8Encoding(false), leaveOpen: false);
        await using (source.ConfigureAwait(false))
        {
            var handle = await host.OpenDedicatedAsync(o => o with { AllowLocalInfile = true }, cancellationToken).ConfigureAwait(false);
            await using (handle.ConfigureAwait(false))
            {
                if (request.Truncate)
                {
                    await TruncateAsync(handle, cancellationToken).ConfigureAwait(false);
                }
                var columns = new List<string>();
                var expressions = new List<string>();
                var dialect = Dialect;
                for (var i = 0; i < request.Columns.Count; i++)
                {
                    var column = request.Columns[i];
                    var quoted = dialect.QuoteIdentifier(column.Name);
                    if (request.LocalNumbers && column.IsNumeric)
                    {
                        var variable = "@ColVar" + i.ToString(CultureInfo.InvariantCulture);
                        columns.Add(variable);
                        expressions.Add(quoted + " = REPLACE(REPLACE(" + variable + ", " + dialect.StringLiteral(request.NumberFormat.NumberGroupSeparator)
                            + ", ''), " + dialect.StringLiteral(request.NumberFormat.NumberDecimalSeparator) + ", '.')");
                    }
                    else
                    {
                        columns.Add(quoted);
                    }
                }
                var csv = request.Csv;
                long rows;
                try
                {
                    rows = await LoadAsync(handle, source, columns, expressions, csv, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e) when (!cancellationToken.IsCancellationRequested && host.StatementError(e) is { Code: LocalInfileDisabled or NotAllowedCommand or InfileCapabilityDisabled } error)
                {
                    throw new CsvLocalInfileDisabledException(error.Message, e);
                }
                Interlocked.Exchange(ref _rows, rows);
                return Convert.ToInt64(await handle.ExecuteScalarAsync("SELECT @@warning_count", cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>ER_CLIENT_LOCAL_FILES_DISABLED: the server has <c>local_infile</c> off (the MySQL 8 default).</summary>
    private const int LocalInfileDisabled = 3948;

    /// <summary>ER_NOT_ALLOWED_COMMAND: the same on MariaDB up to 10.4 and on older MySQL.</summary>
    private const int NotAllowedCommand = 1148;

    /// <summary>ER_LOAD_INFILE_CAPABILITY_DISABLED: the same on MariaDB 10.5+.</summary>
    private const int InfileCapabilityDisabled = 4166;

    private Task<long> LoadAsync(IDbConnectionHandle handle, Stream source, List<string> columns, List<string> expressions, CsvOptions csv, CancellationToken cancellationToken) =>
        handle.BulkLoadAsync(new BulkLoadRequest
        {
            Database = request.Database,
            Table = request.Table,
            Source = source,
            Columns = columns,
            Expressions = expressions,
            FieldTerminator = csv.FieldTerminator,
            Encloser = csv.Encloser.Length == 1 ? csv.Encloser[0] : null,
            OptionallyEnclosed = csv.OptionallyEnclosed,
            Escaper = csv.Escaper.Length == 1 ? csv.Escaper[0] : null,
            LineTerminator = csv.LineTerminator,
            IgnoreLines = Math.Max(0, csv.IgnoreLines),
            Conflict = request.Duplicates switch
            {
                CsvDuplicates.InsertIgnore => BulkLoadConflict.Ignore,
                CsvDuplicates.Replace => BulkLoadConflict.Replace,
                _ => BulkLoadConflict.None,
            },
            LowPriority = request.LowPriority,
            CharacterSet = "utf8mb4",
        }, cancellationToken);

    // ---------- Client ----------

    private sealed record Batch(string Sql, long LastRecord);

    private async Task<long> RunClientAsync(CancellationToken cancellationToken)
    {
        var (file, encodingName) = await TextFileEncoding.OpenAsync(request.Path, request.Encoding, cancellationToken).ConfigureAwait(false);
        _counter = new CountingStream(file, cancellationToken);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var channel = Channel.CreateBounded<Batch>(new BoundedChannelOptions(2) { SingleReader = true, SingleWriter = true });

        // Parsing in parallel with the sending (section 13.4); inserting is sequential.
        var producer = Task.Run(
            async () =>
            {
                try
                {
                    using var reader = new StreamReader(_counter, TextFileEncoding.For(encodingName), detectEncodingFromByteOrderMarks: false, 1024 * 1024);
                    var csv = new CsvReader(reader, request.Csv);
                    var batcher = new CsvInsertBatcher(request.Database, request.Table, request.Columns, request.Duplicates, request.LowPriority,
                        request.LocalNumbers ? request.NumberFormat : null);
                    while (csv.Read() is { } record)
                    {
                        if (batcher.Add(record) is { } sql)
                        {
                            await channel.Writer.WriteAsync(new Batch(sql, csv.RecordsRead - 1), linked.Token).ConfigureAwait(false);
                        }
                    }
                    if (batcher.Flush() is { } last)
                    {
                        await channel.Writer.WriteAsync(new Batch(last, csv.RecordsRead), linked.Token).ConfigureAwait(false);
                    }
                    channel.Writer.TryComplete();
                }
                catch (Exception e)
                {
                    channel.Writer.TryComplete(e);
                    throw;
                }
            },
            linked.Token);

        try
        {
            return await host.RunJobAsync(
                async (handle, ct) =>
                {
                    await handle.ExecuteNonQueryAsync("SET @OLD_LOCAL_SQL_MODE=@@SQL_MODE, SQL_MODE=''", ct).ConfigureAwait(false);
                    try
                    {
                        if (request.Truncate)
                        {
                            await TruncateAsync(handle, ct).ConfigureAwait(false);
                        }
                        long warnings = 0;
                        await foreach (var batch in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                        {
                            try
                            {
                                var affected = await handle.ExecuteNonQueryAsync(batch.Sql, ct).ConfigureAwait(false);
                                Interlocked.Add(ref _rows, Math.Max(0, affected));
                                warnings += Convert.ToInt64(await handle.ExecuteScalarAsync("SELECT @@warning_count", ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
                            }
                            catch (Exception e) when (!ct.IsCancellationRequested && host.StatementError(e) is { } error)
                            {
                                throw new DbOperationException(error with
                                {
                                    Message = string.Create(CultureInfo.InvariantCulture, $"{error.Message} (in the INSERT with the rows up to record {batch.LastRecord} of the file)"),
                                });
                            }
                        }
                        return warnings;
                    }
                    finally
                    {
                        if (handle.IsOpen)
                        {
                            try
                            {
                                await handle.ExecuteNonQueryAsync("SET SQL_MODE=@OLD_LOCAL_SQL_MODE", CancellationToken.None).ConfigureAwait(false);
                            }
                            catch (Exception e) when (host.StatementError(e) is not null)
                            {
                                // The job connection is reset again on its next use.
                            }
                        }
                    }
                },
                cancellationToken,
                logCommands: false).ConfigureAwait(false);
        }
        finally
        {
            await linked.CancelAsync().ConfigureAwait(false);
            try
            {
                await producer.ConfigureAwait(false);
            }
            catch (Exception) when (linked.IsCancellationRequested)
            {
                // The producer stopped because the sending stopped.
            }
            await _counter.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>"Truncate destination table before import": <c>TRUNCATE TABLE</c>, otherwise <c>DELETE FROM</c>.</summary>
    private async Task TruncateAsync(IDbConnectionHandle handle, CancellationToken cancellationToken)
    {
        var table = new DbObjectRef(request.Database, request.Database, request.Table, DbObjectKind.Table);
        try
        {
            await handle.ExecuteNonQueryAsync(Dialect.Truncate(table), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested && host.StatementError(e) is not null)
        {
            await handle.ExecuteNonQueryAsync("DELETE FROM " + Dialect.QualifiedName(table), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReportAsync(IProgress<CsvImportProgress> progress, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(ReportInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                progress.Report(new CsvImportProgress(_counter?.BytesRead ?? 0, _total, Interlocked.Read(ref _rows), _clock.Elapsed));
            }
        }
        catch (OperationCanceledException)
        {
            // End of the job.
        }
    }
}
