using System.Text;
using Eunomia.Drivers.Abstractions;
using MySqlConnector;

namespace Eunomia.Drivers.MySql;

/// <summary>MySQL/MariaDB provider (section 14.1, ADR-08: MySqlConnector).</summary>
public sealed class MySqlProvider : IDbProvider
{
    public DbEngine Engine => DbEngine.MySql;

    public IConnectionFactory Connections { get; } = new MySqlConnectionFactory();

    public IServerServices ForServer(ServerIdentity server) => new MySqlServerServices(server);

    /// <summary>Pieces with the connection's current mode (<c>NO_BACKSLASH_ESCAPES</c>) and RDS.</summary>
    public IServerServices ForConnection(IDbConnectionHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        return handle is MySqlConnectionHandle my
            ? new MySqlServerServices(my.Server, my.Dialect.NoBackslashEscapes, my.IsRds)
            : ForServer(handle.Server);
    }

    public IErrorClassifier Errors { get; } = new MySqlErrorClassifier();

    public DbError? TranslateException(Exception exception) => exception switch
    {
        DbOperationException db => db.Error,
        // Socket closed in the middle of a read: MySqlConnector wraps it without the CR_SERVER_LOST code.
        MySqlException { InnerException: IOException or System.Net.Sockets.SocketException } my => new DbError(2013, "HY000", "Lost connection to server: " + my.Message),
        MySqlException my => MySqlQueryExecutor.ToError(my),
        // Socket closed or tunnel down: the same as CR_SERVER_LOST.
        IOException or System.Net.Sockets.SocketException => new DbError(2013, "HY000", "Lost connection to server: " + exception.Message),
        ObjectDisposedException or InvalidOperationException when exception.Message.Contains("Broken", StringComparison.OrdinalIgnoreCase) || exception.Message.Contains("Connection must be Open", StringComparison.Ordinal) =>
            new DbError(2006, "HY000", "Server has gone away: " + exception.Message),
        _ => null,
    };
}

/// <summary>Pieces without connection state for a server version.</summary>
public sealed class MySqlServerServices(ServerIdentity server, bool noBackslashEscapes = false, bool isRds = false) : IServerServices
{
    public ServerIdentity Server { get; } = server;

    public IDialect Dialect { get; } = new MySqlDialect(server, noBackslashEscapes, isRds);

    public IDdlComposer Ddl { get; } = new MySqlDdlComposer(new MySqlDialect(server, noBackslashEscapes, isRds));

    public IDmlComposer Dml { get; } = new MySqlDmlComposer(new MySqlDialect(server, noBackslashEscapes, isRds));

    public ITypeMapper Types { get; } = new MySqlTypeMapper();

    public ICapabilities Capabilities { get; } = new MySqlCapabilities(server);

    public IErrorClassifier Errors { get; } = new MySqlErrorClassifier();
}

