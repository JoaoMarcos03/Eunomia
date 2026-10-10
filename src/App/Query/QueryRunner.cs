using System.Globalization;
using System.Text;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Execution;
using Eunomia.Engine.Results;
using Eunomia.Engine.Sql.Splitting;

namespace Eunomia.App.Query;

/// <summary>How to run a batch (appendix C §4.4).</summary>
public sealed record QueryRunOptions
{
    /// <summary>"Send batch in one go": joins the statements with <c>; </c> up to <c>max_allowed_packet</c>.</summary>
    public bool BatchInOneGo { get; init; }

    public bool StopOnErrors { get; init; } = true;

    /// <summary><c>MaxQueryResults</c>: result tabs per run.</summary>
    public int MaxResultSets { get; init; } = 10;

    public int BlockSize { get; init; } = 1024;

    public ResultStorage Storage { get; init; } = ResultStorage.InMemory;
}

/// <summary>A result set of a run (one result tab). The source fills while streaming.</summary>
public sealed class QueryResult(int index, MaterializedRowSource source, string sql, string? name)
{
    public int Index { get; } = index;

    public MaterializedRowSource Source { get; } = source;

    /// <summary>SQL of the statement that produced it (result tab hover).</summary>
    public string Sql { get; } = sql;

    /// <summary>Name from <c>-- name:</c> / <c>-- names:</c>, if any.</summary>
    public string? Name { get; } = name;

    /// <summary>Source table, if every column comes from the same one.</summary>
    public string? SourceTable =>
        Source.Schema.Columns.Select(c => c.SourceTable).Distinct(StringComparer.Ordinal).ToList() is [{ } table] ? table : null;

    /// <summary>"name (Nr × Mc)".</summary>
    public string Caption => QueryText.ResultCaption(Name, SourceTable, Index, Source.RowCount.Count, Source.Schema.Count);
}

/// <summary>Error of a run, with the statement and the editor line.</summary>
public sealed record QueryRunError(int StatementIndex, SqlStatement Statement, DbError Error, int Line);

/// <summary>Summary of a run (the final SQL log line, the error dialog and the history).</summary>
public sealed record QueryRunSummary(
    int Executed,
    int Total,
    long AffectedRows,
    long FoundRows,
    long Warnings,
    TimeSpan Duration,
    TimeSpan Network,
    IReadOnlyList<QueryRunError> Errors,
    bool ReachedMaxResults)
{
    public string LogLine => QueryText.Summary(AffectedRows, FoundRows, Warnings, Executed, Total, Duration, Network);
}

/// <summary>
/// Runs a batch in a query tab (section 4.7; appendix C §4.4): statements one by one or in packets up to
/// <c>max_allowed_packet</c>, Stop on errors, up to 10 result sets streaming (later ones are discarded with a warning),
/// warnings and messages to the log, and the "Affected rows … Duration for k of m queries" summary. Runs on the primary connection.
/// </summary>
public sealed class QueryRunner(SessionHost host)
{
    /// <summary>Overhead per statement in a packet (HeidiSQL counts ~20 bytes).</summary>
    private const int PacketOverhead = 20;

    /// <summary>Statement (or packet) n of m started: "Executing query #n of m ...".</summary>
    public event EventHandler<(int Number, int Total)>? Progress;

    /// <summary>New result set: the source already exists and keeps filling.</summary>
    public event EventHandler<QueryResult>? ResultStarted;

    public event EventHandler<QueryResult>? ResultCompleted;

    /// <summary>Lines for the SQL log (warnings, notes, result tab limit).</summary>
    public event EventHandler<string>? Message;

    public async Task<QueryRunSummary> RunAsync(string batchText, IReadOnlyList<SqlStatement> statements, QueryRunOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batchText);
        ArgumentNullException.ThrowIfNull(statements);
        ArgumentNullException.ThrowIfNull(options);
        var names = QueryText.ResultNames(batchText);
        var packets = options.BatchInOneGo
            ? await PacketsAsync(statements, cancellationToken).ConfigureAwait(false)
            : statements.Select((s, i) => (Sql: s.Text, First: i, Count: 1)).ToList();
        var execute = new ExecuteOptions(options.BlockSize, options.StopOnErrors, MaxResultSets: options.MaxResultSets);
        var errors = new List<QueryRunError>();
        long affected = 0, found = 0, warnings = 0;
        var executed = 0;
        var network = TimeSpan.Zero;
        var duration = TimeSpan.Zero;
        var skipped = 0;
        var results = new Dictionary<int, QueryResult>();
        Progress?.Invoke(this, (1, packets.Count));
        // A SET of sql_mode changes the escaping of the simple literals the application generates on this connection.
        var changesMode = statements.Any(s => s.Text.Contains("sql_mode", StringComparison.OrdinalIgnoreCase));
        await host.RunPrimaryAsync(async (handle, ct) =>
        {
            try
            {
                await RunPacketsAsync(handle, ct).ConfigureAwait(false);
            }
            finally
            {
                if (changesMode && handle.IsOpen && !ct.IsCancellationRequested)
                {
                    await host.RefreshSessionStateAsync(handle, ct).ConfigureAwait(false);
                }
            }
            return true;
        }, cancellationToken).ConfigureAwait(false);

        async Task RunPacketsAsync(IDbConnectionHandle handle, CancellationToken ct)
        {
            var batch = new SqlBatch([.. packets.Select(p => p.Sql)]);
            await foreach (var e in handle.Executor.ExecuteAsync(batch, execute, ct).ConfigureAwait(false))
            {
                switch (e)
                {
                    case ResultSetStarted started:
                        var packet = packets[started.StatementIndex];
                        var statement = statements[packet.First];
                        var name = started.ResultSetIndex < names.Count ? names[started.ResultSetIndex] : QueryText.ResultName(statement.Text);
                        var result = new QueryResult(started.ResultSetIndex, new MaterializedRowSource(started.Schema, options.BlockSize, options.Storage), packet.Sql, name);
                        results[started.ResultSetIndex] = result;
                        ResultStarted?.Invoke(this, result);
                        break;
                    case RowBlockReceived block when results.TryGetValue(block.ResultSetIndex, out var target):
                        target.Source.Append(block.Block);
                        break;
                    case ResultSetCompleted completed when results.TryGetValue(completed.ResultSetIndex, out var done):
                        done.Source.Complete();
                        ResultCompleted?.Invoke(this, done);
                        break;
                    case StatementCompleted completed:
                        var count = packets[completed.StatementIndex].Count;
                        executed += count;
                        affected += completed.AffectedRows;
                        found += completed.FoundRows;
                        warnings += completed.Warnings;
                        network += completed.NetworkDuration;
                        duration += completed.Duration;
                        skipped += completed.SkippedResultSets;
                        if (completed.StatementIndex + 1 < packets.Count)
                        {
                            Progress?.Invoke(this, (completed.StatementIndex + 2, packets.Count));
                        }
                        break;
                    case ServerMessage message:
                        Message?.Invoke(this, message.Code is { } code
                            ? string.Create(CultureInfo.InvariantCulture, $"{message.Severity}: ({code}) {message.Text}")
                            : $"{message.Severity}: {message.Text}");
                        break;
                    case ExecutionError error:
                        var at = packets[error.StatementIndex];
                        var failed = statements[at.First];
                        errors.Add(new QueryRunError(at.First, failed, error.Error, QueryText.ErrorLineOf(failed, error.Error.Message)));
                        if (host.ServerServices?.Errors.Classify(error.Error).Category == DbErrorCategory.Deadlock && host.Transactions.RolledBackByServer())
                        {
                            Message?.Invoke(this, "Error: deadlock. The server rolled back the whole open transaction: the changes made since it started were NOT saved.");
                        }
                        if (options.StopOnErrors)
                        {
                            return;
                        }
                        executed += at.Count;
                        break;
                }
            }
        }

        if (skipped > 0)
        {
            Message?.Invoke(this, string.Create(CultureInfo.InvariantCulture,
                $"Reached maximum number of result tabs ({options.MaxResultSets}). To display more results, increase setting in Tools > Preferences > SQL"));
        }
        return new QueryRunSummary(executed, statements.Count, affected, found, warnings, duration, network, errors, skipped > 0);
    }

    /// <summary>"Send batch in one go": packets of statements joined by <c>; </c> up to <c>max_allowed_packet</c>.</summary>
    private async Task<List<(string Sql, int First, int Count)>> PacketsAsync(IReadOnlyList<SqlStatement> statements, CancellationToken cancellationToken)
    {
        var maxPacket = await host.RunPrimaryAsync(
            async (h, ct) => Convert.ToInt64(await h.ExecuteScalarAsync("SELECT @@max_allowed_packet", ct).ConfigureAwait(false), CultureInfo.InvariantCulture),
            cancellationToken, applyQueryTimeout: false).ConfigureAwait(false);
        var packets = new List<(string, int, int)>();
        var current = new StringBuilder();
        var first = 0;
        var count = 0;
        for (var i = 0; i < statements.Count; i++)
        {
            var text = statements[i].Text;
            var size = Encoding.UTF8.GetByteCount(text) + PacketOverhead;
            if (count > 0 && Encoding.UTF8.GetByteCount(current.ToString()) + size > maxPacket)
            {
                packets.Add((current.ToString(), first, count));
                current.Clear();
                count = 0;
            }
            if (count == 0)
            {
                first = i;
            }
            else
            {
                current.Append("; ");
            }
            current.Append(text);
            count++;
        }
        if (count > 0)
        {
            packets.Add((current.ToString(), first, count));
        }
        return packets;
    }
}
