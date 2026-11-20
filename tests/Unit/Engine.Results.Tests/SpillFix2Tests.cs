namespace Eunomia.Engine.Results.Tests;

/// <summary>Spill quota under the global budget and blocks arriving after the result closed.</summary>
public sealed class SpillFix2Tests : IDisposable
{
    private static readonly ResultSchema Schema = new(
    [
        new ColumnInfo("id", "INT", TypeCategory.Integer, StorageKind.Int64, IsPrimaryKey: true, IsNullable: false),
        new ColumnInfo("name", "TEXT", TypeCategory.Text, StorageKind.String, false, true),
    ]);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "eunomia-spill2-" + Guid.NewGuid().ToString("N")[..8]);

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
            b.SetString(r, 1, new string('x', 200) + id, truncated: false);
        }
        return b.Build();
    }

    [Fact]
    public void GlobalBudgetDoesNotSpillPastTheDiskQuota()
    {
        var oneBlock = Block(0, 64).ByteSize;
        // The global budget wants everything on disk; the quota only allows two blocks there.
        var pool = new ResultMemoryPool(oneBlock);
        var storage = new ResultStorage(BudgetBytes: oneBlock * 100, SpillDirectory: _directory, SpillQuotaBytes: oneBlock * 2) { Pool = pool };
        using var source = new MaterializedRowSource(Schema, 64, storage);

        for (var i = 0; i < 6; i++)
        {
            source.Append(Block(i, 64));
        }

        Assert.True(source.SpilledBytes > 0);
        Assert.True(source.SpilledBytes <= oneBlock * 2, $"{source.SpilledBytes} > {oneBlock * 2}");
    }

    [Fact]
    public void BlockAfterDisposeIsIgnored()
    {
        var pool = new ResultMemoryPool();
        var source = new MaterializedRowSource(Schema, 64, new ResultStorage(SpillDirectory: _directory) { Pool = pool });
        source.Append(Block(0, 64));
        source.Dispose();

        source.Append(Block(1, 64));

        Assert.Equal(0, pool.TotalBytes);
    }
}
