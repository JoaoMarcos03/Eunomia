namespace Eunomia.Drivers.Abstractions;

/// <summary>Família de motor. MySQL e MariaDB partilham provider com diferenças por capability.</summary>
public enum DbEngine
{
    MySql,
    MariaDb,
    PostgreSql,
    SqlServer,
    Sqlite,
}

/// <summary>Identidade do servidor, detetada na ligação (<c>VERSION()</c>, <c>@@version_comment</c>).</summary>
public sealed record ServerIdentity(DbEngine Engine, Version Version, string VersionText, string? VersionComment);

/// <summary>
/// Provider de um motor (secção 14.1): identidade e fábrica das restantes peças. Os drivers implementam
/// estes contratos e o motor nunca os referencia diretamente (secção 21).
/// </summary>
public interface IDbProvider
{
    DbEngine Engine { get; }

    IConnectionFactory Connections { get; }

    /// <summary>Peças que dependem da versão do servidor, para uma ligação já aberta.</summary>
    IServerServices ForServer(ServerIdentity server);

    /// <summary>
    /// Peças para o estado atual de uma ligação aberta (em MySQL, o <c>sql_mode</c> da sessão decide o escaping dos
    /// literais simples). Por omissão, as da versão do servidor.
    /// </summary>
    IServerServices ForConnection(IDbConnectionHandle handle) => ForServer(handle.Server);

    /// <summary>
    /// Traduz uma exceção do driver (erro do servidor, socket fechado, timeout) para <see cref="DbError"/>, ou <c>null</c>
    /// se não for do driver. O motor classifica-a depois com <see cref="IServerServices.Errors"/>.
    /// </summary>
    DbError? TranslateException(Exception exception);

    /// <summary>Classificação sem versão do servidor (falhas antes de a ligação abrir).</summary>
    IErrorClassifier Errors { get; }
}

/// <summary>Serviços sem estado de ligação que dependem da versão do servidor.</summary>
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
