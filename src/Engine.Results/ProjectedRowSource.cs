using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Eunomia.Engine.Results;

/// <summary>
/// View of a source with only the visible columns, in the given order (Data tab hidden columns, appendix D B8): blocks
/// are projected without copying data. The full value is requested from the underlying source with the original column index.
/// </summary>
public sealed class ProjectedRowSource : IRowSource, IFullValueSource
{
    private readonly IRowSource _inner;
    private readonly int[] _columns;
    private readonly ConditionalWeakTable<RowBlock, RowBlock> _projected = [];

    public ProjectedRowSource(IRowSource inner, IReadOnlyList<int> columns)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(columns);
        _inner = inner;
        _columns = [.. columns];
        Schema = new ResultSchema(_columns.Select(c => inner.Schema[c]).ToList());
        inner.BlocksAvailable += (_, e) => BlocksAvailable?.Invoke(this, e);
    }

    /// <summary>Underlying source (for editing and export, which need every column).</summary>
    public IRowSource Inner => _inner;

    /// <summary>Column index in the underlying source.</summary>
    public int InnerColumn(int column) => _columns[column];

    public ResultSchema Schema { get; }

    public RowCountState RowCount => _inner.RowCount;

    public int BlockSize => _inner.BlockSize;

    public event EventHandler<BlocksAvailableEventArgs>? BlocksAvailable;

    public bool TryGetBlock(int blockIndex, [NotNullWhen(true)] out RowBlock? block)
    {
        if (_inner.TryGetBlock(blockIndex, out var inner))
        {
            block = Project(inner);
            return true;
        }
        block = null;
        return false;
    }

    public async ValueTask<RowBlock> GetBlockAsync(int blockIndex, CancellationToken cancellationToken = default) =>
        Project(await _inner.GetBlockAsync(blockIndex, cancellationToken).ConfigureAwait(false));

    public Task RequestRangeAsync(long firstRow, int count, CancellationToken cancellationToken = default) =>
        _inner.RequestRangeAsync(firstRow, count, cancellationToken);

    public ValueTask<string?> GetFullTextAsync(long row, int column, CancellationToken cancellationToken = default) =>
        _inner is IFullValueSource full
            ? full.GetFullTextAsync(row, _columns[column], cancellationToken)
            : ValueTask.FromResult<string?>(null);

    private RowBlock Project(RowBlock inner) => _projected.GetValue(inner, b => b.Project(_columns, Schema));
}
