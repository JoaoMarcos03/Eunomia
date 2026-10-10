using Eunomia.Engine.Results;

namespace Eunomia.Drivers.Abstractions;

/// <summary>One or more statements already split by the splitter (<c>DELIMITER</c> belongs to the client, it never gets here).</summary>
public sealed record SqlBatch(IReadOnlyList<string> Statements)
{
    public static SqlBatch Single(string sql) => new([sql]);
}

public sealed record ExecuteOptions(
    int BlockSize = 1024,
    bool StopOnError = true,
    TimeSpan? CommandTimeout = null,
    int MaxResultSets = 10)
{
    /// <summary>Read each statement's warnings (<c>SHOW WARNINGS</c>); the Data tab turns it off to save round trips.</summary>
    public bool CollectWarnings { get; init; } = true;

    /// <summary>
    /// Columns cut in the SELECT (index → length, like <c>LEFT(col, 256)</c> in the Data tab): a value that reaches that
    /// length is flagged as truncated in the block (" [...]" suffix and the full value fetched separately).
    /// </summary>
    public IReadOnlyDictionary<int, int>? TruncatedColumns { get; init; }
}

/// <summary>
/// Event of the execution stream (section 14.1): several result sets streaming, never materialised.
/// </summary>
public abstract record ResultEvent(int StatementIndex);

public sealed record ResultSetStarted(int StatementIndex, int ResultSetIndex, ResultSchema Schema) : ResultEvent(StatementIndex);

/// <summary>A block of rows of the current result set.</summary>
public sealed record RowBlockReceived(int StatementIndex, int ResultSetIndex, RowBlock Block) : ResultEvent(StatementIndex);

public sealed record ResultSetCompleted(int StatementIndex, int ResultSetIndex, long RowCount) : ResultEvent(StatementIndex);

/// <summary>End of a statement: the counts HeidiSQL's SQL log shows (FAM-Q04).</summary>
public sealed record StatementCompleted(int StatementIndex, long AffectedRows, long FoundRows, int Warnings, TimeSpan Duration, TimeSpan NetworkDuration, int SkippedResultSets = 0)
    : ResultEvent(StatementIndex);

public enum MessageSeverity
{
    Info,
    Note,
    Warning,
}

/// <summary><c>SHOW WARNINGS</c>, <c>mysql_info</c>, PostgreSQL notices.</summary>
public sealed record ServerMessage(int StatementIndex, MessageSeverity Severity, string Text, int? Code = null) : ResultEvent(StatementIndex);

public sealed record ExecutionError(int StatementIndex, DbError Error) : ResultEvent(StatementIndex);

/// <summary>Runs batches in streaming (section 14.1).</summary>
public interface IQueryExecutor
{
    IAsyncEnumerable<ResultEvent> ExecuteAsync(SqlBatch batch, ExecuteOptions options, CancellationToken cancellationToken);
}
