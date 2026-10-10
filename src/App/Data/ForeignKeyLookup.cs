using System.Globalization;
using Eunomia.App.Sessions;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Execution;
using Eunomia.Engine.Results;

namespace Eunomia.App.Data;

/// <summary>
/// Lista da chave estrangeira no editor da célula (anexo D B15, "Show values in foreign key columns"): os valores da
/// coluna referida e, se a tabela referida tiver uma coluna de texto, os primeiros 256 caracteres dela ("texto (id)"),
/// ordenados pelo texto, até 10.000; acima disso, o editor normal.
/// </summary>
public static class ForeignKeyLookup
{
    public const int MaxRows = 10000;

    /// <summary>Valores e captions, ou <c>null</c> sem chave estrangeira na coluna ou com 10.000 linhas ou mais.</summary>
    public static async Task<IReadOnlyList<(string Value, string Caption)>?> LoadAsync(OpenSession session, TableDefinition table, string column, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(table);
        var fk = table.ForeignKeys.FirstOrDefault(f => f.Columns.Contains(column, StringComparer.OrdinalIgnoreCase));
        if (fk is null || session.Host.ServerServices is not { } services)
        {
            return null;
        }
        var position = fk.Columns.ToList().FindIndex(c => string.Equals(c, column, StringComparison.OrdinalIgnoreCase));
        var referenced = fk.ReferencedColumns[position];
        var target = await session.Metadata.GetTableAsync(fk.ReferencedTable, cancellationToken).ConfigureAwait(false);
        var text = target.Columns.FirstOrDefault(c => c.Type.Kind == LogicalTypeKind.Text && !string.Equals(c.Name, referenced, StringComparison.OrdinalIgnoreCase));
        var sql = Sql(services.Dialect, fk.ReferencedTable, referenced, text?.Name);
        var rows = await session.Host.RunMetadataAsync(async (h, ct) =>
        {
            var result = new List<(string, string)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var formatter = new DisplayFormatter(CultureInfo.InvariantCulture, thousandsSeparator: false);
            await foreach (var e in h.Executor.ExecuteAsync(new SqlBatch([sql]), new ExecuteOptions { CollectWarnings = false }, ct).ConfigureAwait(false))
            {
                switch (e)
                {
                    case RowBlockReceived block:
                        for (var r = 0; r < block.Block.RowCount; r++)
                        {
                            if (block.Block.IsNull(r, 0))
                            {
                                continue;
                            }
                            var value = DisplayFormatter.FormatRaw(block.Block, r, 0)!;
                            if (!seen.Add(value))
                            {
                                continue;
                            }
                            var caption = block.Block.Schema.Count > 1 && !block.Block.IsNull(r, 1)
                                ? $"{formatter.Format(block.Block, r, 1)} ({value})"
                                : value;
                            result.Add((value, caption));
                        }
                        break;
                    case ExecutionError error:
                        throw new DbOperationException(error.Error);
                }
            }
            return result;
        }, cancellationToken).ConfigureAwait(false);
        return rows.Count >= MaxRows ? null : rows;
    }

    /// <summary><c>SELECT chave[, LEFT(texto, 256)] FROM ref ORDER BY texto|chave LIMIT 10000</c>.</summary>
    public static string Sql(IDialect dialect, DbObjectRef table, string key, string? text)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        var k = dialect.QuoteIdentifier(key);
        return text is null
            ? $"SELECT {k} FROM {dialect.QualifiedName(table)} ORDER BY {k} {dialect.Paging(0, MaxRows)}"
            : $"SELECT {k}, LEFT({dialect.QuoteIdentifier(text)}, 256) FROM {dialect.QualifiedName(table)} ORDER BY {dialect.QuoteIdentifier(text)} {dialect.Paging(0, MaxRows)}";
    }
}
