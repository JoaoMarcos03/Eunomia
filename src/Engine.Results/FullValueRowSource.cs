using System.Diagnostics.CodeAnalysis;

namespace Eunomia.Engine.Results;

/// <summary>
/// Um source de linhas com quem sabe ir buscar os valores completos das células cortadas (o browser do Data tab): as
/// vistas por cima (projeção, filtro local) e o grid chegam aos valores completos para o Ctrl+C e a edição.
/// </summary>
public sealed class FullValueRowSource(IRowSource inner, IFullValueSource values) : IRowSource, IFullValueSource
{
    public IRowSource Inner { get; } = inner ?? throw new ArgumentNullException(nameof(inner));

    public ResultSchema Schema => Inner.Schema;

    public RowCountState RowCount => Inner.RowCount;

    public int BlockSize => Inner.BlockSize;

    public event EventHandler<BlocksAvailableEventArgs>? BlocksAvailable
    {
        add => Inner.BlocksAvailable += value;
        remove => Inner.BlocksAvailable -= value;
    }

    public bool TryGetBlock(int blockIndex, [NotNullWhen(true)] out RowBlock? block) => Inner.TryGetBlock(blockIndex, out block);

    public ValueTask<RowBlock> GetBlockAsync(int blockIndex, CancellationToken cancellationToken = default) => Inner.GetBlockAsync(blockIndex, cancellationToken);

    public Task RequestRangeAsync(long firstRow, int count, CancellationToken cancellationToken = default) => Inner.RequestRangeAsync(firstRow, count, cancellationToken);

    public ValueTask<string?> GetFullTextAsync(long row, int column, CancellationToken cancellationToken = default) =>
        (values ?? throw new InvalidOperationException()).GetFullTextAsync(row, column, cancellationToken);
}
