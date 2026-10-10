using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Eunomia.Engine.Results;

/// <summary>
/// Local view by indexes over another source: the rows that match a regex (filter panel, section 11.5; appendix D B11) or
/// all of them, sorted by a column (<see cref="SortAsync"/>). The scan runs off the UI thread over the loaded rows
/// (including spilled ones) and keeps the indexes; the view's blocks are built on demand, copying
/// only those rows. The regex compares the text of every column and the row number.
/// </summary>
public sealed class FilteredRowSource : IRowSource, IFullValueSource
{
    private readonly IRowSource _inner;
    private readonly long[] _rows;
    private readonly Lock _gate = new();
    private readonly Dictionary<int, RowBlock> _built = [];

    private FilteredRowSource(IRowSource inner, long[] rows, long total)
    {
        _inner = inner;
        _rows = rows;
        TotalRows = total;
        Schema = inner.Schema;
        BlockSize = inner.BlockSize;
    }

    /// <summary>Rows of the underlying source.</summary>
    public long TotalRows { get; }

    public long MatchCount => _rows.LongLength;

    public ResultSchema Schema { get; }

    public int BlockSize { get; }

    public RowCountState RowCount => new(RowCountKind.Final, _rows.LongLength);

    public event EventHandler<BlocksAvailableEventArgs>? BlocksAvailable;

    /// <summary>Row of the underlying source.</summary>
    public long InnerRow(long row) => _rows[row];

    /// <summary>Scans the source (off the UI thread) and returns the view with the matching rows.</summary>
    public static Task<FilteredRowSource> CreateAsync(IRowSource inner, Regex regex, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(regex);
        return Task.Run(async () =>
        {
            // Local formatting off (appendix D B11): no thousands separators.
            var formatter = new DisplayFormatter(CultureInfo.InvariantCulture, thousandsSeparator: false);
            var rows = new List<long>();
            var total = inner.RowCount.Count;
            var blocks = inner.BlockCount();
            for (var b = 0; b < blocks; b++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var block = await inner.GetBlockAsync(b, cancellationToken).ConfigureAwait(false);
                for (var r = 0; r < block.RowCount; r++)
                {
                    if (Matches(block, r, regex, formatter))
                    {
                        rows.Add(block.FirstRow + r);
                    }
                }
            }
            return new FilteredRowSource(inner, [.. rows], total);
        }, cancellationToken);
    }

    /// <summary>
    /// Local sort on a column (query tab result tabs; appendix D B18, P20): sorts the indexes off the UI thread,
    /// by the typed value (numbers, dates) or, for text, like HeidiSQL's <c>CompareAnyNode</c> (numeric when both
    /// texts are numbers, otherwise case-insensitive in the current culture). NULL comes first when ascending. Cancellable.
    /// </summary>
    public static Task<FilteredRowSource> SortAsync(IRowSource inner, int column, bool descending, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfNegative(column);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(column, inner.Schema.Count);
        return Task.Run(async () =>
        {
            var total = inner.RowCount.Count;
            var storage = inner.Schema[column].Storage;
            var nulls = new bool[total];
            var longs = storage is StorageKind.Int64 or StorageKind.Decimal or StorageKind.DateTime ? new long[total] : null;
            var doubles = storage == StorageKind.Double ? new double[total] : null;
            var texts = longs is null && doubles is null ? new string[total] : null;
            var formatter = new DisplayFormatter(CultureInfo.InvariantCulture, thousandsSeparator: false);
            var blocks = inner.BlockCount();
            for (var b = 0; b < blocks; b++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var block = await inner.GetBlockAsync(b, cancellationToken).ConfigureAwait(false);
                for (var r = 0; r < block.RowCount; r++)
                {
                    var row = block.FirstRow + r;
                    if (row >= total)
                    {
                        break;
                    }
                    if (block.IsNull(r, column))
                    {
                        nulls[row] = true;
                    }
                    else if (longs is not null)
                    {
                        longs[row] = block.GetInt64(r, column);
                    }
                    else if (doubles is not null)
                    {
                        doubles[row] = block.GetDouble(r, column);
                    }
                    else
                    {
                        texts![row] = formatter.Format(block, r, column);
                    }
                }
            }
            var rows = new long[total];
            for (var i = 0; i < total; i++)
            {
                rows[i] = i;
            }
            var compared = 0;
            var direction = descending ? -1 : 1;
            try
            {
                Sort();
            }
            catch (InvalidOperationException e) when (e.InnerException is OperationCanceledException canceled)
            {
                // Array.Sort wraps the comparer's exceptions.
                throw canceled;
            }
            return new FilteredRowSource(inner, rows, total);

            void Sort() => Array.Sort(rows, (a, b) =>
            {
                if ((++compared & 0xFFFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                int result;
                if (nulls[a] || nulls[b])
                {
                    result = nulls[a] == nulls[b] ? 0 : nulls[a] ? -1 : 1;
                }
                else if (longs is not null)
                {
                    result = longs[a].CompareTo(longs[b]);
                }
                else if (doubles is not null)
                {
                    result = doubles[a].CompareTo(doubles[b]);
                }
                else
                {
                    result = CompareText(texts![a], texts[b]);
                }
                // Stable: ties keep the server's order.
                return result != 0 ? result * direction : a.CompareTo(b);
            });
        }, cancellationToken);
    }

    /// <summary><c>CompareAnyNode</c>: numeric when both texts are numbers, otherwise case-insensitive in the current culture.</summary>
    public static int CompareText(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (StartsNumeric(a) && StartsNumeric(b)
            && decimal.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            && decimal.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
        {
            return x.CompareTo(y);
        }
        return string.Compare(a, b, CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);
    }

    private static bool StartsNumeric(string text) =>
        text.Length > 0 && (char.IsAsciiDigit(text[0]) || (text[0] == '-' && text.Length > 1 && char.IsAsciiDigit(text[1])));

    private static bool Matches(RowBlock block, int row, Regex regex, DisplayFormatter formatter)
    {
        if (regex.IsMatch((block.FirstRow + row + 1).ToString(CultureInfo.InvariantCulture)))
        {
            return true;
        }
        for (var c = 0; c < block.Schema.Count; c++)
        {
            var text = block.IsNull(row, c) ? DisplayFormatter.NullText : formatter.Format(block, row, c);
            if (regex.IsMatch(text))
            {
                return true;
            }
        }
        return false;
    }

    public bool TryGetBlock(int blockIndex, [NotNullWhen(true)] out RowBlock? block)
    {
        lock (_gate)
        {
            if (_built.TryGetValue(blockIndex, out block))
            {
                return true;
            }
        }
        // Builds right away if the underlying rows are all in memory; otherwise it is left to GetBlockAsync.
        block = Build(blockIndex, b => _inner.TryGetBlock(b, out var resident) ? resident : null);
        return block is not null;
    }

    public async ValueTask<RowBlock> GetBlockAsync(int blockIndex, CancellationToken cancellationToken = default)
    {
        if (TryGetBlock(blockIndex, out var block))
        {
            return block;
        }
        var first = (long)blockIndex * BlockSize;
        var count = (int)Math.Min(BlockSize, _rows.LongLength - first);
        var needed = new Dictionary<int, RowBlock>();
        for (var i = 0; i < count; i++)
        {
            var innerBlock = (int)(_rows[first + i] / _inner.BlockSize);
            if (!needed.ContainsKey(innerBlock))
            {
                needed[innerBlock] = await _inner.GetBlockAsync(innerBlock, cancellationToken).ConfigureAwait(false);
            }
        }
        return Build(blockIndex, b => needed.GetValueOrDefault(b)) ?? throw new InvalidOperationException("Block not available.");
    }

    private RowBlock? Build(int blockIndex, Func<int, RowBlock?> innerBlock)
    {
        var first = (long)blockIndex * BlockSize;
        if (blockIndex < 0 || first >= _rows.LongLength)
        {
            return null;
        }
        var count = (int)Math.Min(BlockSize, _rows.LongLength - first);
        var builder = new RowBlockBuilder(Schema, blockIndex, first, count);
        for (var i = 0; i < count; i++)
        {
            var innerRow = _rows[first + i];
            if (innerBlock((int)(innerRow / _inner.BlockSize)) is not { } source)
            {
                return null;
            }
            builder.CopyRow(source, (int)(innerRow - source.FirstRow), i);
        }
        var block = builder.Build();
        lock (_gate)
        {
            _built[blockIndex] = block;
        }
        return block;
    }

    public async Task RequestRangeAsync(long firstRow, int count, CancellationToken cancellationToken = default)
    {
        if (_rows.Length == 0)
        {
            return;
        }
        var first = (int)(Math.Max(0, firstRow) / BlockSize);
        var last = (int)(Math.Min(_rows.LongLength - 1, Math.Max(0, firstRow + Math.Max(1, count) - 1)) / BlockSize);
        for (var b = first; b <= last; b++)
        {
            if (!TryGetBlock(b, out _))
            {
                await GetBlockAsync(b, cancellationToken).ConfigureAwait(false);
                BlocksAvailable?.Invoke(this, new BlocksAvailableEventArgs(b, b));
            }
        }
    }

    public ValueTask<string?> GetFullTextAsync(long row, int column, CancellationToken cancellationToken = default) =>
        _inner is IFullValueSource full ? full.GetFullTextAsync(_rows[row], column, cancellationToken) : ValueTask.FromResult<string?>(null);
}
