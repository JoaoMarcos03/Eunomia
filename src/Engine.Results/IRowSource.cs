using System.Diagnostics.CodeAnalysis;

namespace Eunomia.Engine.Results;

/// <summary>
/// Fonte de linhas por índice (secções 11 e 15.1). O grid lê só blocos residentes, sem bloquear
/// (<see cref="TryGetBlock"/>), e pede ranges de forma assíncrona; quando chegam blocos novos,
/// <see cref="BlocksAvailable"/> é disparado numa thread qualquer.
/// </summary>
public interface IRowSource
{
    ResultSchema Schema { get; }

    RowCountState RowCount { get; }

    /// <summary>Linhas por bloco. O bloco <c>b</c> contém as linhas <c>[b * BlockSize, (b + 1) * BlockSize)</c>.</summary>
    int BlockSize { get; }

    /// <summary>Devolve o bloco se estiver residente. Nunca bloqueia nem faz I/O.</summary>
    bool TryGetBlock(int blockIndex, [NotNullWhen(true)] out RowBlock? block);

    /// <summary>Devolve o bloco, carregando-o (ou refazendo-o) se não estiver residente.</summary>
    ValueTask<RowBlock> GetBlockAsync(int blockIndex, CancellationToken cancellationToken = default);

    /// <summary>
    /// Garante que as linhas <c>[firstRow, firstRow + count)</c> ficam residentes e protegidas de despejo
    /// até ao próximo pedido (visível + prefetch). Termina quando todos os blocos do range estiverem residentes.
    /// </summary>
    Task RequestRangeAsync(long firstRow, int count, CancellationToken cancellationToken = default);

    /// <summary>Blocos novos residentes. Pode ser disparado fora da UI thread.</summary>
    event EventHandler<BlocksAvailableEventArgs>? BlocksAvailable;
}

public sealed class BlocksAvailableEventArgs(int firstBlock, int lastBlock) : EventArgs
{
    public int FirstBlock { get; } = firstBlock;

    public int LastBlock { get; } = lastBlock;
}

public static class RowSourceExtensions
{
    public static long TotalRows(this IRowSource source) => source.RowCount.Count;

    public static int BlockCount(this IRowSource source) =>
        (int)((source.RowCount.Count + source.BlockSize - 1) / source.BlockSize);

    /// <summary>Bloco e linha local de uma linha global, se o bloco estiver residente.</summary>
    public static bool TryGetRow(this IRowSource source, long row, [NotNullWhen(true)] out RowBlock? block, out int localRow)
    {
        var blockIndex = (int)(row / source.BlockSize);
        localRow = (int)(row - (long)blockIndex * source.BlockSize);
        return source.TryGetBlock(blockIndex, out block);
    }
}
