using Eunomia.Engine.Results;
using Eunomia.UI.Shell;

namespace Eunomia.UI.Headless.Tests;

/// <summary>Data tab export: the full-value re-read is matched to the page rows by key, whatever order it came in.</summary>
public sealed class RowsByKeyTests
{
    private static readonly ResultSchema Schema = new(
    [
        new ColumnInfo("id", "INT", TypeCategory.Integer, StorageKind.Int64, IsPrimaryKey: true, IsNullable: false),
        new ColumnInfo("name", "TEXT", TypeCategory.Text, StorageKind.String, false, true),
    ]);

    private static MaterializedRowSource Source(int blockSize, params long[] ids)
    {
        var source = new MaterializedRowSource(Schema, blockSize);
        for (var b = 0; b * blockSize < ids.Length; b++)
        {
            var count = Math.Min(blockSize, ids.Length - (b * blockSize));
            var builder = new RowBlockBuilder(Schema, b, (long)b * blockSize, count);
            for (var r = 0; r < count; r++)
            {
                var id = ids[(b * blockSize) + r];
                builder.SetInt64(r, 0, id);
                builder.SetString(r, 1, "n" + id);
            }
            source.Append(builder.Build());
        }
        source.Complete();
        return source;
    }

    [Fact(Timeout = UiTimeouts.Default)]
    public async Task MatchesRowsByKeyInAnyOrder()
    {
        var page = Source(2, 10, 20, 30, 40);
        var full = Source(2, 40, 10, 30, 20);
        Assert.Equal([3L, 1L, 2L], await MainWindow.RowsByKeyAsync(page, full, [1, 0, 2], ["ID"]));
    }

    [Fact(Timeout = UiTimeouts.Default)]
    public async Task MissingRowOrNoKeyGivesNull()
    {
        var page = Source(2, 10, 20, 30);
        var full = Source(2, 10, 30);
        Assert.Null(await MainWindow.RowsByKeyAsync(page, full, [0, 1], ["id"]));
        Assert.Null(await MainWindow.RowsByKeyAsync(page, full, [0], []));
        Assert.Null(await MainWindow.RowsByKeyAsync(page, full, [0], ["missing"]));
    }
}
