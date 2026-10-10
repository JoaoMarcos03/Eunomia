namespace Eunomia.Drivers.Abstractions;

/// <summary>Server or protocol error, as the driver receives it.</summary>
public sealed record DbError(int Code, string? SqlState, string Message, int? Line = null);

/// <summary>Categories that decide reconnect, retry and messages (section 14.1).</summary>
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

/// <summary>Server code → category and hint (section 14.1).</summary>
public interface IErrorClassifier
{
    ClassifiedError Classify(DbError error);
}

/// <summary>Error of a driver operation already translated to a <see cref="DbError"/> (the engine classifies it with <see cref="IErrorClassifier"/>).</summary>
public sealed class DbOperationException(DbError error, Exception? inner = null) : Exception(error?.Message, inner)
{
    public DbError Error { get; } = error ?? throw new ArgumentNullException(nameof(error));
}
