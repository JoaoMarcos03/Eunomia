using System.Text.RegularExpressions;

namespace Eunomia.Engine.Results;

/// <summary>
/// Procura no grid (anexo C §4.7): percorre as células de todas as linhas carregadas, coluna a coluna, a partir de uma
/// posição (incluída), para a frente ou para trás, comparando o texto de display; NULL não conta. Corre fora da UI
/// thread e é cancelável.
/// </summary>
public static class RowSearch
{
    public static Task<(long Row, int Column)?> FindAsync(
        IRowSource source, DisplayFormatter formatter, Regex regex, long startRow, int startColumn, bool backward, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(formatter);
        ArgumentNullException.ThrowIfNull(regex);
        return Task.Run<(long, int)?>(async () =>
        {
            var rows = source.RowCount.Count;
            var columns = source.Schema.Count;
            if (rows == 0 || columns == 0)
            {
                return null;
            }
            var row = Math.Clamp(startRow, 0, rows - 1);
            var column = Math.Clamp(startColumn, 0, columns - 1);
            RowBlock? block = null;
            while (row >= 0 && row < rows)
            {
                var blockIndex = (int)(row / source.BlockSize);
                if (block is null || block.BlockIndex != blockIndex)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    block = await source.GetBlockAsync(blockIndex, cancellationToken).ConfigureAwait(false);
                }
                var r = (int)(row - block.FirstRow);
                for (; column >= 0 && column < columns; column += backward ? -1 : 1)
                {
                    if (!block.IsNull(r, column) && regex.IsMatch(formatter.Format(block, r, column)))
                    {
                        return (row, column);
                    }
                }
                row += backward ? -1 : 1;
                column = backward ? columns - 1 : 0;
            }
            return null;
        }, cancellationToken);
    }
}
