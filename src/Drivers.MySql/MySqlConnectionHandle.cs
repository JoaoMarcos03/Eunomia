using System.Data.Common;
using System.Globalization;
using Eunomia.Drivers.Abstractions;
using MySqlConnector;

namespace Eunomia.Drivers.MySql;

/// <summary>
/// Opens MySQL/MariaDB connections with MySqlConnector (ADR-08). No pool (the SessionHost manages the connections), user
/// variables allowed, zero dates preserved, <c>LOAD DATA LOCAL</c> off (S4), real affected rows (like HeidiSQL's
/// <c>mysql_affected_rows</c>) and no command timeout in the driver (D8: the client cancels).
/// </summary>
public sealed class MySqlConnectionFactory : IConnectionFactory
{
    public async ValueTask<IDbConnectionHandle> OpenAsync(
        ConnectionOptions options,
        Func<SecretReference, CancellationToken, ValueTask<string>> resolveSecret,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(resolveSecret);
        var builder = CreateBuilder(options);
        if (options.Password is { } secret)
        {
            builder.Password = await resolveSecret(secret, cancellationToken).ConfigureAwait(false);
        }
        var connection = new MySqlConnection(builder.ConnectionString);
        builder.Password = string.Empty;
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var (identity, sqlMode, encryption) = await ReadIdentityAsync(connection, cancellationToken).ConfigureAwait(false);
            return new MySqlConnectionHandle(connection, identity, sqlMode, isRds: false) { Encryption = encryption };
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static MySqlConnectionStringBuilder CreateBuilder(ConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = new MySqlConnectionStringBuilder
        {
            Server = options.Host,
            Port = (uint)options.Port,
            UserID = options.User,
            Database = options.InitialDatabase ?? string.Empty,
            SslMode = options.Tls switch
            {
                TlsMode.Disabled => MySqlSslMode.None,
                TlsMode.Required => MySqlSslMode.Required,
                TlsMode.VerifyCa => MySqlSslMode.VerifyCA,
                TlsMode.VerifyFull => MySqlSslMode.VerifyFull,
                _ => MySqlSslMode.Preferred,
            },
            UseCompression = options.Compression,
            ConnectionTimeout = (uint)Math.Clamp(options.ConnectTimeout?.TotalSeconds ?? 15, 1, 3600),
            Pooling = false,
            AllowUserVariables = true,
            AllowZeroDateTime = true,
            ConvertZeroDateTime = false,
            TreatTinyAsBoolean = false,
            GuidFormat = MySqlGuidFormat.None,
            UseAffectedRows = true,
            AllowLoadLocalInfile = options.AllowLocalInfile,
            AllowPublicKeyRetrieval = true,
            ApplicationName = options.ApplicationName,
            DefaultCommandTimeout = 0,
            CharacterSet = "utf8mb4",
            // 12.4: if KILL QUERY does not answer within 2 s, the socket is closed.
            CancellationTimeout = 2,
        };
        if (options.TlsFiles is { } files)
        {
            if (!string.IsNullOrEmpty(files.CaCertificate))
            {
                builder.SslCa = files.CaCertificate;
            }
            if (!string.IsNullOrEmpty(files.Certificate))
            {
                builder.SslCert = files.Certificate;
            }
            if (!string.IsNullOrEmpty(files.PrivateKey))
            {
                builder.SslKey = files.PrivateKey;
            }
        }
        return builder;
    }

    /// <summary>
    /// Version, <c>sql_mode</c> and the TLS cipher (<c>Ssl_cipher</c>: empty without TLS) in a single round trip. If
    /// <c>SHOW STATUS</c> fails (proxies), the cipher stays unknown and the connection carries on.
    /// </summary>
    internal static async Task<(ServerIdentity Identity, string SqlMode, string? Encryption)> ReadIdentityAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        await using var cmd = new MySqlCommand("SELECT VERSION(), @@version_comment, @@sql_mode;\nSHOW SESSION STATUS LIKE 'Ssl_cipher'", connection);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var versionText = reader.GetString(0);
        var comment = reader.IsDBNull(1) ? null : reader.GetString(1);
        var sqlMode = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
        string? encryption = null;
        try
        {
            if (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false) && await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                encryption = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            }
        }
        catch (MySqlException)
        {
        }
        return (ParseIdentity(versionText, comment), sqlMode, encryption);
    }

    /// <summary>Engine and version from <c>VERSION()</c> ("10.4.32-MariaDB", "8.0.36", "5.5.5-10.6.12-MariaDB").</summary>
    public static ServerIdentity ParseIdentity(string versionText, string? comment)
    {
        ArgumentNullException.ThrowIfNull(versionText);
        var maria = versionText.Contains("mariadb", StringComparison.OrdinalIgnoreCase)
                    || (comment?.Contains("mariadb", StringComparison.OrdinalIgnoreCase) ?? false);
        var text = versionText;
        // Old proxies announced "5.5.5-10.x.y-MariaDB": the real version comes after it.
        if (maria && text.StartsWith("5.5.5-", StringComparison.Ordinal))
        {
            text = text[6..];
        }
        var numeric = new string(text.TakeWhile(c => char.IsAsciiDigit(c) || c == '.').ToArray()).TrimEnd('.');
        var version = Version.TryParse(numeric, out var v) ? v : new Version(0, 0);
        return new ServerIdentity(maria ? DbEngine.MariaDb : DbEngine.MySql, version, versionText, comment);
    }
}

/// <summary>An open connection: executor, metadata, ping and cancel (section 14.1).</summary>
public sealed class MySqlConnectionHandle : IDbConnectionHandle
{
    private readonly MySqlConnection _connection;
    private MySqlCommand? _running;

    internal MySqlConnectionHandle(MySqlConnection connection, ServerIdentity server, string sqlMode, bool isRds)
    {
        _connection = connection;
        Server = server;
        IsRds = isRds;
        SetSqlMode(sqlMode);
        Executor = new MySqlQueryExecutor(this);
        Metadata = new MySqlMetadataProvider(this);
    }

    public ServerIdentity Server { get; }

    /// <summary>The session's <c>@@sql_mode</c>, read on opening and in <see cref="RefreshSessionAsync"/>.</summary>
    public string SqlMode { get; private set; } = string.Empty;

    /// <summary>Dialect with the session's current mode (<c>NO_BACKSLASH_ESCAPES</c> decides <see cref="MySqlDialect.PlainStringLiteral"/>).</summary>
    public MySqlDialect Dialect { get; private set; } = null!;

    public bool IsRds { get; }

    /// <summary>Reads <c>@@SESSION.sql_mode</c> again (after the startup script or a user <c>SET</c>).</summary>
    public async Task RefreshSessionAsync(CancellationToken cancellationToken)
    {
        var mode = await ExecuteScalarAsync("SELECT @@SESSION.sql_mode", cancellationToken).ConfigureAwait(false);
        SetSqlMode(mode as string ?? Convert.ToString(mode, CultureInfo.InvariantCulture) ?? string.Empty);
    }

    private void SetSqlMode(string sqlMode)
    {
        SqlMode = sqlMode;
        Dialect = new MySqlDialect(Server, sqlMode.Contains("NO_BACKSLASH_ESCAPES", StringComparison.OrdinalIgnoreCase), IsRds);
    }

    public long ServerConnectionId => _connection.ServerThread;

    public bool IsOpen => _connection.State == System.Data.ConnectionState.Open;

    public IQueryExecutor Executor { get; }

    public IMetadataProvider Metadata { get; }

    /// <summary>The connection's current database (after <c>USE</c>).</summary>
    public string? Database => string.IsNullOrEmpty(_connection.Database) ? null : _connection.Database;

    /// <summary>TLS cipher read on opening (<c>Ssl_cipher</c>), <c>""</c> without TLS, <c>null</c> if unknown.</summary>
    public string? Encryption { get; init; }

    internal MySqlConnection Connection => _connection;

    public event Action<string>? CommandIssued;

    public Func<string, DbError?>? Guard { get; set; }

    internal MySqlCommand CreateCommand(string sql) => CreateCommand(sql, sql);

    /// <summary>Command with <paramref name="sql"/>; the SQL log shows <paramref name="logged"/> (without what the client appends).</summary>
    internal MySqlCommand CreateCommand(string sql, string logged)
    {
        if (Guard?.Invoke(logged) is { } refused)
        {
            throw new DbOperationException(refused);
        }
        CommandIssued?.Invoke(logged);
        var cmd = new MySqlCommand(sql, _connection) { CommandTimeout = 0 };
        _running = cmd;
        return cmd;
    }

    internal void Finished(MySqlCommand cmd) => Interlocked.CompareExchange(ref _running, null, cmd);

    /// <summary>Cancels the running statement with <c>KILL QUERY</c> through an auxiliary connection (done by MySqlConnector).</summary>
    public Task CancelCurrentAsync(CancellationToken cancellationToken)
    {
        Volatile.Read(ref _running)?.Cancel();
        return Task.CompletedTask;
    }

    public async Task PingAsync(CancellationToken cancellationToken)
    {
        if (!await _connection.PingAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new DbOperationException(new DbError(2006, "HY000", "MySQL server has gone away (ping failed)."));
        }
    }

    /// <summary>Runs a statement without a result (startup script, <c>USE</c>, <c>SET</c>).</summary>
    public async Task<int> ExecuteNonQueryAsync(string sql, CancellationToken cancellationToken)
    {
        await using var cmd = CreateCommand(sql);
        try
        {
            return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Finished(cmd);
        }
    }

    /// <summary>
    /// <c>LOAD DATA LOCAL INFILE</c> fed by the stream (<see cref="MySqlBulkLoader.SourceStream"/>): the server receives
    /// the data without reading files from the client. The SQL log shows the statement with the stream in place of the file.
    /// </summary>
    public async Task<long> BulkLoadAsync(BulkLoadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var loader = new MySqlBulkLoader(_connection)
        {
            TableName = Dialect.QuoteIdentifier(request.Database) + "." + Dialect.QuoteIdentifier(request.Table),
            SourceStream = request.Source,
            Local = true,
            FieldTerminator = request.FieldTerminator,
            LineTerminator = request.LineTerminator,
            NumberOfLinesToSkip = request.IgnoreLines,
            FieldQuotationOptional = request.OptionallyEnclosed,
            ConflictOption = request.Conflict switch
            {
                BulkLoadConflict.Ignore => MySqlBulkLoaderConflictOption.Ignore,
                BulkLoadConflict.Replace => MySqlBulkLoaderConflictOption.Replace,
                _ => MySqlBulkLoaderConflictOption.None,
            },
            Priority = request.LowPriority ? MySqlBulkLoaderPriority.Low : MySqlBulkLoaderPriority.None,
            CharacterSet = request.CharacterSet,
        };
        if (request.Encloser is { } encloser)
        {
            loader.FieldQuotationCharacter = encloser;
        }
        if (request.Escaper is { } escaper)
        {
            loader.EscapeCharacter = escaper;
        }
        loader.Columns.AddRange(request.Columns);
        loader.Expressions.AddRange(request.Expressions);
        var described = DescribeBulkLoad(request, loader.TableName);
        if (Guard?.Invoke(described) is { } refused)
        {
            throw new DbOperationException(refused);
        }
        CommandIssued?.Invoke(described);
        try
        {
            return await loader.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (MySqlException e) when (IsCancellation(e, cancellationToken))
        {
            // The cancel read from the SourceStream arrives wrapped in a LOAD DATA error when it wins the race against
            // MySqlConnector's own cancel; for the engine, cancelling is not an error (the import showed an error instead of "cancelled").
            throw new OperationCanceledException(e.Message, e, cancellationToken);
        }
    }

    /// <summary>The error was caused by the caller's cancel request (an <see cref="OperationCanceledException"/> in the chain).</summary>
    internal static bool IsCancellation(Exception exception, CancellationToken cancellationToken)
    {
        if (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        for (var e = exception.InnerException; e is not null; e = e.InnerException)
        {
            if (e is OperationCanceledException)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The <c>LOAD DATA</c> as the SQL log shows it (MySqlBulkLoader builds the real text).</summary>
    internal string DescribeBulkLoad(BulkLoadRequest request, string table)
    {
        var d = Dialect;
        var sb = new System.Text.StringBuilder("LOAD DATA ");
        sb.Append(request.LowPriority ? "LOW_PRIORITY " : string.Empty).Append("LOCAL INFILE '<stream>' ");
        sb.Append(request.Conflict switch { BulkLoadConflict.Ignore => "IGNORE ", BulkLoadConflict.Replace => "REPLACE ", _ => string.Empty });
        sb.Append("INTO TABLE ").Append(table);
        if (request.CharacterSet is { Length: > 0 } charset)
        {
            sb.Append(" CHARACTER SET ").Append(charset);
        }
        sb.Append(" FIELDS TERMINATED BY ").Append(d.PlainStringLiteral(request.FieldTerminator));
        if (request.Encloser is { } encloser)
        {
            sb.Append(request.OptionallyEnclosed ? " OPTIONALLY" : string.Empty).Append(" ENCLOSED BY ").Append(d.PlainStringLiteral(encloser.ToString()));
        }
        if (request.Escaper is { } escaper)
        {
            sb.Append(" ESCAPED BY ").Append(d.PlainStringLiteral(escaper.ToString()));
        }
        sb.Append(" LINES TERMINATED BY ").Append(d.PlainStringLiteral(request.LineTerminator));
        if (request.IgnoreLines > 0)
        {
            sb.Append(" IGNORE ").Append(request.IgnoreLines.ToString(CultureInfo.InvariantCulture)).Append(" LINES");
        }
        if (request.Columns.Count > 0)
        {
            sb.Append(" (").Append(string.Join(", ", request.Columns)).Append(')');
        }
        if (request.Expressions.Count > 0)
        {
            sb.Append(" SET ").Append(string.Join(", ", request.Expressions));
        }
        return sb.ToString();
    }

    /// <summary>Reads the first value of the first row (or <c>null</c>).</summary>
    public async Task<object?> ExecuteScalarAsync(string sql, CancellationToken cancellationToken)
    {
        await using var cmd = CreateCommand(sql);
        try
        {
            var value = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return value is DBNull ? null : value;
        }
        finally
        {
            Finished(cmd);
        }
    }

    /// <summary>Reads every row of a small (metadata) query as text; NULL stays <c>null</c>.</summary>
    internal async Task<List<string?[]>> QueryRowsAsync(string sql, CancellationToken cancellationToken)
    {
        await using var cmd = CreateCommand(sql);
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await ReadRowsAsync(reader, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Finished(cmd);
        }
    }

    /// <summary>
    /// The rows of each result set of a batch with several statements, in a single round trip (on a remote server
    /// every round trip counts).
    /// </summary>
    internal async Task<List<List<string?[]>>> QueryResultSetsAsync(string sql, CancellationToken cancellationToken)
    {
        await using var cmd = CreateCommand(sql);
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var sets = new List<List<string?[]>>();
            do
            {
                sets.Add(await ReadRowsAsync(reader, cancellationToken).ConfigureAwait(false));
            }
            while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));
            return sets;
        }
        finally
        {
            Finished(cmd);
        }
    }

    private static async Task<List<string?[]>> ReadRowsAsync(MySqlDataReader reader, CancellationToken cancellationToken)
    {
        var rows = new List<string?[]>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = new string?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : AsText(reader, i);
            }
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>Value of a column as text, lossless (dates in ISO format, binaries in UTF-8).</summary>
    internal static string AsText(DbDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        return value switch
        {
            string s => s,
            byte[] b => System.Text.Encoding.UTF8.GetString(b),
            DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            MySqlDateTime md => md.IsValidDateTime ? md.GetDateTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "0000-00-00 00:00:00",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}
