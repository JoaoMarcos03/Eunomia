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
/// Uma alteração de linha do edit buffer (TR-17): os valores originais (das colunas que identificam a linha; numa
/// linha nova, vazio) e os valores novos das colunas alteradas. As chaves são os nomes das colunas.
/// </summary>
public sealed record RowChange(RowChangeKind Kind, IReadOnlyDictionary<string, CellLiteral> Original, IReadOnlyDictionary<string, CellLiteral> Changes)
{
    /// <summary>
    /// Valores que as colunas alteradas tinham quando a linha foi lida (deteção de conflitos, Fase 14): se no servidor já
    /// forem outros (outra sessão gravou entretanto), o UPDATE não muda nada e lança <see cref="RowConflictException"/>.
    /// <c>null</c> grava sem verificar (sobrescrever).
    /// </summary>
    public IReadOnlyDictionary<string, CellLiteral>? Expected { get; init; }
}

/// <summary>A linha mudou no servidor desde que foi lida: gravar apagaria a alteração de outra sessão.</summary>
public sealed class RowConflictException(string message) : Exception(message);

/// <summary>
/// Colunas que identificam uma linha (anexo D B15, <c>GetWhereClause</c>): a chave primária, senão um índice UNIQUE
/// só com colunas NOT NULL, senão todas as colunas (e então UPDATE/DELETE levam o limite de uma linha).
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

/// <summary>Resultado de uma gravação: as linhas afetadas e os valores da chave que identificam a linha agora.</summary>
public sealed record RowWriteResult(long AffectedRows, IReadOnlyDictionary<string, CellLiteral> KeyAfter);

/// <summary>A gravação não afetou exatamente uma linha (o edit buffer mantém a linha alterada).</summary>
public sealed class RowWriteException(string message) : Exception(message);

/// <summary>
/// Grava uma linha editada no grid (anexo D B15; D3; TR-17) na ligação primária, com o SQL no log: INSERT só com as
/// colunas alteradas (e <c>LAST_INSERT_ID()</c> para o auto incremento que ficou por preencher), UPDATE e DELETE com o
/// WHERE pela chave (valores originais) e sempre <c>LIMIT 1</c>: mesmo que a chave da cache já não exista no servidor
/// (alguém a tirou entretanto), uma gravação nunca muda mais do que uma linha. Tem de afetar uma linha: um UPDATE que
/// não mudou valores (0 linhas com <c>UseAffectedRows</c>) confirma-se com um <c>COUNT(*)</c> pela chave nova.
/// </summary>
public sealed partial class RowWriter(SessionHost host)
{
    private IServerServices Services => host.ServerServices ?? throw new InvalidOperationException("The session is not connected.");

    [System.Text.RegularExpressions.GeneratedRegex(@"^[+-]?(\d+(\.\d*)?|\.\d+)([eE][+-]?\d+)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex Number();

    /// <summary>
    /// Valores para colunas numéricas e BIT têm de ser números (ponto decimal, sem separadores de milhares) ou dígitos
    /// binários. Sem isto, o texto ia entre aspas e um servidor sem modo estrito convertia-o calado: "1,5" ou "1.234,50"
    /// numa cultura com vírgula decimal ficavam 1, "true" ficava 0.
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
                // Auto incremento por preencher: o valor gerado identifica a linha nova.
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
                // Com chave, o WHERE leva também os valores lidos das colunas alteradas (FLOAT não: o texto do servidor
                // arredonda e a comparação falharia sempre).
                var expected = key.IsUnique && change.Expected is { } known
                    ? changes.Where(c => c.Column.Type.Kind != LogicalTypeKind.Float && known.ContainsKey(c.Column.Name))
                        .Select(c => new ColumnValue(c.Column, known[c.Column.Name])).ToList()
                    : [];
                var affected = await ExecuteAsync(dml.Update(table.Ref, changes, [.. Where(change.Original), .. expected], withoutKey: !key.IsUnique), cancellationToken).ConfigureAwait(false);
                if (affected == 0)
                {
                    // 0 linhas: os valores já eram os novos (o servidor não conta), a linha mudou (conflito) ou desapareceu.
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
    /// Apaga várias linhas do grid (Delete selected rows) numa transação própria (14.5): ou todas, ou nenhuma. Corre num
    /// só trabalho da ligação primária, para que nada de outra tab entre no meio da transação. Com uma transação do
    /// utilizador já aberta, junta-se a ela (sem BEGIN nem COMMIT). Cada DELETE tem de afetar uma linha; senão, ROLLBACK e
    /// <see cref="RowWriteException"/>.
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
    /// Relê a linha gravada pela chave (<c>EnsureFullRow</c>): valores completos em texto bruto sem perdas
    /// (<see cref="DisplayFormatter.FormatRaw"/>), ou <c>null</c> por coluna NULL; <c>null</c> se a linha já não existir.
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

    /// <summary>A mensagem do HeidiSQL (anexo D B15), com a nota sobre tabelas sem chave.</summary>
    public static string Expected(string statement, long affected) =>
        string.Create(CultureInfo.InvariantCulture,
            $"The {statement} affected {affected} rows, but 1 was expected. On tables without a primary or unique key, Eunomia must match on all columns, which can fail with rounded or truncated values.");
}
