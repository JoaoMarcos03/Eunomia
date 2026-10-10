namespace Eunomia.Drivers.Abstractions;

/// <summary>Erro do servidor ou do protocolo, como o driver o recebe.</summary>
public sealed record DbError(int Code, string? SqlState, string Message, int? Line = null);

/// <summary>Categorias que decidem reconnect, retry e mensagens (secção 14.1).</summary>
public enum DbErrorCategory
{
    ConnectionLost,
    Deadlock,
    LockTimeout,
    Syntax,
    Permission,
    Timeout,
    Cancelled,
    Constraint,
    Other,
}

public sealed record ClassifiedError(DbErrorCategory Category, bool CanReconnect, bool CanRetry, string? Hint);

/// <summary>Código do servidor → categoria e hint (secção 14.1).</summary>
public interface IErrorClassifier
{
    ClassifiedError Classify(DbError error);
}

/// <summary>Erro de uma operação do driver já traduzido para <see cref="DbError"/> (o motor classifica-o com <see cref="IErrorClassifier"/>).</summary>
public sealed class DbOperationException(DbError error, Exception? inner = null) : Exception(error?.Message, inner)
{
    public DbError Error { get; } = error ?? throw new ArgumentNullException(nameof(error));
}
