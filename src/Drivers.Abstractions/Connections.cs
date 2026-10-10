namespace Eunomia.Drivers.Abstractions;

public enum TlsMode
{
    Disabled,
    Preferred,
    Required,
    VerifyCa,
    VerifyFull,
}

/// <summary>Reference to a secret stored outside the settings (ADR-10). Resolved by the infrastructure's secrets port.</summary>
public readonly record struct SecretReference(string Key);

/// <summary>In-process SSH tunnel (ADR-09). Authentication in order: agent, private key (with passphrase), password.</summary>
public sealed record SshTunnelOptions(string Host, int Port, string User, SecretReference? Password, string? PrivateKeyPath, int LocalPort = 0)
{
    public SecretReference? Passphrase { get; init; }

    /// <summary>Try the agent's keys (OpenSSH or Pageant).</summary>
    public bool UseAgent { get; init; } = true;

    /// <summary>Handshake timeout (HeidiSQL: 4 s; here it is a limit, not a fixed wait, PR-07).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>SSH channel keep-alive.</summary>
    public TimeSpan KeepAlive { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>Files and ciphers of the SSL tab (appendix A §1.6).</summary>
public sealed record TlsFiles(string? CaCertificate, string? Certificate, string? PrivateKey, string? Cipher);

/// <summary>Parameters of a connection (the Session Manager fields that reach the driver).</summary>
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

    /// <summary>"Enable cleartext authentication" (<c>mysql_clear_password</c> plugin).</summary>
    public bool CleartextAuth { get; init; }

    /// <summary>Application name in the connection attributes (<c>program_name</c>).</summary>
    public string ApplicationName { get; init; } = "Eunomia";

    /// <summary>
    /// Allows <c>LOAD DATA LOCAL INFILE</c> (CSV import in server mode). It lets the server request files from the client:
    /// only on a connection dedicated to the import, closed at the end.
    /// </summary>
    public bool AllowLocalInfile { get; init; }
}

public enum BulkLoadConflict
{
    /// <summary>Duplicates raise an error (INSERT).</summary>
    None,
    Ignore,
    Replace,
}

/// <summary>
/// Bulk load from a client stream (CSV import in server mode, section 13.2): on MySQL,
/// <c>LOAD DATA LOCAL INFILE</c> fed by the stream, without the server reading client files by name.
/// </summary>
public sealed record BulkLoadRequest
{
    public required string Database { get; init; }

    public required string Table { get; init; }

    /// <summary>Data already in the <see cref="CharacterSet"/> encoding.</summary>
    public required Stream Source { get; init; }

    /// <summary>Target of each field, in order: columns (<c>`c`</c>) or variables (<c>@v</c>), already quoted.</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];

    /// <summary><c>SET</c> assignments (<c>`c` = expression</c>).</summary>
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

/// <summary>Opens connections with parameters, TLS, SSH and authentication (section 14.1).</summary>
public interface IConnectionFactory
{
    /// <summary>Resolves a secret at opening time; it never stays in memory beyond the connection.</summary>
    ValueTask<IDbConnectionHandle> OpenAsync(
        ConnectionOptions options,
        Func<SecretReference, CancellationToken, ValueTask<string>> resolveSecret,
        CancellationToken cancellationToken);
}

/// <summary>
/// An open connection. The SessionHost has a primary one, a metadata one and one for jobs (ADR-05);
/// each one is one of these handles.
/// </summary>
public interface IDbConnectionHandle : IAsyncDisposable
{
    ServerIdentity Server { get; }

    /// <summary>Connection id on the server (for <c>KILL QUERY</c>).</summary>
    long ServerConnectionId { get; }

    bool IsOpen { get; }

    IQueryExecutor Executor { get; }

    IMetadataProvider Metadata { get; }

    /// <summary>
    /// Cancels whatever is running (section 12.4): a protocol cancel request or <c>KILL QUERY</c>
    /// through an auxiliary connection. Does not close the connection.
    /// </summary>
    Task CancelCurrentAsync(CancellationToken cancellationToken);

    Task PingAsync(CancellationToken cancellationToken);

    /// <summary>SQL the connection is about to send (for the SQL log: the engine marks it as internal or the user's).</summary>
    event Action<string>? CommandIssued;

    /// <summary>Runs a statement without a result (<c>USE</c>, <c>SET</c>, startup script).</summary>
    Task<int> ExecuteNonQueryAsync(string sql, CancellationToken cancellationToken);

    /// <summary>First value of the first row, or <c>null</c>.</summary>
    Task<object?> ExecuteScalarAsync(string sql, CancellationToken cancellationToken);

    /// <summary>
    /// Loads <see cref="BulkLoadRequest.Source"/> into the table; returns the affected rows. Only on a connection opened with
    /// <see cref="ConnectionOptions.AllowLocalInfile"/>.
    /// </summary>
    Task<long> BulkLoadAsync(BulkLoadRequest request, CancellationToken cancellationToken);

    /// <summary>The connection's current database (after <c>USE</c>), or <c>null</c>.</summary>
    string? Database { get; }

    /// <summary>
    /// The connection's TLS cipher (for example <c>TLS_AES_256_GCM_SHA384</c>), <c>""</c> if the connection has no TLS, or
    /// <c>null</c> if unknown.
    /// </summary>
    string? Encryption => null;

    /// <summary>
    /// Statement guard (read-only session, section 23.2): called with each statement before it goes to the server; a
    /// returned error refuses it (the driver throws a <see cref="DbOperationException"/> with it and nothing is sent).
    /// </summary>
    Func<string, DbError?>? Guard { get; set; }

    /// <summary>
    /// Reads again the session state that changes the generated SQL (on MySQL, <c>sql_mode</c>), after the startup script or
    /// a user <c>SET</c>. Does nothing by default.
    /// </summary>
    Task RefreshSessionAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
