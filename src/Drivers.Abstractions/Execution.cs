using Eunomia.Engine.Results;

namespace Eunomia.Drivers.Abstractions;

/// <summary>Uma ou mais instruções já separadas pelo splitter (o <c>DELIMITER</c> é do cliente, nunca chega aqui).</summary>
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
    /// <summary>Ler os warnings de cada instrução (<c>SHOW WARNINGS</c>); o Data tab desliga para poupar idas ao servidor.</summary>
    public bool CollectWarnings { get; init; } = true;

    /// <summary>
    /// Colunas cortadas no SELECT (índice → comprimento, como <c>LEFT(col, 256)</c> no Data tab): o valor que chega a esse
    /// comprimento fica marcado como truncado no bloco (sufixo " [...]" e valor completo pedido à parte).
    /// </summary>
    public IReadOnlyDictionary<int, int>? TruncatedColumns { get; init; }
}

/// <summary>
/// Evento do stream de execução (secção 14.1): vários result sets em streaming, nunca materializados.
/// </summary>
public abstract record ResultEvent(int StatementIndex);

public sealed record ResultSetStarted(int StatementIndex, int ResultSetIndex, ResultSchema Schema) : ResultEvent(StatementIndex);

/// <summary>Um bloco de linhas do result set em curso.</summary>
public sealed record RowBlockReceived(int StatementIndex, int ResultSetIndex, RowBlock Block) : ResultEvent(StatementIndex);

public sealed record ResultSetCompleted(int StatementIndex, int ResultSetIndex, long RowCount) : ResultEvent(StatementIndex);

/// <summary>Fim de uma instrução: as contagens que o SQL log do HeidiSQL mostra (FAM-Q04).</summary>
public sealed record StatementCompleted(int StatementIndex, long AffectedRows, long FoundRows, int Warnings, TimeSpan Duration, TimeSpan NetworkDuration, int SkippedResultSets = 0)
    : ResultEvent(StatementIndex);

public enum MessageSeverity
{
    Info,
    Note,
    Warning,
}

/// <summary><c>SHOW WARNINGS</c>, <c>mysql_info</c>, notices do PostgreSQL.</summary>
public sealed record ServerMessage(int StatementIndex, MessageSeverity Severity, string Text, int? Code = null) : ResultEvent(StatementIndex);

public sealed record ExecutionError(int StatementIndex, DbError Error) : ResultEvent(StatementIndex);

/// <summary>Executa batches em streaming (secção 14.1).</summary>
public interface IQueryExecutor
{
    IAsyncEnumerable<ResultEvent> ExecuteAsync(SqlBatch batch, ExecuteOptions options, CancellationToken cancellationToken);
}
