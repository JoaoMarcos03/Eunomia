using Eunomia.Engine.Results.Synthetic;

namespace Eunomia.Engine.Results.Tests;

public sealed class SyntheticRowSourceTests
{
    [Fact]
    public void ByDefaultHasAMillionRowsBySixtyColumns()
    {
        var source = new SyntheticRowSource();

        Assert.Equal(new RowCountState(RowCountKind.Exact, 1_000_000), source.RowCount);
        Assert.Equal(60, source.Schema.Count);
        Assert.Equal(977, source.BlockCount());
    }

    [Fact]
    public void SchemaHasMixedTypesAndPkOnTheFirstColumn()
    {
        var schema = new SyntheticRowSource().Schema;

        Assert.True(schema[0].IsPrimaryKey);
        Assert.False(schema[0].IsNullable);
        var types = schema.Columns.Select(c => c.NativeType).ToHashSet();
        foreach (var expected in new[] { "INT", "BIGINT", "DECIMAL(12,2)", "DOUBLE", "DATETIME", "DATE", "VARCHAR(30)", "VARCHAR(255)", "TEXT" })
        {
            Assert.Contains(expected, types);
        }
        Assert.Contains(schema.Columns, c => !c.IsNullable && !c.IsPrimaryKey);
    }

    [Fact]
    public void ValuesAreDeterministic()
    {
        var a = new SyntheticRowSource().BuildBlock(123);
        var b = new SyntheticRowSource().BuildBlock(123);
        var formatter = new DisplayFormatter(System.Globalization.CultureInfo.InvariantCulture);

        for (var c = 0; c < a.Schema.Count; c++)
        {
            for (var r = 0; r < a.RowCount; r += 97)
            {
                Assert.Equal(formatter.Format(a, r, c), formatter.Format(b, r, c));
            }
        }
    }

    [Fact]
    public void DifferentSeedGivesDifferentValues()
    {
        var formatter = new DisplayFormatter(System.Globalization.CultureInfo.InvariantCulture);
        var a = new SyntheticRowSource(seed: 1).BuildBlock(0);
        var b = new SyntheticRowSource(seed: 2).BuildBlock(0);

        var differences = Enumerable.Range(0, a.RowCount).Count(r => formatter.Format(a, r, 1) != formatter.Format(b, r, 1));
        Assert.True(differences > a.RowCount / 2);
    }

    [Fact]
    public void IdIsRowNumberPlusOne()
    {
        var block = new SyntheticRowSource().BuildBlock(5);

        Assert.Equal(5 * 1024 + 1, block.GetInt64(0, 0));
        Assert.Equal(5 * 1024 + 1024, block.GetInt64(1023, 0));
    }

    [Fact]
    public void NullableColumnsHaveNullsAndNotNullOnesDoNot()
    {
        var source = new SyntheticRowSource();
        var block = source.BuildBlock(0);

        for (var c = 0; c < source.Schema.Count; c++)
        {
            var nulls = Enumerable.Range(0, block.RowCount).Count(r => block.IsNull(r, c));
            if (source.Schema[c].IsNullable)
            {
                // ~8% of 1024, with a wide margin.
                Assert.InRange(nulls, 30, 150);
            }
            else
            {
                Assert.Equal(0, nulls);
            }
        }
    }

    [Fact]
    public void LongTextIsCutAt256AndFlagged()
    {
        var source = new SyntheticRowSource();
        var block = source.BuildBlock(0);
        var textColumn = source.Schema.Columns.ToList().FindIndex(c => c.NativeType == "TEXT");

        var truncated = 0;
        for (var r = 0; r < block.RowCount; r++)
        {
            if (block.IsNull(r, textColumn))
            {
                continue;
            }
            var text = block.GetString(r, textColumn);
            Assert.True(text.Length <= SyntheticRowSource.TextTruncateLength);
            if (block.IsTruncated(r, textColumn))
            {
                Assert.Equal(SyntheticRowSource.TextTruncateLength, text.Length);
                truncated++;
            }
        }
        Assert.True(truncated > 100);
    }

    [Fact]
    public void VarcharHonoursTheTypeLength()
    {
        var source = new SyntheticRowSource();
        var block = source.BuildBlock(3);
        var shortColumn = source.Schema.Columns.ToList().FindIndex(c => c.NativeType == "VARCHAR(30)");
        var longColumn = source.Schema.Columns.ToList().FindIndex(c => c.NativeType == "VARCHAR(255)");

        for (var r = 0; r < block.RowCount; r++)
        {
            Assert.True(block.GetString(r, shortColumn).Length <= 30);
            Assert.True(block.GetString(r, longColumn).Length <= 255);
        }
    }

    [Fact]
    public void LastBlockHasOnlyTheRemainingRows()
    {
        var source = new SyntheticRowSource(rows: 2500, blockSize: 1024);

        Assert.Equal(3, source.BlockCount());
        Assert.Equal(2500 - 2048, source.BuildBlock(2).RowCount);
    }

    [Fact]
    public async Task RequestRangeLeavesTheBlocksResident()
    {
        var source = new SyntheticRowSource();
        var available = new List<int>();
        source.BlocksAvailable += (_, e) => { lock (available) { available.Add(e.FirstBlock); } };

        Assert.False(source.TryGetBlock(10, out _));
        await source.RequestRangeAsync(10 * 1024 + 500, 2000, TestContext.Current.CancellationToken);

        Assert.True(source.TryGetBlock(10, out _));
        Assert.True(source.TryGetBlock(11, out _));
        Assert.True(source.TryGetBlock(12, out _));
        Assert.False(source.TryGetBlock(13, out _));
        Assert.True(source.TryGetRow(11 * 1024 + 7, out var block, out var local));
        Assert.Equal(11, block.BlockIndex);
        Assert.Equal(7, local);
        lock (available)
        {
            Assert.Equal([10, 11, 12], available.Order());
        }
    }

    [Fact]
    public async Task ConcurrentRequestsForTheSameBlockLoadOnce()
    {
        var source = new SyntheticRowSource(latency: TimeSpan.FromMilliseconds(50));
        var ct = TestContext.Current.CancellationToken;

        var tasks = Enumerable.Range(0, 8).Select(_ => source.GetBlockAsync(4, ct).AsTask()).ToArray();
        var blocks = await Task.WhenAll(tasks);

        Assert.All(blocks, b => Assert.Same(blocks[0], b));
        Assert.Equal(1, source.Store.LoadCount);
    }

    [Fact]
    public async Task BudgetEvictsUnpinnedBlocksAndRebuildsThemIdentically()
    {
        var oneBlock = new SyntheticRowSource().BuildBlock(0).ByteSize;
        var source = new SyntheticRowSource(budgetBytes: oneBlock * 3);
        var ct = TestContext.Current.CancellationToken;
        var formatter = new DisplayFormatter(System.Globalization.CultureInfo.InvariantCulture);

        await source.RequestRangeAsync(0, 1024, ct);
        Assert.True(source.TryGetBlock(0, out var original));
        var before = formatter.Format(original, 17, 5);

        for (var b = 1; b <= 6; b++)
        {
            await source.RequestRangeAsync(b * 1024L, 1024, ct);
        }

        Assert.True(source.Store.ResidentBytes <= oneBlock * 3 + oneBlock / 2);
        Assert.False(source.TryGetBlock(0, out _));
        Assert.True(source.TryGetBlock(6, out _));

        var again = await source.GetBlockAsync(0, ct);
        Assert.Equal(before, formatter.Format(again, 17, 5));
    }

    [Fact]
    public async Task PinnedBlocksAreNotEvicted()
    {
        var oneBlock = new SyntheticRowSource().BuildBlock(0).ByteSize;
        var source = new SyntheticRowSource(budgetBytes: oneBlock);
        var ct = TestContext.Current.CancellationToken;

        // A range of 3 blocks above a budget of 1: all 3 stay (visible + prefetch is not evicted).
        await source.RequestRangeAsync(0, 3 * 1024, ct);

        Assert.True(source.TryGetBlock(0, out _));
        Assert.True(source.TryGetBlock(1, out _));
        Assert.True(source.TryGetBlock(2, out _));
    }

    [Fact]
    public async Task CancellingARequestDoesNotCancelTheSharedLoad()
    {
        var source = new SyntheticRowSource(latency: TimeSpan.FromMilliseconds(100));
        using var cts = new CancellationTokenSource();

        var cancelled = source.GetBlockAsync(2, cts.Token).AsTask();
        var other = source.GetBlockAsync(2, TestContext.Current.CancellationToken).AsTask();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var block = await other;
        Assert.Equal(2, block.BlockIndex);
    }
}

public sealed class FullValueTests
{
    [Fact]
    public async Task FullValueHasNoSeparatorOrTruncation()
    {
        var source = new SyntheticRowSource();
        var ct = TestContext.Current.CancellationToken;
        var block = source.BuildBlock(0);
        var textColumn = source.Schema.Columns.ToList().FindIndex(c => c.NativeType == "TEXT");
        var row = Enumerable.Range(0, block.RowCount).First(r => block.IsTruncated(r, textColumn));

        Assert.Equal("1", await source.GetFullTextAsync(0, 0, ct));
        Assert.Equal("100000", await source.GetFullTextAsync(99_999, 0, ct));
        var full = await source.GetFullTextAsync(row, textColumn, ct);
        Assert.NotNull(full);
        Assert.True(full.Length > SyntheticRowSource.TextTruncateLength);
        Assert.StartsWith(block.GetString(row, textColumn), full, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FullValueOfNullIsNull()
    {
        var source = new SyntheticRowSource();
        var block = source.BuildBlock(0);
        var row = Enumerable.Range(0, block.RowCount).First(r => block.IsNull(r, 1));

        Assert.Null(await source.GetFullTextAsync(row, 1, TestContext.Current.CancellationToken));
    }
}
