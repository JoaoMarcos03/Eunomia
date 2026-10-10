using Eunomia.Engine.Results;

namespace Eunomia.App.Export;

/// <summary>
/// Reads the rows and columns to export from the source (appendix B §1.2): only the loaded rows, in grid order,
/// visible columns in visual order.
/// </summary>
public static class GridExportSource
{
    /// <summary>Some cell to export came cut (it must be read again without <c>LEFT()</c>).</summary>
    public static async Task<bool> HasTruncatedAsync(IRowSource source, IReadOnlyList<long> rows, IReadOnlyList<int> columns, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(columns);
        foreach (var row in rows)
        {
            var block = await source.GetBlockAsync((int)(row / source.BlockSize), cancellationToken).ConfigureAwait(false);
            var r = (int)(row - block.FirstRow);
            foreach (var c in columns)
            {
                if (block.IsTruncated(r, c))
                {
                    return true;
                }
            }
        }
        return false;
    }

    public static async Task<GridExportData> ReadAsync(
        IRowSource source,
        IReadOnlyList<long> rows,
        IReadOnlyList<(int Column, GridExportColumn Info)> columns,
        string tableName,
        IReadOnlyList<string> keyColumns,
        bool blobAsText,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(columns);
        var values = new List<IReadOnlyList<string?>>(rows.Count);
        foreach (var row in rows)
        {
            var block = await source.GetBlockAsync((int)(row / source.BlockSize), cancellationToken).ConfigureAwait(false);
            var r = (int)(row - block.FirstRow);
            values.Add(columns.Select(c => GridExportValues.Text(block, r, c.Column, blobAsText)).ToList());
        }
        return new GridExportData(columns.Select(c => c.Info).ToList(), values, tableName, keyColumns);
    }
}
