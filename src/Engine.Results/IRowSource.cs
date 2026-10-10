using System.Diagnostics.CodeAnalysis;

namespace Eunomia.Engine.Results;

/// <summary>
/// Row source by index (sections 11 and 15.1). The grid only reads resident blocks, without blocking
/// (<see cref="TryGetBlock"/>), and requests ranges asynchronously; when new blocks arrive,
/// <see cref="BlocksAvailable"/> is raised on any thread.
/// </summary>
public interface IRowSource
{
    ResultSchema Schema { get; }

    RowCountState RowCount { get; }

    /// <summary>Rows per block. Block <c>b</c> holds rows <c>[b * BlockSize, (b + 1) * BlockSize)</c>.</summary>
    int BlockSize { get; }

    /// <summary>Returns the block if it is resident. Never blocks or does I/O.</summary>
    bool TryGetBlock(int blockIndex, [NotNullWhen(true)] out RowBlock? block);

    /// <summary>Returns the block, loading it (or rebuilding it) if it is not resident.</summary>
    ValueTask<RowBlock> GetBlockAsync(int blockIndex, CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes sure rows <c>[firstRow, firstRow + count)</c> stay resident and protected from eviction
    /// until the next request (visible + prefetch). Completes when every block of the range is resident.
    /// </summary>
    Task RequestRangeAsync(long firstRow, int count, CancellationToken cancellationToken = default);

    /// <summary>New resident blocks. May be raised off the UI thread.</summary>
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

    /// <summary>Block and local row of a global row, if the block is resident.</summary>
    public static bool TryGetRow(this IRowSource source, long row, [NotNullWhen(true)] out RowBlock? block, out int localRow)
    {
        var blockIndex = (int)(row / source.BlockSize);
        localRow = (int)(row - (long)blockIndex * source.BlockSize);
        return source.TryGetBlock(blockIndex, out block);
    }
}
