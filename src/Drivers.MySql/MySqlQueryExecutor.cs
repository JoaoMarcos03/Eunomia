using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Results;
using MySqlConnector;

namespace Eunomia.Drivers.MySql;

/// <summary>
/// Streaming execution (section 14.1): each statement of the batch runs separately; result sets come out in columnar
/// blocks (<see cref="RowBlock"/>), never fully materialised. Each statement's counts (affected,
/// found, warnings, duration and network) are the ones HeidiSQL's SQL log shows (FAM-Q04).
/// </summary>
public sealed class MySqlQueryExecutor(MySqlConnectionHandle handle) : IQueryExecutor
{
    private static readonly MySqlTypeMapper Types = new();

    public async IAsyncEnumerable<ResultEvent> ExecuteAsync(SqlBatch batch, ExecuteOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(options);
        var resultSetIndex = 0;
        for (var index = 0; index < batch.Statements.Count; index++)
        {
            var warnings = new List<(string Level, int Code, string Message)>();
            void OnInfo(object sender, MySqlInfoMessageEventArgs e) => warnings.AddRange(e.Errors.Select(x => (x.Level, (int)x.ErrorCode, x.Message)));
            handle.Connection.InfoMessage += OnInfo;
            var channel = System.Threading.Channels.Channel.CreateBounded<ResultEvent>(new System.Threading.Channels.BoundedChannelOptions(4)
            {
                SingleReader = true,
                SingleWriter = true,
            });
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var producer = RunStatementAsync(batch.Statements[index], index, resultSetIndex, options, channel.Writer, stop.Token);
            var drained = false;
            try
            {
                await foreach (var e in channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    if (e is ResultSetStarted)
                    {
                        resultSetIndex++;
                    }
                    yield return e;
                }
                drained = true;
            }
            finally
            {
                if (!drained)
                {
                    // The reader stopped halfway (exception or break): reading was stuck on the full channel with the reader open
                    // and the connection busy. Cancels the query on the server and waits for the producer to release the connection.
                    await stop.CancelAsync().ConfigureAwait(false);
                    try
                    {
                        await producer.ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // Cancelled on purpose: what matters is that the connection is free.
                    }
                    handle.Connection.InfoMessage -= OnInfo;
                }
            }
            var outcome = await producer.ConfigureAwait(false);
            handle.Connection.InfoMessage -= OnInfo;
            // MySqlConnector only reports warnings for statements without a result set; for the others they come from the
            // SHOW WARNINGS that followed in the same command.
            if (outcome.Warnings is { } collected && warnings.Count == 0)
            {
                warnings.AddRange(collected);
            }
            foreach (var w in warnings)
            {
                var severity = w.Level switch
                {
                    "Note" => MessageSeverity.Note,
                    "Warning" => MessageSeverity.Warning,
                    _ => MessageSeverity.Info,
                };
                yield return new ServerMessage(index, severity, w.Message, w.Code);
            }
            if (outcome.Error is { } error)
            {
                yield return new ExecutionError(index, error);
                if (options.StopOnError)
                {
                    yield break;
                }
                continue;
            }
            yield return new StatementCompleted(index, outcome.Affected, outcome.Found, warnings.Count, outcome.Duration, outcome.Network, outcome.Skipped);
        }
    }

    private readonly record struct Outcome(
        long Affected, long Found, TimeSpan Duration, TimeSpan Network, DbError? Error, bool HadResultSet = false, int Skipped = 0,
        IReadOnlyList<(string Level, int Code, string Message)>? Warnings = null);

    /// <summary>Appended to statements that may return rows: warnings arrive in the same round trip (PR-10).</summary>
    private const string WarningsSuffix = "\n;SHOW WARNINGS";

    /// <summary>The result set has the shape of <c>SHOW WARNINGS</c> (Level, Code, Message).</summary>
    private static bool IsWarningsShape(MySqlDataReader reader) =>
        reader.FieldCount == 3
        && string.Equals(reader.GetName(0), "Level", StringComparison.Ordinal)
        && string.Equals(reader.GetName(1), "Code", StringComparison.Ordinal)
        && string.Equals(reader.GetName(2), "Message", StringComparison.Ordinal);

    /// <summary>Runs a statement and writes the result events to the channel (bounded: backpressure, section 11.4).</summary>
    private async Task<Outcome> RunStatementAsync(
        string sql, int index, int firstResultSet, ExecuteOptions options,
        System.Threading.Channels.ChannelWriter<ResultEvent> writer, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var network = TimeSpan.Zero;
        long found = 0;
        var resultSet = firstResultSet;
        var skipped = 0;
        MySqlCommand? cmd = null;
        // SHOW WARNINGS follows in the same command (multi-statement): the last result set, if it has its shape, holds the
        // statement's warnings and not a result.
        var withWarnings = options.CollectWarnings && MySqlStatementShape.MayReturnRows(sql) && MySqlStatementShape.EndsInCode(sql);
        List<(string Level, int Code, string Message)>? warnings = null;
        try
        {
            cmd = handle.CreateCommand(withWarnings ? sql + WarningsSuffix : sql, sql);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var duration = clock.Elapsed;
            var more = true;
            while (more)
            {
                if (reader.FieldCount == 0)
                {
                    more = await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }
                var fetch = Stopwatch.StartNew();
                var emit = resultSet < options.MaxResultSets;
                var schema = BuildSchema(reader);
                // A result set with the shape of SHOW WARNINGS is held back until we know whether it is the last one.
                var held = withWarnings && IsWarningsShape(reader) ? new List<ResultEvent>() : null;
                var heldWarnings = held is null ? null : new List<(string, int, string)>();
                async ValueTask Write(ResultEvent e)
                {
                    if (held is not null)
                    {
                        held.Add(e);
                    }
                    else
                    {
                        await writer.WriteAsync(e, cancellationToken).ConfigureAwait(false);
                    }
                }
                if (emit)
                {
                    await Write(new ResultSetStarted(index, resultSet, schema)).ConfigureAwait(false);
                }
                long rows = 0;
                var blockIndex = 0;
                var builder = new RowBlockBuilder(schema, blockIndex, 0, options.BlockSize);
                var inBlock = 0;
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    heldWarnings?.Add((reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                        reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture),
                        reader.IsDBNull(2) ? string.Empty : reader.GetString(2)));
                    if (emit)
                    {
                        ReadRow(reader, schema, builder, inBlock, options.TruncatedColumns);
                        inBlock++;
                        if (inBlock == options.BlockSize)
                        {
                            await Write(new RowBlockReceived(index, resultSet, builder.Build())).ConfigureAwait(false);
                            blockIndex++;
                            builder = new RowBlockBuilder(schema, blockIndex, rows + 1, options.BlockSize);
                            inBlock = 0;
                        }
                    }
                    rows++;
                }
                if (emit && inBlock > 0)
                {
                    await Write(new RowBlockReceived(index, resultSet, builder.Build(inBlock))).ConfigureAwait(false);
                }
                if (emit)
                {
                    await Write(new ResultSetCompleted(index, resultSet, rows)).ConfigureAwait(false);
                }
                network += fetch.Elapsed;
                more = await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                if (held is not null && !more)
                {
                    // It was the SHOW WARNINGS we appended.
                    warnings = heldWarnings;
                    break;
                }
                foreach (var e in held ?? [])
                {
                    await writer.WriteAsync(e, cancellationToken).ConfigureAwait(false);
                }
                if (!emit)
                {
                    skipped++;
                }
                found += rows;
                resultSet++;
            }
            var affected = Math.Max(0, reader.RecordsAffected);
            return new Outcome(affected, found, duration, network, null, resultSet > firstResultSet, skipped, warnings);
        }
        catch (MySqlException ex) when (!cancellationToken.IsCancellationRequested || ex.ErrorCode != MySqlErrorCode.QueryInterrupted)
        {
            var error = ToError(ex);
            return new Outcome(0, found, clock.Elapsed, network, withWarnings ? error with { Message = StripWarningsSuffix(error.Message) } : error);
        }
        catch (DbOperationException refused)
        {
            // Refused by the connection's guard (read-only session): a statement error, nothing was sent.
            return new Outcome(0, 0, clock.Elapsed, TimeSpan.Zero, refused.Error);
        }
        finally
        {
            if (cmd is not null)
            {
                handle.Finished(cmd);
                await cmd.DisposeAsync().ConfigureAwait(false);
            }
            writer.TryComplete();
        }
    }

    /// <summary>
    /// Removes from the message the <c>SHOW WARNINGS</c> the client appended: syntax errors quote the rest of the text
    /// (<c>near '…'</c>, cut at 80 characters), which may end with the whole suffix or a piece of it.
    /// </summary>
    internal static string StripWarningsSuffix(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var full = message.Replace(WarningsSuffix, string.Empty, StringComparison.Ordinal);
        if (!ReferenceEquals(full, message) && full.Length != message.Length)
        {
            return full;
        }
        var near = message.IndexOf("' at line ", StringComparison.Ordinal);
        if (near > 0)
        {
            for (var length = WarningsSuffix.Length - 1; length >= 2; length--)
            {
                var part = WarningsSuffix[..length];
                if (string.CompareOrdinal(message, near - length, part, 0, length) == 0)
                {
                    return message.Remove(near - length, length);
                }
            }
        }
        return message;
    }

    public static DbError ToError(MySqlException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return new DbError(ex.Number != 0 ? ex.Number : (int)ex.ErrorCode, ex.SqlState, ex.Message);
    }

    /// <summary>Result schema: names, native type, colour category, storage, PK and NULL.</summary>
    internal static ResultSchema BuildSchema(MySqlDataReader reader)
    {
        var columns = reader.GetColumnSchema();
        var infos = new ColumnInfo[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            var c = (MySqlDbColumn)columns[i];
            var (native, logical) = Describe(c);
            var storage = Types.StorageFor(logical);
            var scale = logical.Kind switch
            {
                LogicalTypeKind.Decimal => logical.Scale ?? 0,
                LogicalTypeKind.DateTime or LogicalTypeKind.Time => logical.FractionDigits ?? 0,
                _ => 0,
            };
            infos[i] = new ColumnInfo(
                c.ColumnName,
                native,
                MySqlTypeMapper.CategoryOf(logical),
                storage,
                IsPrimaryKey: c.IsKey == true,
                IsNullable: c.AllowDBNull != false,
                Scale: scale)
            {
                SourceDatabase = string.IsNullOrEmpty(c.BaseSchemaName) ? null : c.BaseSchemaName,
                SourceTable = string.IsNullOrEmpty(c.BaseTableName) ? null : c.BaseTableName,
                SourceColumn = string.IsNullOrEmpty(c.BaseColumnName) ? null : c.BaseColumnName,
            };
        }
        return new ResultSchema(infos);
    }

    /// <summary>Native type (as in the grid header) and logical type of a result column.</summary>
    internal static (string Native, LogicalType Logical) Describe(MySqlDbColumn column)
    {
        var precision = column.NumericPrecision ?? 0;
        var scale = column.NumericScale ?? 0;
        var dataTypeName = column.DataTypeName ?? string.Empty;
        var unsigned = dataTypeName.Contains("UNSIGNED", StringComparison.OrdinalIgnoreCase);
        var size = column.ColumnSize ?? 0;
        LogicalType logical = column.ProviderType switch
        {
            MySqlDbType.Byte or MySqlDbType.UByte => new LogicalType(LogicalTypeKind.Integer, Bits: 8, Signed: column.ProviderType == MySqlDbType.Byte),
            MySqlDbType.Int16 or MySqlDbType.UInt16 => new LogicalType(LogicalTypeKind.Integer, Bits: 16, Signed: column.ProviderType == MySqlDbType.Int16),
            MySqlDbType.Int24 or MySqlDbType.UInt24 => new LogicalType(LogicalTypeKind.Integer, Bits: 24, Signed: column.ProviderType == MySqlDbType.Int24),
            MySqlDbType.Int32 or MySqlDbType.UInt32 => new LogicalType(LogicalTypeKind.Integer, Bits: 32, Signed: column.ProviderType == MySqlDbType.Int32),
            MySqlDbType.Int64 or MySqlDbType.UInt64 => new LogicalType(LogicalTypeKind.Integer, Bits: 64, Signed: column.ProviderType == MySqlDbType.Int64 && !unsigned),
            MySqlDbType.Bit => new LogicalType(LogicalTypeKind.Bit, Bits: Math.Max(1, size)),
            MySqlDbType.Decimal or MySqlDbType.NewDecimal => new LogicalType(LogicalTypeKind.Decimal, Precision: precision, Scale: scale),
            MySqlDbType.Float => new LogicalType(LogicalTypeKind.Float, Bits: 32),
            MySqlDbType.Double => new LogicalType(LogicalTypeKind.Float, Bits: 64),
            MySqlDbType.Year => new LogicalType(LogicalTypeKind.Integer, Bits: 16, Signed: false),
            MySqlDbType.Date or MySqlDbType.Newdate => new LogicalType(LogicalTypeKind.Date),
            MySqlDbType.Time => new LogicalType(LogicalTypeKind.Time, FractionDigits: scale),
            MySqlDbType.DateTime or MySqlDbType.Timestamp => new LogicalType(LogicalTypeKind.DateTime, FractionDigits: scale),
            MySqlDbType.JSON => new LogicalType(LogicalTypeKind.Json),
            MySqlDbType.Enum => new LogicalType(LogicalTypeKind.Enum),
            MySqlDbType.Set => new LogicalType(LogicalTypeKind.Set),
            MySqlDbType.Geometry => new LogicalType(LogicalTypeKind.Spatial),
            MySqlDbType.Guid => new LogicalType(LogicalTypeKind.Uuid),
            MySqlDbType.TinyBlob or MySqlDbType.Blob or MySqlDbType.MediumBlob or MySqlDbType.LongBlob or MySqlDbType.Binary or MySqlDbType.VarBinary =>
                new LogicalType(LogicalTypeKind.Binary, Length: size),
            _ => new LogicalType(LogicalTypeKind.Text, Length: size),
        };
        return (dataTypeName.ToUpperInvariant(), logical);
    }

    private static void ReadRow(MySqlDataReader reader, ResultSchema schema, RowBlockBuilder builder, int row, IReadOnlyDictionary<int, int>? truncated)
    {
        for (var c = 0; c < schema.Count; c++)
        {
            if (reader.IsDBNull(c))
            {
                builder.SetNull(row, c);
                continue;
            }
            var info = schema[c];
            switch (info.Storage)
            {
                case StorageKind.Int64:
                    builder.SetInt64(row, c, Convert.ToInt64(reader.GetValue(c), CultureInfo.InvariantCulture));
                    break;
                case StorageKind.Decimal:
                    var d = reader.GetDecimal(c);
                    builder.SetDecimalUnscaled(row, c, (long)(d * Pow10(info.Scale)));
                    break;
                case StorageKind.Double:
                    builder.SetDouble(row, c, reader.GetDouble(c));
                    break;
                case StorageKind.Binary:
                    var value = reader.GetValue(c);
                    var bytes = value as byte[] ?? System.Text.Encoding.UTF8.GetBytes(value.ToString() ?? string.Empty);
                    builder.SetBytes(row, c, bytes, truncated is not null && truncated.TryGetValue(c, out var maxBytes) && bytes.Length >= maxBytes);
                    break;
                default:
                    var text = Text(reader, c, info);
                    builder.SetString(row, c, text, truncated is not null && truncated.TryGetValue(c, out var maxChars) && text.Length >= maxChars);
                    break;
            }
        }
    }

    /// <summary>Text of a cell stored as text: dates and times in the server's format, zero dates included.</summary>
    private static string Text(MySqlDataReader reader, int ordinal, ColumnInfo info)
    {
        if (info.NativeType.StartsWith("DECIMAL", StringComparison.Ordinal))
        {
            // DECIMAL above 28 digits does not fit in System.Decimal: read as MySqlDecimal (the server's text).
            return reader.GetMySqlDecimal(ordinal).ToString();
        }
        var value = reader.GetValue(ordinal);
        return value switch
        {
            string s => s,
            DateTime dt => FormatDateTime(dt, info),
            MySqlDateTime md => md.IsValidDateTime ? FormatDateTime(md.GetDateTime(), info) : ZeroDate(info),
            TimeSpan ts => FormatTime(ts, info.Scale),
            MySqlDecimal md => md.ToString(),
            byte[] b => System.Text.Encoding.UTF8.GetString(b),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
    }

    private static string FormatDateTime(DateTime value, ColumnInfo info)
    {
        if (info.NativeType == "DATE")
        {
            return value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        var text = value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        return info.Scale > 0 ? text + "." + Fraction(value.Ticks % TimeSpan.TicksPerSecond, info.Scale) : text;
    }

    private static string ZeroDate(ColumnInfo info) =>
        info.NativeType == "DATE" ? "0000-00-00" : info.Scale > 0 ? "0000-00-00 00:00:00." + new string('0', info.Scale) : "0000-00-00 00:00:00";

    /// <summary>MySQL TIME: can be negative and exceed 24 h (−838:59:59 to 838:59:59).</summary>
    internal static string FormatTime(TimeSpan value, int fractionDigits)
    {
        var negative = value < TimeSpan.Zero;
        var abs = negative ? value.Negate() : value;
        var text = string.Create(CultureInfo.InvariantCulture, $"{(negative ? "-" : string.Empty)}{(long)abs.TotalHours:00}:{abs.Minutes:00}:{abs.Seconds:00}");
        return fractionDigits > 0 ? text + "." + Fraction(abs.Ticks % TimeSpan.TicksPerSecond, fractionDigits) : text;
    }

    private static string Fraction(long ticks, int digits) =>
        ticks.ToString("0000000", CultureInfo.InvariantCulture)[..Math.Min(7, digits)].PadRight(digits, '0');

    private static decimal Pow10(int scale)
    {
        decimal result = 1;
        for (var i = 0; i < scale; i++)
        {
            result *= 10;
        }
        return result;
    }
}
