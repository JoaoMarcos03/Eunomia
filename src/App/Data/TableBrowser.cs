using System.Globalization;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Execution;
using Eunomia.Engine.Results;

namespace Eunomia.App.Data;

/// <summary>Data tab state of a table (saved per table) and the paging limits.</summary>
public sealed record DataBrowseOptions
{
    public IReadOnlyList<string> Hidden { get; init; } = [];

    public string? Filter { get; init; }

    public IReadOnlyList<DataSort> Sort { get; init; } = [];

    public int RowsPerStep { get; init; } = DataTabQuery.DefaultRowsPerStep;

    public int MaximumRows { get; init; } = DataTabQuery.DefaultMaximumRows;

    public ResultStorage Storage { get; init; } = ResultStorage.InMemory;
}

/// <summary>
/// Data of a table in the Data tab (appendix D B2–B6): loads the first N rows on the primary connection, appends with
/// Next (<c>LIMIT loaded, step</c>) and Show all (up to the maximum), streaming into a
/// <see cref="MaterializedRowSource"/>. Columns cut at 256 are flagged; the full value of a cell is
/// fetched by key (<see cref="IFullValueSource"/>). One browser per load: changing the filter, sort or columns creates another.
/// </summary>
public sealed class TableBrowser : IFullValueSource, IDisposable
{
    private readonly SessionHost _host;
    private readonly IServerServices _services;
    private readonly DisplayFormatter _raw = new(CultureInfo.InvariantCulture, thousandsSeparator: false);
    private DataSelect _select;
    private MaterializedRowSource? _source;
    private ResultSchema? _schema;

    public TableBrowser(SessionHost host, TableDefinition table, DataBrowseOptions options)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(options);
        _host = host;
        _services = host.ServerServices ?? throw new InvalidOperationException("Not connected.");
        Table = table;
        Options = options;
        _select = DataTabQuery.Build(_services.Dialect, table, options.Hidden, options.Filter, options.Sort, 0, options.RowsPerStep);
    }

    public TableDefinition Table { get; }

    public DataBrowseOptions Options { get; }

    /// <summary>Last SELECT run (columns, key and cut columns).</summary>
    public DataSelect Select => _select;

    /// <summary>Grid rows; <c>null</c> until the first result set arrives.</summary>
    public MaterializedRowSource? Source => _source;

    /// <summary>Rows requested so far (HeidiSQL's "wanted").</summary>
    public long Wanted { get; private set; }

    public long Loaded => _source?.RowCount.Count ?? 0;

    /// <summary>Next and Show all stay enabled if the last load brought everything it asked for and has not reached the maximum.</summary>
    public bool CanLoadMore => Loaded == Wanted && Wanted < Options.MaximumRows;

    /// <summary>Show all reached the maximum (D5: warning in the label).</summary>
    public bool ReachedMaximum => Loaded >= Options.MaximumRows;

    /// <summary>The first result set started: the grid can already show the source.</summary>
    public event EventHandler? SourceReady;

    /// <summary>
    /// Time a SELECT abandoned halfway (another choice in the tree) has to finish before the <c>KILL QUERY</c>: a small
    /// page finishes on its own (cancelling it opened another connection and cost seconds on a remote server); a large
    /// table is cancelled right after. Esc cancels immediately.
    /// </summary>
    public static readonly TimeSpan AbandonGrace = TimeSpan.FromMilliseconds(500);

    /// <summary>First load: <paramref name="wanted"/> rows (a multiple of the step, to continue on the right page).</summary>
    public Task LoadAsync(long wanted, CancellationToken cancellationToken)
    {
        var step = Options.RowsPerStep;
        var count = Math.Min(Options.MaximumRows, Math.Max(step, (wanted + step - 1) / step * step));
        return FetchAsync(0, (int)count, cancellationToken);
    }

    /// <summary>Next: one more step, up to the maximum.</summary>
    public Task NextAsync(CancellationToken cancellationToken) =>
        CanLoadMore ? FetchAsync(Loaded, (int)Math.Min(Options.RowsPerStep, Options.MaximumRows - Loaded), cancellationToken) : Task.CompletedTask;

    /// <summary>Show all: the rest, up to the maximum (<c>DatagridMaximumRows</c>).</summary>
    public Task ShowAllAsync(CancellationToken cancellationToken) =>
        CanLoadMore ? FetchAsync(Loaded, (int)(Options.MaximumRows - Loaded), cancellationToken) : Task.CompletedTask;

    private async Task FetchAsync(long offset, int count, CancellationToken cancellationToken)
    {
        _select = DataTabQuery.Build(_services.Dialect, Table, Options.Hidden, Options.Filter, Options.Sort, offset, count);
        Wanted = offset + count;
        var truncatedColumns = _select.Columns
            .Select((name, index) => (name, index))
            .Where(c => _select.TruncatedColumns.Contains(c.name))
            .ToDictionary(c => c.index, _ => DataTabQuery.MaxData);
        var options = new ExecuteOptions(BlockSize: Options.RowsPerStep) { CollectWarnings = false, TruncatedColumns = truncatedColumns };
        var firstBlock = _source is null ? 0 : (int)(offset / Options.RowsPerStep);
        var select = _select;
        try
        {
            await _host.RunPrimaryAsync(async (handle, ct) =>
            {
                await foreach (var e in handle.Executor.ExecuteAsync(SqlBatch.Single(select.Sql), options, ct).ConfigureAwait(false))
                {
                    switch (e)
                    {
                        case ResultSetStarted started when started.ResultSetIndex == 0 && _source is null:
                            _schema = Rename(started.Schema, select);
                            _source = new MaterializedRowSource(_schema, Options.RowsPerStep, Options.Storage);
                            SourceReady?.Invoke(this, EventArgs.Empty);
                            break;
                        case RowBlockReceived block when block.ResultSetIndex == 0:
                            _source!.Append(block.Block.WithPosition(firstBlock + block.Block.BlockIndex, offset + block.Block.FirstRow, _schema));
                            break;
                        case ExecutionError error:
                            throw new DbOperationException(error.Error);
                    }
                }
                return true;
            }, cancellationToken, abandonGrace: AbandonGrace).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelled (Esc, another choice in the tree): the rows that arrived are kept, in whole blocks of one step (the
            // block in progress is left out), and Next and Show all continue from there.
            Wanted = Loaded;
            throw;
        }
        if (!CanLoadMore)
        {
            _source?.Complete();
        }
    }

    /// <summary>
    /// The same rows already loaded, with nothing cut (appendix D B4: Export and Copy as run the SELECT again without
    /// <c>LEFT()</c>), in the same order. The caller disposes the result.
    /// </summary>
    public async Task<MaterializedRowSource> LoadFullRowsAsync(CancellationToken cancellationToken)
    {
        var select = DataTabQuery.Build(_services.Dialect, Table, Options.Hidden, Options.Filter, Options.Sort, 0, (int)Math.Max(1, Loaded), fullRows: true);
        var options = new ExecuteOptions(BlockSize: Options.RowsPerStep) { CollectWarnings = false };
        MaterializedRowSource? result = null;
        await _host.RunPrimaryAsync(async (handle, ct) =>
        {
            await foreach (var e in handle.Executor.ExecuteAsync(SqlBatch.Single(select.Sql), options, ct).ConfigureAwait(false))
            {
                switch (e)
                {
                    case ResultSetStarted started when started.ResultSetIndex == 0 && result is null:
                        result = new MaterializedRowSource(Rename(started.Schema, select), Options.RowsPerStep, Options.Storage);
                        break;
                    case RowBlockReceived block when block.ResultSetIndex == 0:
                        result!.Append(block.Block.WithPosition(block.Block.BlockIndex, block.Block.FirstRow, result.Schema));
                        break;
                    case ExecutionError error:
                        throw new DbOperationException(error.Error);
                }
            }
            return true;
        }, cancellationToken).ConfigureAwait(false);
        result?.Complete();
        return result ?? throw new InvalidOperationException("No result.");
    }

    /// <summary>Source names (the <c>LEFT(…)</c> columns have none) and the key flagged as PK for the bold.</summary>
    private static ResultSchema Rename(ResultSchema schema, DataSelect select)
    {
        if (schema.Count != select.Columns.Count)
        {
            return schema;
        }
        var keys = select.KeyColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new ResultSchema(schema.Columns.Select((c, i) => c with { Name = select.Columns[i], IsPrimaryKey = c.IsPrimaryKey || keys.Contains(select.Columns[i]) }).ToList());
    }

    /// <summary>
    /// Count for the label: <c>SHOW TABLE STATUS LIKE</c> (approximate) or <c>COUNT(*)</c> with
    /// <paramref name="exact"/> ("Query exact row count"), on the metadata connection.
    /// </summary>
    public async Task<(long? Rows, bool Exact)> CountAsync(bool exact, CancellationToken cancellationToken)
    {
        var dialect = _services.Dialect;
        if (exact)
        {
            var sql = "SELECT COUNT(*) FROM " + dialect.QualifiedName(Table.Ref);
            if (DataTabQuery.WhereClause(Options.Filter) is { } where)
            {
                sql += " WHERE " + where;
            }
            var value = await _host.RunMetadataAsync((h, ct) => h.ExecuteScalarAsync(sql, ct), cancellationToken).ConfigureAwait(false);
            return (Convert.ToInt64(value, CultureInfo.InvariantCulture), true);
        }
        var rows = await _host.RunMetadataAsync(async (h, ct) =>
        {
            long? result = null;
            var column = -1;
            var options = new ExecuteOptions(BlockSize: 16) { CollectWarnings = false };
            await foreach (var e in h.Executor.ExecuteAsync(SqlBatch.Single(dialect.EstimatedCount(Table.Ref)), options, ct).ConfigureAwait(false))
            {
                switch (e)
                {
                    case ResultSetStarted started:
                        column = started.Schema.Columns.ToList().FindIndex(c => string.Equals(c.Name, "Rows", StringComparison.OrdinalIgnoreCase));
                        break;
                    case RowBlockReceived block when column >= 0 && result is null && block.Block.RowCount > 0 && !block.Block.IsNull(0, column):
                        result = long.TryParse(_raw.Format(block.Block, 0, column), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
                        break;
                    case ExecutionError error:
                        throw new DbOperationException(error.Error);
                }
            }
            return result;
        }, cancellationToken).ConfigureAwait(false);
        return (rows, false);
    }

    /// <summary>
    /// Quick Filter "More values" (appendix D B10): <c>SELECT col, COUNT(*) AS c FROM t [WHERE filter] GROUP BY col ORDER BY
    /// c DESC, col LIMIT 50</c>, on the metadata connection. <c>null</c> values are NULL; binary comes as hex.
    /// </summary>
    public async Task<IReadOnlyList<(string? Value, long Count)>> TopValuesAsync(string column, CancellationToken cancellationToken)
    {
        var dialect = _services.Dialect;
        var col = dialect.QuoteIdentifier(column);
        var sql = $"SELECT {col}, COUNT(*) AS c FROM {dialect.QualifiedName(Table.Ref)}";
        if (DataTabQuery.WhereClause(Options.Filter) is { } where)
        {
            sql += " WHERE " + where;
        }
        sql += $" GROUP BY {col} ORDER BY c DESC, {col} {dialect.Paging(0, 50)}";
        return await _host.RunMetadataAsync(async (h, ct) =>
        {
            var values = new List<(string?, long)>();
            var options = new ExecuteOptions(BlockSize: 64) { CollectWarnings = false };
            await foreach (var e in h.Executor.ExecuteAsync(SqlBatch.Single(sql), options, ct).ConfigureAwait(false))
            {
                switch (e)
                {
                    case RowBlockReceived block:
                        for (var r = 0; r < block.Block.RowCount; r++)
                        {
                            var value = block.Block.IsNull(r, 0) ? null : Raw(block.Block, r, 0);
                            values.Add((value, long.Parse(Raw(block.Block, r, 1), NumberStyles.Integer, CultureInfo.InvariantCulture)));
                        }
                        break;
                    case ExecutionError error:
                        throw new DbOperationException(error.Error);
                }
            }
            return (IReadOnlyList<(string?, long)>)values;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Raw text of a loaded cell (no thousands separators or suffix); <c>null</c> if it is NULL or not in memory.</summary>
    public string? RawText(long row, int column, out bool truncated)
    {
        truncated = false;
        if (_source is not { } source || !source.TryGetRow(row, out var block, out var r) || block.IsNull(r, column))
        {
            return null;
        }
        truncated = block.IsTruncated(r, column);
        return Raw(block, r, column);
    }

    /// <summary>
    /// Full value of a cell (Ctrl+C, editing, export): without display formatting; if the cell came cut, fetches
    /// the column by the row's key (<c>SELECT col FROM t WHERE key</c>).
    /// </summary>
    public async ValueTask<string?> GetFullTextAsync(long row, int column, CancellationToken cancellationToken = default)
    {
        if (_source is not { } source)
        {
            return null;
        }
        var block = await source.GetBlockAsync((int)(row / source.BlockSize), cancellationToken).ConfigureAwait(false);
        var r = (int)(row - block.FirstRow);
        if (block.IsNull(r, column))
        {
            return null;
        }
        if (!block.IsTruncated(r, column) || !_select.TruncatedColumns.Contains(_select.Columns[column]))
        {
            return Raw(block, r, column);
        }
        var dialect = _services.Dialect;
        var where = string.Join(" AND ", _select.KeyColumns.Select(k =>
        {
            var index = IndexOf(k);
            return dialect.QuoteIdentifier(k) + (block.IsNull(r, index) ? " IS NULL" : " = " + Literal(block, r, index));
        }));
        var sql = $"SELECT {dialect.QuoteIdentifier(_select.Columns[column])} FROM {dialect.QualifiedName(Table.Ref)} WHERE {where} {dialect.Paging(0, 1)}";
        var value = await _host.RunPrimaryAsync((h, ct) => h.ExecuteScalarAsync(sql, ct), cancellationToken).ConfigureAwait(false);
        return value switch
        {
            null or DBNull => null,
            byte[] bytes when source.Schema[column].Storage == StorageKind.Binary => "0x" + Convert.ToHexString(bytes),
            byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture),
        };
    }

    private int IndexOf(string column)
    {
        for (var i = 0; i < _select.Columns.Count; i++)
        {
            if (string.Equals(_select.Columns[i], column, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        throw new InvalidOperationException("Key column not in the SELECT: " + column);
    }

    /// <summary>Lossless raw text (<see cref="DisplayFormatter.FormatRaw"/>) of a cell that is not NULL.</summary>
    private static string Raw(RowBlock block, int row, int column) => DisplayFormatter.FormatRaw(block, row, column) ?? string.Empty;

    /// <summary>SQL literal of a cell value (keys in the WHERE): numbers unquoted, binary as hex, the rest as text.</summary>
    private string Literal(RowBlock block, int row, int column)
    {
        var info = block.Schema[column];
        var text = Raw(block, row, column);
        return info.Storage switch
        {
            StorageKind.Int64 or StorageKind.Double or StorageKind.Decimal when info.IsNumeric => text,
            StorageKind.Binary => text.Length <= 2 ? "''" : text,
            _ => _services.Dialect.StringLiteral(text),
        };
    }

    public void Dispose() => _source?.Dispose();
}
