using Eunomia.Drivers.Abstractions;

namespace Eunomia.Engine.Execution;

/// <summary>States of a session (section 12.2).</summary>
public enum SessionState
{
    Disconnected,
    Connecting,
    Ready,
    Busy,
    Reconnecting,
    Failed,
    Closing,
    Closed,
}

/// <summary>Role of a session connection (section 12.2, ADR-05).</summary>
public enum ConnectionRole
{
    /// <summary>User queries, grid edits, DDL: keeps variables, temporary tables and transactions.</summary>
    Primary,

    /// <summary>Tree, columns, counts, autocomplete.</summary>
    Metadata,

    /// <summary>Export, import, maintenance (opened on demand).</summary>
    Job,
}

/// <summary>Priority in a connection's queue (12.3): visible request &gt; prefetch &gt; statistics.</summary>
public enum WorkPriority
{
    Visible,
    Prefetch,
    Statistics,
}

/// <summary>SQL log categories (the shell maps them to HeidiSQL's).</summary>
public enum SessionLogCategory
{
    Error,
    UserSql,
    InternalSql,
    Script,
    Info,
    Debug,

    /// <summary>Does not go to the SQL log (the statements of a Run SQL file: millions of lines; the job logs the summary).</summary>
    None,
}

/// <summary>Destination of the session's messages (SQL log). Called from any thread.</summary>
public interface ISessionLog
{
    void Write(string session, SessionLogCategory category, string message);
}

/// <summary>
/// What the SessionHost needs to connect (the App converts the saved session into this). The password and the SSH one
/// go as <see cref="SecretReference"/>: they are only read from the vault at opening time.
/// </summary>
public sealed record SessionConnectionSettings(string Name, ConnectionOptions Connection)
{
    /// <summary>Startup script file, run after each (re)connection; errors only go to the log.</summary>
    public string? StartupScriptPath { get; init; }

    /// <summary>Ping every X seconds (keep-alive); <c>null</c> turns it off.</summary>
    public TimeSpan? KeepAlive { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Client-side execution timeout for user queries (D8); <c>null</c> = no limit.</summary>
    public TimeSpan? QueryTimeout { get; init; }

    /// <summary>Separate metadata connection (turning it off goes back to HeidiSQL's single-connection mode).</summary>
    public bool SeparateMetadataConnection { get; init; } = true;

    /// <summary>Concurrent job connections (12.6).</summary>
    public int MaxJobConnections { get; init; } = 2;

    /// <summary><c>SET time_zone</c> after connecting ("Use own client time zone").</summary>
    public string? TimeZone { get; init; }

    /// <summary>Text of the "Connecting to …" line in the SQL log.</summary>
    public string? ConnectDescription { get; init; }

    /// <summary>
    /// Read-only session (section 23.2): each connection is put in <c>SET SESSION TRANSACTION READ ONLY</c> and only sends
    /// what <see cref="ReadOnlyPolicy"/> allows; the rest is refused on the client.
    /// </summary>
    public bool ReadOnly { get; init; }
}

/// <summary>State change, with the error when there is one.</summary>
public sealed class SessionStateChangedEventArgs(SessionState state, SessionState previous, Exception? error) : EventArgs
{
    public SessionState State { get; } = state;

    public SessionState Previous { get; } = previous;

    public Exception? Error { get; } = error;
}

/// <summary>
/// The connection dropped and the operation was not retried (user queries are never retried on their own: they may have
/// run on the server). The session reconnects; the user runs it again.
/// </summary>
public sealed class SessionConnectionLostException(string message, DbError error, Exception? inner)
    : Exception(message, inner)
{
    public DbError Error { get; } = error;
}

/// <summary>Server error in a session operation (the error kind comes from the driver's classifier).</summary>
public sealed class SessionOperationException(DbError error, ClassifiedError classified, Exception? inner)
    : Exception(error.Message, inner)
{
    public DbError Error { get; } = error;

    public ClassifiedError Classified { get; } = classified;
}
