using System.Globalization;
using Eunomia.Engine.Results.Synthetic;

namespace Eunomia.Engine.Results.Tests;

public sealed class FormattedCellCacheTests
{
    private static readonly DisplayFormatter Formatter = new(CultureInfo.InvariantCulture);

    [Fact]
    public void SemBlocoResidenteNaoBloqueiaNemFormata()
    {
        var cache = new FormattedCellCache(new SyntheticRowSource(), Formatter);

        Assert.False(cache.TryGet(0, 0, formatIfResident: true, out _));
    }

    [Fact]
    public async Task ComBlocoResidenteFormataNaHoraSePermitido()
    {
        var source = new SyntheticRowSource();
        await source.RequestRangeAsync(0, 10, TestContext.Current.CancellationToken);
        var cache = new FormattedCellCache(source, Formatter);

        Assert.False(cache.TryGet(0, 0, formatIfResident: false, out _));
        Assert.True(cache.TryGet(0, 0, formatIfResident: true, out var cells));
        Assert.Equal("1", cells[0]);
        Assert.Equal("1,024", cells[1023]);
        Assert.True(cache.TryGet(0, 0, formatIfResident: false, out var again));
        Assert.Same(cells, again);
    }

    [Fact]
    public async Task PrefetchCarregaEFormataEmBackground()
    {
        var source = new SyntheticRowSource();
        var cache = new FormattedCellCache(source, Formatter);
        var done = new TaskCompletionSource();
        var count = 0;
        cache.Formatted += (_, _) =>
        {
            if (Interlocked.Increment(ref count) == 2)
            {
                done.TrySetResult();
            }
        };

        cache.Prefetch(20, 21, [0, 3]);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.True(cache.TryGet(20, 0, false, out var ids));
        Assert.Equal("20,481", ids[0]);
        Assert.True(cache.TryGet(21, 3, false, out _));
        Assert.False(cache.TryGet(21, 4, false, out _));
    }

    [Fact]
    public async Task OrcamentoLimitaAMemoria()
    {
        var source = new SyntheticRowSource();
        await source.RequestRangeAsync(0, 1024, TestContext.Current.CancellationToken);
        var cache = new FormattedCellCache(source, Formatter, budgetBytes: 1024);

        for (var c = 0; c < 60; c++)
        {
            Assert.True(cache.TryGet(0, c, true, out _));
        }
        for (var b = 1; b <= 3; b++)
        {
            await source.RequestRangeAsync(b * 1024L, 1024, TestContext.Current.CancellationToken);
            for (var c = 0; c < 60; c++)
            {
                cache.TryGet(b, c, true, out _);
            }
        }

        // Mantém só as 64 entradas mais recentes quando o orçamento é ultrapassado.
        Assert.False(cache.TryGet(0, 0, false, out _));
        Assert.True(cache.TryGet(3, 59, false, out _));
    }
}
