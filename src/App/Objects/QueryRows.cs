using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Results;

namespace Eunomia.App.Objects;

/// <summary>Rows of a small query (server lists, maintenance results) as raw text, NULL = <c>null</c>.</summary>
internal static class QueryRows
{
    /// <summary>Reads every row on the given connection; a server error becomes a <see cref="DbOperationException"/>.</summary>
    public static async Task<List<string?[]>> ReadAsync(IDbConnectionHandle h, string sql, CancellationToken cancellationToken)
    {
        var rows = new List<string?[]>();
        await foreach (var e in h.Executor.ExecuteAsync(new SqlBatch([sql]), new ExecuteOptions { CollectWarnings = false }, cancellationToken).ConfigureAwait(false))
        {
            switch (e)
            {
                case RowBlockReceived block:
                    for (var r = 0; r < block.Block.RowCount; r++)
                    {
                        var row = new string?[block.Block.Schema.Count];
                        for (var c = 0; c < row.Length; c++)
                        {
                            row[c] = DisplayFormatter.FormatRaw(block.Block, r, c);
                        }
                        rows.Add(row);
                    }
                    break;
                case ExecutionError error:
                    throw new DbOperationException(error.Error);
            }
        }
        return rows;
    }
}
