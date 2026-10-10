using Eunomia.Drivers.Abstractions;

namespace Eunomia.Engine.Execution;

/// <summary>Estados de uma sessão (secção 12.2).</summary>
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

/// <summary>Papel de uma ligação da sessão (secção 12.2, ADR-05).</summary>
public enum ConnectionRole
{
    /// <summary>Queries do utilizador, edições do grid, DDL: guarda variáveis, temporárias e transações.</summary>
    Primary,

    /// <summary>Árvore, colunas, contagens, autocomplete.</summary>
    Metadata,

    /// <summary>Export, import, maintenance (abertas a pedido).</summary>
    Job,
}

/// <summary>Prioridade na fila de uma ligação (12.3): pedido visível &gt; prefetch &gt; estatísticas.</summary>
public enum WorkPriority
{
    Visible,
    Prefetch,
    Statistics,
}

/// <summary>Categorias do SQL log (o shell converte para as do HeidiSQL).</summary>
public enum SessionLogCategory
{
    Error,
    UserSql,
    InternalSql,
    Script,
    Info,
    Debug,

    /// <summary>Não vai para o SQL log (as instruções de um Run SQL file: milhões de linhas; o job regista o resumo).</summary>
    None,
}

/// <summary>Destino das mensagens da sessão (SQL log). Chamado de qualquer thread.</summary>
public interface ISessionLog
{
    void Write(string session, SessionLogCategory category, string message);
}

/// <summary>
/// O que o SessionHost precisa para ligar (a App converte a sessão gravada para isto). A password e a do SSH vão
/// como <see cref="SecretReference"/>: só são lidas do cofre no momento de abrir.
/// </summary>
public sealed record SessionConnectionSettings(string Name, ConnectionOptions Connection)
{
    /// <summary>Ficheiro de startup script, corrido depois de cada (re)ligação; erros só vão para o log.</summary>
    public string? StartupScriptPath { get; init; }

    /// <summary>Ping every X seconds (keep-alive); <c>null</c> desliga.</summary>
    public TimeSpan? KeepAlive { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Timeout de execução no cliente para queries do utilizador (D8); <c>null</c> = sem limite.</summary>
    public TimeSpan? QueryTimeout { get; init; }

    /// <summary>Ligação de metadata separada (desligar volta ao modo de ligação única do HeidiSQL).</summary>
    public bool SeparateMetadataConnection { get; init; } = true;

    /// <summary>Ligações de job em simultâneo (12.6).</summary>
    public int MaxJobConnections { get; init; } = 2;

    /// <summary><c>SET time_zone</c> depois de ligar ("Use own client time zone").</summary>
    public string? TimeZone { get; init; }

    /// <summary>Texto da linha "Connecting to …" do SQL log.</summary>
    public string? ConnectDescription { get; init; }

    /// <summary>
    /// Sessão read-only (secção 23.2): cada ligação fica em <c>SET SESSION TRANSACTION READ ONLY</c> e só envia o que a
    /// <see cref="ReadOnlyPolicy"/> deixa; o resto é recusado no cliente.
    /// </summary>
    public bool ReadOnly { get; init; }
}

/// <summary>Mudança de estado, com o erro quando há.</summary>
public sealed class SessionStateChangedEventArgs(SessionState state, SessionState previous, Exception? error) : EventArgs
{
    public SessionState State { get; } = state;

    public SessionState Previous { get; } = previous;

    public Exception? Error { get; } = error;
}

/// <summary>
/// A ligação caiu e a operação não foi repetida (queries do utilizador nunca são repetidas sozinhas: podem ter
/// corrido no servidor). A sessão volta a ligar; o utilizador corre outra vez.
/// </summary>
public sealed class SessionConnectionLostException(string message, DbError error, Exception? inner)
    : Exception(message, inner)
{
    public DbError Error { get; } = error;
}

/// <summary>Erro de servidor numa operação da sessão (o tipo de erro vem do classificador do driver).</summary>
public sealed class SessionOperationException(DbError error, ClassifiedError classified, Exception? inner)
    : Exception(error.Message, inner)
{
    public DbError Error { get; } = error;

    public ClassifiedError Classified { get; } = classified;
}
