using System.Data.Common;
using System.Globalization;
using Eunomia.Drivers.Abstractions;
using MySqlConnector;

namespace Eunomia.Drivers.MySql;

/// <summary>
/// Abre ligações MySQL/MariaDB com MySqlConnector (ADR-08). Sem pool (o SessionHost gere as ligações), variáveis de
/// utilizador permitidas, zero dates preservadas, <c>LOAD DATA LOCAL</c> desligado (S4), affected rows reais (como o
/// <c>mysql_affected_rows</c> do HeidiSQL) e sem timeout de comando no driver (D8: o cliente cancela).
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
            // 12.4: se o KILL QUERY não responder em 2 s, fecha-se o socket.
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
    /// Versão, <c>sql_mode</c> e a cifra TLS (<c>Ssl_cipher</c>: vazio sem TLS) numa só ida ao servidor. Se o
    /// <c>SHOW STATUS</c> falhar (proxies), a cifra fica por saber e a ligação continua.
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

    /// <summary>Motor e versão a partir de <c>VERSION()</c> ("10.4.32-MariaDB", "8.0.36", "5.5.5-10.6.12-MariaDB").</summary>
    public static ServerIdentity ParseIdentity(string versionText, string? comment)
    {
        ArgumentNullException.ThrowIfNull(versionText);
        var maria = versionText.Contains("mariadb", StringComparison.OrdinalIgnoreCase)
                    || (comment?.Contains("mariadb", StringComparison.OrdinalIgnoreCase) ?? false);
        var text = versionText;
        // Proxies antigos anunciavam "5.5.5-10.x.y-MariaDB": a versão real vem a seguir.
        if (maria && text.StartsWith("5.5.5-", StringComparison.Ordinal))
        {
            text = text[6..];
        }
        var numeric = new string(text.TakeWhile(c => char.IsAsciiDigit(c) || c == '.').ToArray()).TrimEnd('.');
        var version = Version.TryParse(numeric, out var v) ? v : new Version(0, 0);
        return new ServerIdentity(maria ? DbEngine.MariaDb : DbEngine.MySql, version, versionText, comment);
    }
}

/// <summary>Uma ligação aberta: executor, metadata, ping e cancel (secção 14.1).</summary>
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

    /// <summary><c>@@sql_mode</c> da sessão, lido na abertura e em <see cref="RefreshSessionAsync"/>.</summary>
    public string SqlMode { get; private set; } = string.Empty;

    /// <summary>Dialeto com o modo atual da sessão (o <c>NO_BACKSLASH_ESCAPES</c> decide o <see cref="MySqlDialect.PlainStringLiteral"/>).</summary>
    public MySqlDialect Dialect { get; private set; } = null!;

    public bool IsRds { get; }

    /// <summary>Relê o <c>@@SESSION.sql_mode</c> (depois do startup script ou de um <c>SET</c> do utilizador).</summary>
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

    /// <summary>Base atual da ligação (depois de <c>USE</c>).</summary>
    public string? Database => string.IsNullOrEmpty(_connection.Database) ? null : _connection.Database;

    /// <summary>Cifra TLS lida na abertura (<c>Ssl_cipher</c>), <c>""</c> sem TLS, <c>null</c> se não se sabe.</summary>
    public string? Encryption { get; init; }

    internal MySqlConnection Connection => _connection;

    public event Action<string>? CommandIssued;

    public Func<string, DbError?>? Guard { get; set; }

    internal MySqlCommand CreateCommand(string sql) => CreateCommand(sql, sql);

    /// <summary>Comando com <paramref name="sql"/>; o SQL log mostra <paramref name="logged"/> (sem o que o cliente junta).</summary>
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

    /// <summary>Cancela a instrução em curso com <c>KILL QUERY</c> por uma ligação auxiliar (feito pelo MySqlConnector).</summary>
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

    /// <summary>Executa uma instrução sem resultado (startup script, <c>USE</c>, <c>SET</c>).</summary>
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
    /// <c>LOAD DATA LOCAL INFILE</c> alimentado pelo stream (<see cref="MySqlBulkLoader.SourceStream"/>): o servidor recebe
    /// os dados sem ler ficheiros do cliente. O SQL log mostra a instrução com o stream no lugar do ficheiro.
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
            // O cancel lido do SourceStream chega embrulhado num erro do LOAD DATA quando ganha a corrida ao cancel do próprio
            // MySqlConnector; para o motor, cancelar não é erro (o import mostrava um erro em vez de "cancelado").
            throw new OperationCanceledException(e.Message, e, cancellationToken);
        }
    }

    /// <summary>O erro foi causado pelo cancel pedido pelo chamador (uma <see cref="OperationCanceledException"/> na cadeia).</summary>
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

    /// <summary>O <c>LOAD DATA</c> como o SQL log o mostra (o MySqlBulkLoader monta o texto real).</summary>
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

    /// <summary>Lê o primeiro valor da primeira linha (ou <c>null</c>).</summary>
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

    /// <summary>Lê todas as linhas de uma query pequena (metadata) como texto; NULL fica <c>null</c>.</summary>
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
    /// As linhas de cada result set de um batch com várias instruções, numa só ida ao servidor (num servidor remoto
    /// cada ida conta).
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

    /// <summary>Valor de uma coluna como texto, sem perdas (datas em formato ISO, binários em UTF-8).</summary>
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
