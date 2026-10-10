using System.Globalization;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Results;

namespace Eunomia.Engine.Execution.Editing;

public enum RowChangeKind
{
    Insert,
    Update,
    Delete,
}

/// <summary>
/// A row change from the edit buffer (TR-17): the original values (of the columns that identify the row; empty for a
/// new row) and the new values of the changed columns. Keys are column names.
/// </summary>
public sealed record RowChange(RowChangeKind Kind, IReadOnlyDictionary<string, CellLiteral> Original, IReadOnlyDictionary<string, CellLiteral> Changes)
{
    /// <summary>
    /// Values the changed columns had when the row was read (conflict detection, phase 14): if they are already different on
    /// the server (another session saved in the meantime), the UPDATE changes nothing and throws a <see cref="RowConflictException"/>.
    /// <c>null</c> saves without checking (overwrite).
    /// </summary>
    public IReadOnlyDictionary<string, CellLiteral>? Expected { get; init; }
}

/// <summary>The row changed on the server since it was read: saving would wipe out another session's change.</summary>
public sealed class RowConflictException(string message) : Exception(message);

/// <summary>
/// Columns that identify a row (appendix D B15, <c>GetWhereClause</c>): the primary key, otherwise a UNIQUE index
/// with only NOT NULL columns, otherwise every column (and then UPDATE/DELETE carry the one-row limit).
/// </summary>
public sealed record RowKey(IReadOnlyList<ColumnDefinition> Columns, bool IsUnique)
{
    public static RowKey Of(TableDefinition table)
    {
        ArgumentNullException.ThrowIfNull(table);
        ColumnDefinition? Find(string name) => table.Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        var primary = table.Indexes.FirstOrDefault(i => i.Kind == IndexKind.Primary);
        var unique = primary ?? table.Indexes.FirstOrDefault(i => i.Kind == IndexKind.Unique && i.Columns.All(c => Find(c) is { IsNullable: false }));
        if (unique is not null && unique.Columns.Select(Find).ToList() is var columns && columns.All(c => c is not null))
        {
            return new RowKey(columns!, IsUnique: true);
        }
        return new RowKey(table.Columns, IsUnique: false);
    }
}

/// <summary>Result of a save: the affected rows and the key values that identify the row now.</summary>
public sealed record RowWriteResult(long AffectedRows, IReadOnlyDictionary<string, CellLiteral> KeyAfter);

/// <summary>The save did not affect exactly one row (the edit buffer keeps the changed row).</summary>
public sealed class RowWriteException(string message) : Exception(message);

/// <summary>
/// Saves a row edited in the grid (appendix D B15; D3; TR-17) on the primary connection, with the SQL in the log: INSERT
/// with only the changed columns (and <c>LAST_INSERT_ID()</c> for an auto-increment left unfilled), UPDATE and DELETE with
/// the WHERE by the key (original values) and always <c>LIMIT 1</c>: even if the cached key no longer exists on the
/// server (someone removed it in the meantime), a save never changes more than one row. It must affect one row: an
/// UPDATE that changed no values (0 rows with <c>UseAffectedRows</c>) is confirmed with a <c>COUNT(*)</c> by the new key.
/// </summary>
public sealed partial class RowWriter(SessionHost host)
{
    private IServerServices Services => host.ServerServices ?? throw new InvalidOperationException("The session is not connected.");

    [System.Text.RegularExpressions.GeneratedRegex(@"^[+-]?(\d+(\.\d*)?|\.\d+)([eE][+-]?\d+)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex Number();

    /// <summary>
    /// Values for numeric and BIT columns must be numbers (decimal point, no thousands separators) or binary
    /// digits. Without this, the text went quoted and a server without strict mode converted it silently: "1,5" or "1.234,50"
    /// in a culture with a decimal comma became 1, and "true" became 0.
    /// </summary>
    public static void Validate(IEnumerable<ColumnValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var v in values)
        {
            if (v.Value.IsFunction || v.Value.Text is not { } text)
            {
                continue;
            }
            var trimmed = text.Trim();
            var ok = v.Column.Type.Kind switch
            {
                LogicalTypeKind.Integer or LogicalTypeKind.Decimal or LogicalTypeKind.Float or LogicalTypeKind.Boolean => Number().IsMatch(trimmed),
                LogicalTypeKind.Bit => Number().IsMatch(trimmed) || (trimmed.Length > 0 && trimmed.All(c => c is '0' or '1')) || IsHex(trimmed),
                _ => true,
            };
            if (!ok)
            {
                throw new RowWriteException(
                    $"\"{text}\" is not a valid value for the numeric column \"{v.Column.Name}\". Use a dot as the decimal separator and no thousands separators, NULL (Ctrl+N) for no value, or an SQL expression (Ctrl+F2).");
            }
        }
    }

    private static bool IsHex(string text) =>
        text.Length > 2 && text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && !text.AsSpan(2).ContainsAnyExcept("0123456789abcdefABCDEF");

    public async Task<RowWriteResult> WriteAsync(TableDefinition table, RowChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(change);
        var dml = Services.Dml;
        var key = RowKey.Of(table);
        var columns = table.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var changes = change.Changes.Where(c => columns.ContainsKey(c.Key)).Select(c => new ColumnValue(columns[c.Key], c.Value)).ToList();
        if (change.Kind != RowChangeKind.Delete)
        {
            Validate(changes);
        }
        List<ColumnValue> Where(IReadOnlyDictionary<string, CellLiteral> values) =>
            [.. key.Columns.Select(c => new ColumnValue(c, values.TryGetValue(c.Name, out var v) ? v : CellLiteral.Null))];
        var after = new Dictionary<string, CellLiteral>(change.Original, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in change.Changes)
        {
            after[name] = value;
        }

        switch (change.Kind)
        {
            case RowChangeKind.Insert:
            {
                var affected = await ExecuteAsync(dml.Insert(table.Ref, changes), cancellationToken).ConfigureAwait(false);
                if (affected != 1)
                {
                    throw new RowWriteException(Expected("INSERT", affected));
                }
                // Auto-increment left unfilled: the generated value identifies the new row.
                if (dml.LastInsertId is { } lastId && table.Columns.FirstOrDefault(c => c.IsAutoIncrement) is { } auto
                    && (!change.Changes.TryGetValue(auto.Name, out var given) || given.IsNull || given.Text is "" or "0"))
                {
                    var id = await host.RunPrimaryAsync((h, ct) => h.ExecuteScalarAsync(lastId, ct), cancellationToken).ConfigureAwait(false);
                    after[auto.Name] = new CellLiteral(Convert.ToString(id, CultureInfo.InvariantCulture));
                }
                return new RowWriteResult(affected, after);
            }
            case RowChangeKind.Update:
            {
                if (changes.Count == 0)
                {
                    return new RowWriteResult(0, after);
                }
                // With a key, the WHERE also carries the values read from the changed columns (not FLOAT: the server's text
                // rounds and the comparison would always fail).
                var expected = key.IsUnique && change.Expected is { } known
                    ? changes.Where(c => c.Column.Type.Kind != LogicalTypeKind.Float && known.ContainsKey(c.Column.Name))
                        .Select(c => new ColumnValue(c.Column, known[c.Column.Name])).ToList()
                    : [];
                var affected = await ExecuteAsync(dml.Update(table.Ref, changes, [.. Where(change.Original), .. expected], withoutKey: !key.IsUnique), cancellationToken).ConfigureAwait(false);
                if (affected == 0)
                {
                    // 0 rows: the values were already the new ones (the server does not count them), the row changed (conflict) or it disappeared.
                    var withNew = expected.Count == 0 ? Where(after)
                        : [.. Where(after), .. changes.Where(c => c.Column.Type.Kind != LogicalTypeKind.Float && !c.Value.IsFunction)];
                    if (await CountAsync(dml.Count(table.Ref, withNew), cancellationToken).ConfigureAwait(false) > 0)
                    {
                        affected = 1;
                    }
                    else if (expected.Count > 0 && await CountAsync(dml.Count(table.Ref, Where(change.Original)), cancellationToken).ConfigureAwait(false) > 0)
                    {
                        throw new RowConflictException(
                            $"The row was changed by another session after it was loaded ({string.Join(", ", expected.Select(e => e.Column.Name))}). " +
                            "Saving now would overwrite that change.");
                    }
                }
                if (affected != 1)
                {
                    throw new RowWriteException(Expected("UPDATE", affected));
                }
                return new RowWriteResult(affected, after);
            }
            default:
            {
                var affected = await ExecuteAsync(dml.Delete(table.Ref, Where(change.Original), withoutKey: !key.IsUnique), cancellationToken).ConfigureAwait(false);
                if (affected != 1)
                {
                    throw new RowWriteException(Expected("DELETE", affected));
                }
                return new RowWriteResult(affected, change.Original);
            }
        }
    }

    /// <summary>
    /// Deletes several grid rows (Delete selected rows) in a transaction of its own (14.5): all of them or none. Runs as a
    /// single unit of work on the primary connection, so nothing from another tab gets into the middle of the transaction. With a
    /// user transaction already open, it joins it (no BEGIN or COMMIT). Each DELETE must affect one row; otherwise, ROLLBACK and
    /// a <see cref="RowWriteException"/>.
    /// </summary>
    public async Task<int> DeleteRowsAsync(TableDefinition table, IReadOnlyList<IReadOnlyDictionary<string, CellLiteral>> originals, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(originals);
        var dml = Services.Dml;
        var key = RowKey.Of(table);
        var statements = originals
            .Select(o => dml.Delete(table.Ref, [.. key.Columns.Select(c => new ColumnValue(c, o.TryGetValue(c.Name, out var v) ? v : CellLiteral.Null))], withoutKey: !key.IsUnique))
            .ToList();
        var own = statements.Count > 1 && !host.Transactions.IsOpen;
        return await host.RunPrimaryAsync(async (h, ct) =>
        {
            if (own)
            {
                await h.ExecuteNonQueryAsync("START TRANSACTION", ct).ConfigureAwait(false);
            }
            var done = 0;
            try
            {
                foreach (var sql in statements)
                {
                    var affected = await h.ExecuteNonQueryAsync(sql, ct).ConfigureAwait(false);
                    if (affected != 1)
                    {
                        throw new RowWriteException(Expected("DELETE", affected) + (own ? " No rows were deleted (the transaction was rolled back)." : string.Empty));
                    }
                    done++;
                    progress?.Report(done);
                }
                if (own)
                {
                    await h.ExecuteNonQueryAsync("COMMIT", ct).ConfigureAwait(false);
                }
                return done;
            }
            catch (Exception) when (own && h.IsOpen)
            {
                await h.ExecuteNonQueryAsync("ROLLBACK", CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the saved row back by its key (<c>EnsureFullRow</c>): full values as lossless raw text
    /// (<see cref="DisplayFormatter.FormatRaw"/>), or <c>null</c> for a NULL column; <c>null</c> if the row no longer exists.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string?>?> ReadRowAsync(TableDefinition table, IReadOnlyDictionary<string, CellLiteral> key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(key);
        var rowKey = RowKey.Of(table);
        var where = rowKey.Columns.Select(c => new ColumnValue(c, key.TryGetValue(c.Name, out var v) ? v : CellLiteral.Null)).ToList();
        var sql = Services.Dml.SelectRow(table.Ref, table.Columns, where);
        return await host.RunPrimaryAsync(async (h, ct) =>
        {
            Dictionary<string, string?>? row = null;
            await foreach (var e in h.Executor.ExecuteAsync(new SqlBatch([sql]), new ExecuteOptions { CollectWarnings = false }, ct).ConfigureAwait(false))
            {
                switch (e)
                {
                    case RowBlockReceived received when row is null && received.Block.RowCount > 0:
                        var block = received.Block;
                        row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                        for (var c = 0; c < block.Schema.Count; c++)
                        {
                            row[block.Schema[c].Name] = DisplayFormatter.FormatRaw(block, 0, c);
                        }
                        break;
                    case ExecutionError error:
                        throw new DbOperationException(error.Error);
                }
            }
            return (IReadOnlyDictionary<string, string?>?)row;
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<long> CountAsync(string sql, CancellationToken cancellationToken) =>
        Convert.ToInt64(await host.RunPrimaryAsync((h, ct) => h.ExecuteScalarAsync(sql, ct), cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);

    private Task<int> ExecuteAsync(string sql, CancellationToken cancellationToken) =>
        host.RunPrimaryAsync((h, ct) => h.ExecuteNonQueryAsync(sql, ct), cancellationToken);

    /// <summary>HeidiSQL's message (appendix D B15), with the note about tables without a key.</summary>
    public static string Expected(string statement, long affected) =>
        string.Create(CultureInfo.InvariantCulture,
            $"The {statement} affected {affected} rows, but 1 was expected. On tables without a primary or unique key, Eunomia must match on all columns, which can fail with rounded or truncated values.");
}
