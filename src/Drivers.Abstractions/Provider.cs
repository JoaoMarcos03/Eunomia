namespace Eunomia.Drivers.Abstractions;

/// <summary>Engine family. MySQL and MariaDB share a provider, with differences by capability.</summary>
public enum DbEngine
{
    MySql,
    MariaDb,
    PostgreSql,
    SqlServer,
    Sqlite,
}

/// <summary>Server identity, detected on connecting (<c>VERSION()</c>, <c>@@version_comment</c>).</summary>
public sealed record ServerIdentity(DbEngine Engine, Version Version, string VersionText, string? VersionComment);

/// <summary>
/// Provider of an engine (section 14.1): identity and factory of the other pieces. Drivers implement
/// these contracts and the engine never references them directly (section 21).
/// </summary>
public interface IDbProvider
{
    DbEngine Engine { get; }

    IConnectionFactory Connections { get; }

    /// <summary>Pieces that depend on the server version, for an already open connection.</summary>
    IServerServices ForServer(ServerIdentity server);

    /// <summary>
    /// Pieces for the current state of an open connection (on MySQL, the session's <c>sql_mode</c> decides the escaping of
    /// simple literals). By default, the ones for the server version.
    /// </summary>
    IServerServices ForConnection(IDbConnectionHandle handle) => ForServer(handle.Server);

    /// <summary>
    /// Translates a driver exception (server error, closed socket, timeout) into a <see cref="DbError"/>, or <c>null</c>
    /// if it does not come from the driver. The engine then classifies it with <see cref="IServerServices.Errors"/>.
    /// </summary>
    DbError? TranslateException(Exception exception);

    /// <summary>Classification without a server version (failures before the connection opens).</summary>
    IErrorClassifier Errors { get; }
}

/// <summary>Services without connection state that depend on the server version.</summary>
public interface IServerServices
{
    ServerIdentity Server { get; }

    IDialect Dialect { get; }

    IDdlComposer Ddl { get; }

    IDmlComposer Dml { get; }

    ITypeMapper Types { get; }

    ICapabilities Capabilities { get; }

    IErrorClassifier Errors { get; }
}
