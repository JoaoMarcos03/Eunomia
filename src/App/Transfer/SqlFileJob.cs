using System.Diagnostics;
using System.Globalization;
using System.Text;
using Eunomia.App.Export;
using Eunomia.App.Query;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Execution;
using Eunomia.Engine.Sql.Splitting;
using Eunomia.Engine.Transfer;

namespace Eunomia.App.Transfer;

public sealed record SqlFileRequest
{
    public required string Path { get; init; }

    /// <summary>Name from <see cref="SqlFile.LoadEncodings"/>; with a BOM, the BOM rules.</summary>
    public string Encoding { get; init; } = SqlFile.AutoDetect;

    /// <summary>The session's current database (the file runs in it, as in HeidiSQL), or <c>null</c>.</summary>
    public string? Database { get; init; }

    public bool StopOnErrors { get; init; } = true;

    /// <summary>"Send batch in one go": statements in multi-statement packets up to <c>max_allowed_packet</c>.</summary>
    public bool BatchInOneGo { get; init; }

    public string Delimiter { get; init; } = ";";

    /// <summary>Name of the saved session (in the journal, to resume).</summary>
    public string Session { get; init; } = string.Empty;
}

/// <summary>Error of a statement in the file (section 13.1: located by statement number and line).</summary>
public sealed record SqlFileError(long Statement, long Line, string Message, string Sql);

public sealed record SqlFileProgress(long BytesRead, long TotalBytes, long Statements, long Errors, long AffectedRows, TimeSpan Elapsed);

public sealed record SqlFileResult(
    Guid JobId,
    bool Cancelled,
    bool StoppedOnError,
    long Statements,
    long Errors,
    long AffectedRows,
    long BytesRead,
    long TotalBytes,
    TimeSpan Elapsed,
    IReadOnlyList<SqlFileError> ErrorList)
{
    /// <summary>The file finished (the journal was deleted).</summary>
    public bool Completed => !Cancelled && !StoppedOnError;
}

/// <summary>The file changed since the job stopped (different size or fingerprint).</summary>
public sealed class SqlFileChangedException(string path)
    : Exception($"The file has changed since the import stopped and cannot be resumed: {path}");

/// <summary>
/// Streaming Run SQL file (27.1 #21; PR-14; sections 13.1–13.3; appendix B §4.2): the file is read in 1 MB blocks
/// and split by the <see cref="StreamingSqlSplitter"/> (<c>DELIMITER</c> is not lost between blocks), and the
/// statements run on a job connection, one by one or in multi-statement packets ("Send batch in one go"), without going
/// to the SQL log (only the summary and the errors). Stop on errors stops at the first error; without it, errors are
/// counted and the job continues. The journal keeps the position after the last statement run (bytes, characters, line
/// and delimiter), and the file's size and fingerprint: a stopped job (cancel, error, crash) resumes from that point.
/// </summary>
public sealed class SqlFileJob
{
    private const int ChunkBytes = 1024 * 1024;
    private const int MaxErrorsKept = 1000;
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan JournalInterval = TimeSpan.FromSeconds(1);

    private readonly SessionHost _host;
    private readonly SqlFileRequest _request;
    private readonly TransferJournal? _journal;
    private readonly TransferJournalEntry? _resume;
    private readonly List<SqlFileError> _errors = [];
    private readonly Stopwatch _clock = new();
    private TimeSpan _lastReport;
    private TimeSpan _lastJournal;
    private TransferJournalEntry? _entry;

    // Job state (only the job thread writes it).
    private long _statements;
    private long _errorCount;
    private long _affected;
    private long _bytesRead;
    private long _totalBytes;

    public SqlFileJob(SessionHost host, SqlFileRequest request, TransferJournal? journal = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(request);
        _host = host;
        _request = request;
        _journal = journal;
        Id = Guid.NewGuid();
    }

    /// <summary>Resumes a stopped job from the journal.</summary>
    public SqlFileJob(SessionHost host, TransferJournalEntry resume, TransferJournal journal)
        : this(host, RequestOf(resume), journal)
    {
        _resume = resume;
        Id = resume.Id;
    }

    public Guid Id { get; }

    /// <summary>Size of the blocks read from the file (tests use small blocks to land on the boundaries).</summary>
    internal int ChunkSize { get; init; } = ChunkBytes;

    public static SqlFileRequest RequestOf(TransferJournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new SqlFileRequest
        {
            Path = entry.Path,
            Encoding = entry.Options.GetValueOrDefault("encoding") ?? SqlFile.AutoDetect,
            Database = entry.Database,
            StopOnErrors = entry.Options.GetValueOrDefault("stopOnErrors") != "false",
            BatchInOneGo = entry.Options.GetValueOrDefault("batch") == "true",
            Delimiter = entry.Delimiter,
            Session = entry.Session,
        };
    }

    public async Task<SqlFileResult> RunAsync(IProgress<SqlFileProgress>? progress, CancellationToken cancellationToken)
    {
        _clock.Restart();
        var cancelled = false;
        var stopped = false;
        try
        {
            stopped = await _host.RunJobAsync((handle, ct) => RunOnConnectionAsync(handle, progress, ct), cancellationToken, logCommands: false).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch
        {
            SaveJournal(force: true);
            throw;
        }
        if (cancelled || stopped)
        {
            SaveJournal(force: true);
        }
        else if (_journal is not null)
        {
            _journal.Delete(Id);
        }
        progress?.Report(Progress());
        return new SqlFileResult(Id, cancelled, stopped, _statements, _errorCount, _affected, _bytesRead, _totalBytes, _clock.Elapsed, [.. _errors]);
    }

    /// <summary>Runs the file; returns <c>true</c> if it stopped on an error (Stop on errors).</summary>
    private async Task<bool> RunOnConnectionAsync(IDbConnectionHandle handle, IProgress<SqlFileProgress>? progress, CancellationToken cancellationToken)
    {
        var dialect = (_host.ServerServices ?? throw new InvalidOperationException("The session is not connected.")).Dialect;
        if (_request.Database is { Length: > 0 } database)
        {
            await handle.ExecuteNonQueryAsync(dialect.ChangeDatabase(database), cancellationToken).ConfigureAwait(false);
        }
        var maxPacket = Convert.ToInt64(await handle.ExecuteScalarAsync("SELECT @@max_allowed_packet", cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);

        await using var stream = new FileStream(_request.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
        _totalBytes = stream.Length;
        var bytes = new byte[Math.Max(ChunkSize, 4096)];
        Encoding encoding;
        string encodingName;
        StreamingSqlSplitter splitter;
        long byteOffset;
        long charOffset;
        if (_resume is { } resume)
        {
            if (resume.InputSize != stream.Length || (resume.InputFingerprint is { } print && print != TransferJournal.Fingerprint(_request.Path)))
            {
                throw new SqlFileChangedException(_request.Path);
            }
            encodingName = _request.Encoding;
            encoding = TextFileEncoding.For(encodingName);
            stream.Seek(resume.Offset, SeekOrigin.Begin);
            splitter = new StreamingSqlSplitter(new SplitterPosition(resume.CharOffset, resume.Line, resume.Delimiter));
            byteOffset = resume.Offset;
            charOffset = resume.CharOffset;
            _statements = resume.Count;
            _errorCount = resume.Errors;
        }
        else
        {
            var head = await stream.ReadAsync(bytes.AsMemory(0, (int)Math.Min(bytes.Length, stream.Length)), cancellationToken).ConfigureAwait(false);
            (encodingName, var bom) = TextFileEncoding.Detect(bytes.AsSpan(0, head), _request.Encoding);
            encoding = TextFileEncoding.For(encodingName);
            stream.Seek(bom, SeekOrigin.Begin);
            splitter = new StreamingSqlSplitter(_request.Delimiter);
            byteOffset = bom;
            charOffset = 0;
        }
        _bytesRead = byteOffset;
        _entry = new TransferJournalEntry
        {
            Id = Id,
            Kind = TransferJobKind.SqlImport,
            Session = _request.Session,
            Database = _request.Database,
            Path = _request.Path,
            InputSize = stream.Length,
            InputFingerprint = _journal is null ? null : _resume?.InputFingerprint ?? TransferJournal.Fingerprint(_request.Path),
            Started = _resume?.Started ?? DateTimeOffset.Now,
            Updated = DateTimeOffset.Now,
            Options = new Dictionary<string, string>
            {
                ["encoding"] = encodingName,
                ["stopOnErrors"] = _request.StopOnErrors ? "true" : "false",
                ["batch"] = _request.BatchInOneGo ? "true" : "false",
            },
            Offset = byteOffset,
            CharOffset = charOffset,
            Line = _resume?.Line ?? 1,
            Delimiter = _resume?.Delimiter ?? _request.Delimiter,
            Count = _statements,
            Errors = _errorCount,
        };
        SaveJournal(force: true);

        var decoder = encoding.GetDecoder();
        var chars = new char[encoding.GetMaxCharCount(ChunkSize) + 2];
        var emitted = new List<StreamedStatement>();
        var packet = new List<(StreamedStatement Statement, long NextByte)>();
        long packetBytes = 0;
        // Limit of the unfinished statement: larger than the server accepts is useless (and memory stays constant).
        var maxPending = Math.Max(16L * 1024 * 1024, maxPacket + (1024 * 1024));
        var eof = false;
        while (!eof)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(bytes.AsMemory(0, ChunkSize), cancellationToken).ConfigureAwait(false);
            eof = read == 0;
            var count = decoder.GetChars(bytes, 0, read, chars, 0, flush: eof);
            _bytesRead = stream.Position;

            // Byte positions of the resume points: counted gradually within the block (each character once).
            var chunkStart = charOffset;
            var countedChars = 0;
            var countedBytes = byteOffset;
            long ByteOffsetOf(long absoluteChar)
            {
                var upTo = (int)Math.Clamp(absoluteChar - chunkStart, countedChars, count);
                countedBytes += encoding.GetByteCount(chars, countedChars, upTo - countedChars);
                countedChars = upTo;
                return countedBytes;
            }

            splitter.Append(chars.AsSpan(0, count), emitted);
            if (eof)
            {
                splitter.Complete(emitted);
            }
            foreach (var statement in emitted)
            {
                var nextByte = ByteOffsetOf(statement.Next.Offset);
                if (!_request.BatchInOneGo)
                {
                    if (await ExecuteAsync(handle, [(statement, nextByte)], cancellationToken).ConfigureAwait(false))
                    {
                        return true;
                    }
                    Report(progress);
                    continue;
                }
                var size = encoding.GetByteCount(statement.Text) + 2;
                if (packet.Count > 0 && packetBytes + size > maxPacket)
                {
                    if (await ExecuteAsync(handle, packet, cancellationToken).ConfigureAwait(false))
                    {
                        return true;
                    }
                    packet.Clear();
                    packetBytes = 0;
                    Report(progress);
                }
                packet.Add((statement, nextByte));
                packetBytes += size;
            }
            emitted.Clear();
            charOffset += count;
            byteOffset = ByteOffsetOf(charOffset);
            if (splitter.PendingLength > maxPending)
            {
                throw new InvalidOperationException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"No delimiter (\"{splitter.Delimiter}\") found in the last {splitter.PendingLength / (1024 * 1024)} MiB of the file, after statement #{_statements}. The statement would be larger than max_allowed_packet."));
            }
            Report(progress);
        }
        if (packet.Count > 0 && await ExecuteAsync(handle, packet, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }
        _bytesRead = _totalBytes;
        return false;
    }

    /// <summary>One statement, or a multi-statement packet; returns <c>true</c> if the job must stop (Stop on errors).</summary>
    private async Task<bool> ExecuteAsync(IDbConnectionHandle handle, IReadOnlyList<(StreamedStatement Statement, long NextByte)> statements, CancellationToken cancellationToken)
    {
        var sql = statements.Count == 1 ? statements[0].Statement.Text : string.Join(";\n", statements.Select(s => s.Statement.Text));
        try
        {
            var affected = await handle.ExecuteNonQueryAsync(sql, cancellationToken).ConfigureAwait(false);
            _affected += Math.Max(0, affected);
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested && _host.StatementError(e) is { } error)
        {
            _errorCount++;
            var first = statements[0].Statement;
            if (_errors.Count < MaxErrorsKept)
            {
                _errors.Add(new SqlFileError(_statements + 1, first.Line + Math.Max(0, (error.Line ?? 1) - 1), error.Message, sql.Length > 2000 ? sql[..2000] : sql));
            }
            if (_request.StopOnErrors)
            {
                // Resuming starts at the statement that failed.
                return true;
            }
        }
        _statements += statements.Count;
        var last = statements[^1];
        _entry = _entry! with
        {
            Offset = last.NextByte,
            CharOffset = last.Statement.Next.Offset,
            Line = last.Statement.Next.Line,
            Delimiter = last.Statement.Next.Delimiter,
            Count = _statements,
            Errors = _errorCount,
            LastStatement = last.Statement.Text.Length > 500 ? last.Statement.Text[..500] : last.Statement.Text,
        };
        SaveJournal(force: false);
        return false;
    }

    private void Report(IProgress<SqlFileProgress>? progress)
    {
        if (progress is not null && _clock.Elapsed - _lastReport >= ReportInterval)
        {
            _lastReport = _clock.Elapsed;
            progress.Report(Progress());
        }
    }

    private SqlFileProgress Progress() => new(_bytesRead, _totalBytes, _statements, _errorCount, _affected, _clock.Elapsed);

    private void SaveJournal(bool force)
    {
        if (_journal is null || _entry is null || (!force && _clock.Elapsed - _lastJournal < JournalInterval))
        {
            return;
        }
        _lastJournal = _clock.Elapsed;
        _entry = _entry with { Updated = DateTimeOffset.Now, Count = _statements, Errors = _errorCount };
        try
        {
            _journal.Save(_entry);
        }
        catch (IOException)
        {
            // Without a journal there is no resume, but the import continues.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
