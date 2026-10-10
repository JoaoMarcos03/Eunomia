namespace Eunomia.Drivers.Abstractions;

public enum TlsMode
{
    Disabled,
    Preferred,
    Required,
    VerifyCa,
    VerifyFull,
}

/// <summary>Referência a um segredo guardado fora das settings (ADR-10). Resolvida pela porta de segredos da infraestrutura.</summary>
public readonly record struct SecretReference(string Key);

/// <summary>Túnel SSH in-process (ADR-09). Autenticação pela ordem: agent, chave privada (com passphrase), password.</summary>
public sealed record SshTunnelOptions(string Host, int Port, string User, SecretReference? Password, string? PrivateKeyPath, int LocalPort = 0)
{
    public SecretReference? Passphrase { get; init; }

    /// <summary>Tentar as chaves do agent (OpenSSH ou Pageant).</summary>
    public bool UseAgent { get; init; } = true;

    /// <summary>Timeout do handshake (HeidiSQL: 4 s; aqui é um limite, não uma espera fixa, PR-07).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Keep-alive do canal SSH.</summary>
    public TimeSpan KeepAlive { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>Ficheiros e cifras do tab SSL (anexo A §1.6).</summary>
public sealed record TlsFiles(string? CaCertificate, string? Certificate, string? PrivateKey, string? Cipher);

/// <summary>Parâmetros de uma ligação (os campos do Session Manager que chegam ao driver).</summary>
public sealed record ConnectionOptions(
    string Host,
    int Port,
    string User,
    SecretReference? Password,
    string? InitialDatabase = null,
    TlsMode Tls = TlsMode.Preferred,
    SshTunnelOptions? Ssh = null,
    bool Compression = false,
    TimeSpan? ConnectTimeout = null)
{
    public TlsFiles? TlsFiles { get; init; }

    /// <summary>"Enable cleartext authentication" (plugin <c>mysql_clear_password</c>).</summary>
    public bool CleartextAuth { get; init; }

    /// <summary>Nome da aplicação nos atributos da ligação (<c>program_name</c>).</summary>
    public string ApplicationName { get; init; } = "Eunomia";

    /// <summary>
    /// Permite <c>LOAD DATA LOCAL INFILE</c> (CSV import no modo servidor). Deixa o servidor pedir ficheiros ao cliente:
    /// só numa ligação dedicada ao import, fechada no fim.
    /// </summary>
    public bool AllowLocalInfile { get; init; }
}

public enum BulkLoadConflict
{
    /// <summary>Duplicados dão erro (INSERT).</summary>
    None,
    Ignore,
    Replace,
}

/// <summary>
/// Carregamento em bloco a partir de um stream do cliente (CSV import no modo servidor, secção 13.2): no MySQL,
/// <c>LOAD DATA LOCAL INFILE</c> alimentado pelo stream, sem o servidor ler ficheiros do cliente por nome.
/// </summary>
public sealed record BulkLoadRequest
{
    public required string Database { get; init; }

    public required string Table { get; init; }

    /// <summary>Dados já na codificação de <see cref="CharacterSet"/>.</summary>
    public required Stream Source { get; init; }

    /// <summary>Destino de cada campo, por ordem: colunas (<c>`c`</c>) ou variáveis (<c>@v</c>), já com aspas.</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];

    /// <summary>Atribuições do <c>SET</c> (<c>`c` = expressão</c>).</summary>
    public IReadOnlyList<string> Expressions { get; init; } = [];

    public string FieldTerminator { get; init; } = "\t";

    public char? Encloser { get; init; }

    public bool OptionallyEnclosed { get; init; }

    public char? Escaper { get; init; }

    public string LineTerminator { get; init; } = "\n";

    public int IgnoreLines { get; init; }

    public BulkLoadConflict Conflict { get; init; }

    public bool LowPriority { get; init; }

    public string? CharacterSet { get; init; }
}

/// <summary>Abre ligações com parâmetros, TLS, SSH e autenticação (secção 14.1).</summary>
public interface IConnectionFactory
{
    /// <summary>Resolve um segredo no momento de abrir; nunca fica em memória além da ligação.</summary>
    ValueTask<IDbConnectionHandle> OpenAsync(
        ConnectionOptions options,
        Func<SecretReference, CancellationToken, ValueTask<string>> resolveSecret,
        CancellationToken cancellationToken);
}

/// <summary>
/// Uma ligação aberta. O SessionHost tem uma primária, uma de metadata e uma para jobs (ADR-05);
/// cada uma é um handle destes.
/// </summary>
public interface IDbConnectionHandle : IAsyncDisposable
{
    ServerIdentity Server { get; }

    /// <summary>Id da ligação no servidor (para <c>KILL QUERY</c>).</summary>
    long ServerConnectionId { get; }

    bool IsOpen { get; }

    IQueryExecutor Executor { get; }

    IMetadataProvider Metadata { get; }

    /// <summary>
    /// Cancela o que estiver a correr (secção 12.4): pedido de cancel do protocolo ou <c>KILL QUERY</c>
    /// por uma ligação auxiliar. Não fecha a ligação.
    /// </summary>
    Task CancelCurrentAsync(CancellationToken cancellationToken);

    Task PingAsync(CancellationToken cancellationToken);

    /// <summary>SQL que a ligação vai enviar (para o SQL log: o motor marca-o como interno ou do utilizador).</summary>
    event Action<string>? CommandIssued;

    /// <summary>Executa uma instrução sem resultado (<c>USE</c>, <c>SET</c>, startup script).</summary>
    Task<int> ExecuteNonQueryAsync(string sql, CancellationToken cancellationToken);

    /// <summary>Primeiro valor da primeira linha, ou <c>null</c>.</summary>
    Task<object?> ExecuteScalarAsync(string sql, CancellationToken cancellationToken);

    /// <summary>
    /// Carrega <see cref="BulkLoadRequest.Source"/> na tabela; devolve as linhas afetadas. Só numa ligação aberta com
    /// <see cref="ConnectionOptions.AllowLocalInfile"/>.
    /// </summary>
    Task<long> BulkLoadAsync(BulkLoadRequest request, CancellationToken cancellationToken);

    /// <summary>Base atual da ligação (depois de <c>USE</c>), ou <c>null</c>.</summary>
    string? Database { get; }

    /// <summary>
    /// Cifra TLS da ligação (por exemplo <c>TLS_AES_256_GCM_SHA384</c>), <c>""</c> se a ligação vai sem TLS, ou
    /// <c>null</c> se não se sabe.
    /// </summary>
    string? Encryption => null;

    /// <summary>
    /// Guarda das instruções (sessão read-only, secção 23.2): chamada com cada instrução antes de ir para o servidor; um
    /// erro devolvido recusa-a (o driver lança <see cref="DbOperationException"/> com ele e nada é enviado).
    /// </summary>
    Func<string, DbError?>? Guard { get; set; }

    /// <summary>
    /// Relê o estado da sessão que muda o SQL gerado (em MySQL, o <c>sql_mode</c>), depois do startup script ou de um
    /// <c>SET</c> do utilizador. Por omissão não faz nada.
    /// </summary>
    Task RefreshSessionAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
