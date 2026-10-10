using System.Text.RegularExpressions;

namespace Eunomia.Engine.Results.Tests;

/// <summary>Filter panel local (anexo D B11; secção 11.5; FAM-G14).</summary>
public sealed class FilteredRowSourceTests
{
    private static readonly ResultSchema Schema = new(
    [
        new ColumnInfo("id", "INT", TypeCategory.Integer, StorageKind.Int64, IsPrimaryKey: true, IsNullable: false),
        new ColumnInfo("name", "VARCHAR(20)", TypeCategory.Text, StorageKind.String, false, true),
    ]);

    private static MaterializedRowSource Source(int rows, int blockSize)
    {
        var source = new MaterializedRowSource(Schema, blockSize);
        for (var b = 0; b * blockSize < rows; b++)
        {
            var count = Math.Min(blockSize, rows - (b * blockSize));
            var builder = new RowBlockBuilder(Schema, b, (long)b * blockSize, count);
            for (var r = 0; r < count; r++)
            {
                var id = (b * blockSize) + r;
                builder.SetInt64(r, 0, id * 1000);
                if (id % 10 == 3)
                {
                    builder.SetNull(r, 1);
                }
                else
                {
                    builder.SetString(r, 1, id % 2 == 0 ? "Ana " + id : "rui " + id);
                }
            }
            source.Append(builder.Build());
        }
        source.Complete();
        return source;
    }

    private static Regex Rx(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    public async Task FicamAsLinhasQuePassamPorOrdem()
    {
        var filtered = await FilteredRowSource.CreateAsync(Source(100, 16), Rx("^ana"), TestContext.Current.CancellationToken);

        Assert.Equal(50, filtered.MatchCount);
        Assert.Equal(100, filtered.TotalRows);
        Assert.Equal(new RowCountState(RowCountKind.Final, 50), filtered.RowCount);
        Assert.True(filtered.TryGetBlock(1, out var block));
        Assert.Equal(16, block.FirstRow);
        Assert.Equal("Ana 32", block.GetString(0, 1));
        Assert.Equal(32, filtered.InnerRow(16));
        Assert.False(filtered.TryGetBlock(4, out _));
    }

    [Fact]
    public async Task ComparaNumerosSemSeparadoresONumeroDaLinhaENull()
    {
        var source = Source(30, 8);
        // 25000 sem separador de milhares (o grid mostra 25,000).
        Assert.Equal(1, (await FilteredRowSource.CreateAsync(source, Rx("^25000$"), TestContext.Current.CancellationToken)).MatchCount);
        // Número da linha (#): a linha 30 é a única com "30" só no número.
        var byNumber = await FilteredRowSource.CreateAsync(source, Rx("^30$"), TestContext.Current.CancellationToken);
        Assert.Equal(1, byNumber.MatchCount);
        Assert.Equal(29, byNumber.InnerRow(0));
        // NULL aparece como "(NULL)".
        Assert.Equal(3, (await FilteredRowSource.CreateAsync(source, Rx(@"\(NULL\)"), TestContext.Current.CancellationToken)).MatchCount);
    }

    [Fact]
    public async Task FuncionaSobreBlocosEmSpill()
    {
        var dir = Path.Combine(Path.GetTempPath(), "eunomia-filter-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using var source = new MaterializedRowSource(Schema, 16, new ResultStorage(BudgetBytes: 1, SpillDirectory: dir));
            var full = Source(64, 16);
            for (var b = 0; b < 4; b++)
            {
                source.Append(full.TryGetBlock(b, out var block) ? block : throw new InvalidOperationException());
            }
            source.Complete();

            var filtered = await FilteredRowSource.CreateAsync(source, Rx("rui"), TestContext.Current.CancellationToken);
            var last = await filtered.GetBlockAsync(1, TestContext.Current.CancellationToken);

            Assert.Equal(25, filtered.MatchCount);
            Assert.StartsWith("rui", last.GetString(0, 1), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task OrdenacaoLocalPorColunaComNullPrimeiro()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = Source(30, 8);

        var byIdDesc = await FilteredRowSource.SortAsync(source, 0, descending: true, ct);
        Assert.Equal(new RowCountState(RowCountKind.Final, 30), byIdDesc.RowCount);
        Assert.True(byIdDesc.TryGetBlock(0, out var first));
        Assert.Equal(29_000, first.GetInt64(0, 0));
        Assert.Equal(22_000, first.GetInt64(7, 0));

        // Texto: NULL primeiro, depois sem maiúsculas e numérico só quando os dois textos são números.
        var byName = await FilteredRowSource.SortAsync(source, 1, descending: false, ct);
        var block = await byName.GetBlockAsync(0, ct);
        Assert.True(block.IsNull(0, 1));
        Assert.True(block.IsNull(2, 1));
        Assert.Equal("Ana 0", block.GetString(3, 1));
        Assert.Equal("Ana 10", block.GetString(4, 1));

        Assert.True(FilteredRowSource.CompareText("9", "10") < 0);
        Assert.True(FilteredRowSource.CompareText("-2", "1") < 0);
        Assert.True(FilteredRowSource.CompareText("abc", "ABD") < 0);
        Assert.True(FilteredRowSource.CompareText("9a", "10a") > 0);
    }

    [Fact]
    public async Task ProcuraNoGridParaAFrenteEParaTras()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = Source(30, 8);
        var formatter = new DisplayFormatter(System.Globalization.CultureInfo.InvariantCulture, thousandsSeparator: false);

        Assert.Equal((12L, 1), await RowSearch.FindAsync(source, formatter, Rx("ana 12"), 0, 0, backward: false, ct));
        Assert.Equal((12L, 0), await RowSearch.FindAsync(source, formatter, Rx("^12000$"), 0, 0, backward: false, ct));
        Assert.Equal((10L, 1), await RowSearch.FindAsync(source, formatter, Rx("^ana 1"), 11, 1, backward: true, ct));
        Assert.Null(await RowSearch.FindAsync(source, formatter, Rx("NULL"), 0, 0, backward: false, ct));
        Assert.Equal((22L, 1), await RowSearch.FindAsync(source, formatter, Rx("ana 2"), 21, 0, backward: false, ct));
    }

    [Fact]
    public async Task OrdenacaoCancelada()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FilteredRowSource.SortAsync(Source(30, 8), 0, false, cts.Token));
    }
}
