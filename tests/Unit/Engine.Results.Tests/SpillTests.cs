namespace Eunomia.Engine.Results.Tests;

/// <summary>Budget and spill of the <see cref="MaterializedRowSource"/> (section 11.3, PR-17).</summary>
public sealed class SpillTests : IDisposable
{
    private static readonly ResultSchema Schema = new(
    [
        new ColumnInfo("id", "INT", TypeCategory.Integer, StorageKind.Int64, IsPrimaryKey: true, IsNullable: false),
        new ColumnInfo("ratio", "DOUBLE", TypeCategory.Real, StorageKind.Double, false, true),
        new ColumnInfo("name", "TEXT", TypeCategory.Text, StorageKind.String, false, true),
    ]);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "eunomia-spill-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static RowBlock Block(int index, int rows)
    {
        var b = new RowBlockBuilder(Schema, index, (long)index * rows, rows);
        for (var r = 0; r < rows; r++)
        {
            var id = (index * rows) + r;
            b.SetInt64(r, 0, id);
            if (r % 7 == 0)
            {
                b.SetNull(r, 1);
            }
            else
            {
                b.SetDouble(r, 1, id / 3.0);
            }
            b.SetString(r, 2, new string('x', 200) + id, truncated: r % 5 == 0);
        }
        return b.Build();
    }

    [Fact]
    public async Task OrcamentoGlobalDespejaOsMaioresEDisposeTiraDoTotal()
    {
        var oneBlock = Block(0, 256).ByteSize;
        // Each result would accept 10 blocks; the global budget only allows 6 in total.
        var pool = new ResultMemoryPool(oneBlock * 6);
        var storage = new ResultStorage(BudgetBytes: oneBlock * 10, SpillDirectory: _directory) { Pool = pool };
        using var big = new MaterializedRowSource(Schema, 256, storage);
        var small = new MaterializedRowSource(Schema, 256, storage);
        for (var i = 0; i < 3; i++)
        {
            small.Append(Block(i, 256));
        }
        for (var i = 0; i < 8; i++)
        {
            big.Append(Block(i, 256));
        }

        Assert.True(pool.TotalBytes <= oneBlock * 6, $"{pool.TotalBytes} > {oneBlock * 6}");
        Assert.Equal(pool.TotalBytes, big.ByteSize + small.ByteSize);
        // The largest one spilled; the small one stayed whole in memory.
        Assert.True(big.SpilledBytes > 0);
        Assert.Equal(Enumerable.Range(0, 3).Sum(i => Block(i, 256).ByteSize), small.ByteSize);
        var back = await big.GetBlockAsync(0, TestContext.Current.CancellationToken);
        Assert.Equal(0, back.GetInt64(0, 0));
        Assert.True(pool.TotalBytes <= oneBlock * 6);

        // Closing a result takes it out of the total.
        var before = pool.TotalBytes;
        var smallBytes = small.ByteSize;
        small.Dispose();
        Assert.Equal(before - smallBytes, pool.TotalBytes);
        small.Dispose();
        Assert.Equal(before - smallBytes, pool.TotalBytes);
    }

    [Fact]
    public async Task AcimaDoOrcamentoVaiParaDiscoEVoltaIgual()
    {
        var oneBlock = Block(0, 256).ByteSize;
        using var source = new MaterializedRowSource(Schema, 256, new ResultStorage(BudgetBytes: oneBlock * 3, SpillDirectory: _directory));
        for (var i = 0; i < 10; i++)
        {
            source.Append(Block(i, 256));
        }
        source.Complete();

        Assert.True(source.ByteSize <= oneBlock * 3);
        Assert.True(source.SpilledBytes > 0);
        Assert.Single(Directory.GetFiles(_directory));
        Assert.False(source.TryGetBlock(0, out _));

        var back = await source.GetBlockAsync(0, TestContext.Current.CancellationToken);
        var original = Block(0, 256);
        for (var r = 0; r < 256; r++)
        {
            Assert.Equal(original.GetInt64(r, 0), back.GetInt64(r, 0));
            Assert.Equal(original.IsNull(r, 1), back.IsNull(r, 1));
            if (!back.IsNull(r, 1))
            {
                Assert.Equal(original.GetDouble(r, 1), back.GetDouble(r, 1));
            }
            Assert.Equal(original.GetString(r, 2), back.GetString(r, 2));
            Assert.Equal(original.IsTruncated(r, 2), back.IsTruncated(r, 2));
        }
        Assert.Equal(new RowCountState(RowCountKind.Final, 2560), source.RowCount);
    }

    [Fact]
    public async Task IntervaloPedidoPeloGridNaoVaiParaDisco()
    {
        var oneBlock = Block(0, 128).ByteSize;
        using var source = new MaterializedRowSource(Schema, 128, new ResultStorage(BudgetBytes: oneBlock * 2, SpillDirectory: _directory));
        source.Append(Block(0, 128));
        await source.RequestRangeAsync(0, 128, TestContext.Current.CancellationToken);
        for (var i = 1; i < 6; i++)
        {
            source.Append(Block(i, 128));
        }

        Assert.True(source.TryGetBlock(0, out _));
        Assert.False(source.TryGetBlock(1, out _));
    }

    [Fact]
    public void SemPastaDeSpillFicaTudoEmMemoria()
    {
        using var source = new MaterializedRowSource(Schema, 64);
        for (var i = 0; i < 5; i++)
        {
            source.Append(Block(i, 64));
        }

        Assert.Equal(0, source.SpilledBytes);
        Assert.True(source.TryGetBlock(0, out _));
    }

    [Fact]
    public void QuotaDeDiscoEsgotadaParaOFetch()
    {
        var oneBlock = Block(0, 64).ByteSize;
        using var source = new MaterializedRowSource(Schema, 64, new ResultStorage(oneBlock, _directory, SpillQuotaBytes: oneBlock * 2));

        Assert.Throws<ResultStorageFullException>(() =>
        {
            for (var i = 0; i < 10; i++)
            {
                source.Append(Block(i, 64));
            }
        });
        Assert.True(source.RowCount.Count > 0);
    }

    [Fact]
    public void FicheiroDesapareceAoFechar()
    {
        var oneBlock = Block(0, 64).ByteSize;
        var source = new MaterializedRowSource(Schema, 64, new ResultStorage(oneBlock, _directory));
        source.Append(Block(0, 64));
        source.Append(Block(1, 64));
        Assert.Single(Directory.GetFiles(_directory));

        source.Dispose();

        Assert.Empty(Directory.GetFiles(_directory));
    }
}
