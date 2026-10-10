using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Eunomia.App.Query;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Execution;
using Eunomia.Engine.Transfer;

namespace Eunomia.App.Transfer;

/// <summary>Quem separa os campos (anexo B §3.1; omissão: o servidor).</summary>
public enum CsvImportMethod
{
    /// <summary><c>LOAD DATA LOCAL INFILE</c> alimentado pelo stream do cliente.</summary>
    Server,

    /// <summary>O cliente lê o CSV e envia INSERT multi-row.</summary>
    Client,
}

public sealed record CsvImportRequest
{
    public required string Path { get; init; }

    public string Encoding { get; init; } = SqlFile.AutoDetect;

    public required string Database { get; init; }

    public required string Table { get; init; }

    /// <summary>Colunas marcadas, pela ordem da lista: o campo k vai para a k-ésima (mapeamento posicional).</summary>
    public required IReadOnlyList<CsvTargetColumn> Columns { get; init; }

    /// <summary>Separadores já sem as sequências de barra invertida (<see cref="CsvOptions.Unescape"/>).</summary>
    public CsvOptions Csv { get; init; } = new();

    public CsvDuplicates Duplicates { get; init; } = CsvDuplicates.Replace;

    public bool LowPriority { get; init; } = true;

    /// <summary>"Input file contains local formatted numbers": o formato é o de <see cref="NumberFormat"/>.</summary>
    public bool LocalNumbers { get; init; }

    public NumberFormatInfo NumberFormat { get; init; } = CultureInfo.CurrentCulture.NumberFormat;

    public bool Truncate { get; init; }

    public CsvImportMethod Method { get; init; } = CsvImportMethod.Server;
}

public sealed record CsvImportProgress(long BytesRead, long TotalBytes, long Rows, TimeSpan Elapsed);

/// <summary>O servidor não aceita <c>LOAD DATA LOCAL</c>: a mensagem diz como seguir.</summary>
public sealed class CsvLocalInfileDisabledException(string serverMessage, Exception inner)
    : Exception(serverMessage + "\n\nThe server does not accept LOAD DATA LOCAL (local_infile is OFF, the default in MySQL 8). "
        + "Use \"Client parses file contents\", or enable it on the server with SET GLOBAL local_infile = 1.", inner);

public sealed record CsvImportResult(bool Cancelled, long Rows, long Warnings, long BytesRead, long TotalBytes, TimeSpan Elapsed);

/// <summary>
/// CSV import (FAM-E07, PR-15; secção 13.2; anexo B §3): no modo servidor, <c>LOAD DATA LOCAL INFILE</c> numa ligação
/// dedicada (a única com <c>AllowLoadLocalInfile</c>, fechada no fim), alimentado pelo ficheiro em streaming e
/// convertido para UTF-8 no caminho (o <c>LOAD DATA</c> não aceita UTF-16); com números locais, as colunas numéricas
/// passam por <c>@variáveis</c> e <c>REPLACE</c>, como no HeidiSQL. No modo cliente, o <see cref="CsvReader"/> lê em
/// paralelo com o envio (canal limitado) e o <see cref="CsvInsertBatcher"/> faz INSERT multi-row numa ligação de job
/// com <c>SQL_MODE=''</c>; o primeiro erro pára, com os lotes anteriores gravados. Progresso por bytes e cancel nos dois
/// modos.
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

    // ---------- Servidor ----------

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

    /// <summary>ER_CLIENT_LOCAL_FILES_DISABLED: o servidor tem <c>local_infile</c> desligado (omissão no MySQL 8).</summary>
    private const int LocalInfileDisabled = 3948;

    /// <summary>ER_NOT_ALLOWED_COMMAND: o mesmo no MariaDB até 10.4 e em MySQL antigos.</summary>
    private const int NotAllowedCommand = 1148;

    /// <summary>ER_LOAD_INFILE_CAPABILITY_DISABLED: o mesmo no MariaDB 10.5+.</summary>
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

    // ---------- Cliente ----------

    private sealed record Batch(string Sql, long LastRecord);

    private async Task<long> RunClientAsync(CancellationToken cancellationToken)
    {
        var (file, encodingName) = await TextFileEncoding.OpenAsync(request.Path, request.Encoding, cancellationToken).ConfigureAwait(false);
        _counter = new CountingStream(file, cancellationToken);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var channel = Channel.CreateBounded<Batch>(new BoundedChannelOptions(2) { SingleReader = true, SingleWriter = true });

        // Parsing em paralelo com o envio (secção 13.4); a inserção é sequencial.
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
                                // A ligação de job volta a ser reposta no próximo uso.
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
                // O produtor parou porque o envio parou.
            }
            await _counter.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>"Truncate destination table before import": <c>TRUNCATE TABLE</c>, senão <c>DELETE FROM</c>.</summary>
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
            // Fim do job.
        }
    }
}
