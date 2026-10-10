using System.Diagnostics;
using Eunomia.UI.Controls.Graph.Layout;

namespace Eunomia.UI.Controls.Graph.Tests;

/// <summary>Layered layout (SV-09; section 17.5): no overlaps, left → right, components, time.</summary>
public sealed class LayeredLayoutTests
{
    private static readonly LayeredLayout Layout = new();

    private static bool Overlap(LayoutPoint a, LayoutSize sa, LayoutPoint b, LayoutSize sb) =>
        a.X < b.X + sb.Width && b.X < a.X + sa.Width && a.Y < b.Y + sb.Height && b.Y < a.Y + sa.Height;

    private static void AssertNoOverlap(IReadOnlyList<LayoutPoint> points, IReadOnlyList<LayoutSize> sizes)
    {
        for (var i = 0; i < points.Count; i++)
        {
            for (var j = i + 1; j < points.Count; j++)
            {
                Assert.False(Overlap(points[i], sizes[i], points[j], sizes[j]), $"Nós {i} e {j} sobrepõem-se.");
            }
        }
    }

    [Fact]
    public void CadeiaDaEsquerdaParaADireitaSemSobreposicoes()
    {
        LayoutSize[] sizes = [new(120, 80), new(100, 200), new(140, 60), new(120, 120)];
        // 0 → 1 → 2, 0 → 3: the parent stays to the left of its children.
        LayoutEdge[] edges = [new(0, 1), new(1, 2), new(0, 3)];
        var points = Layout.Layout(sizes, edges, CancellationToken.None);

        AssertNoOverlap(points, sizes);
        foreach (var e in edges)
        {
            Assert.True(points[e.From].X + sizes[e.From].Width <= points[e.To].X, $"{e.From} devia estar à esquerda de {e.To}.");
        }
        Assert.Equal(0, points.Min(p => p.X));
        Assert.Equal(0, points.Min(p => p.Y));
    }

    [Fact]
    public void CiclosArestasRepetidasEAutoLigacoes()
    {
        LayoutSize[] sizes = [new(100, 50), new(100, 50), new(100, 50)];
        LayoutEdge[] edges = [new(0, 1), new(1, 2), new(2, 0), new(1, 1), new(0, 1), new(5, 0)];
        var points = Layout.Layout(sizes, edges, CancellationToken.None);

        AssertNoOverlap(points, sizes);
        Assert.Equal(3, points.Select(p => p.X).Distinct().Count());
    }

    [Fact]
    public void ComponentesSeparadosEIsoladosNumaGrelha()
    {
        var sizes = Enumerable.Range(0, 12).Select(i => new LayoutSize(100 + (i * 3), 40 + (i * 5))).ToArray();
        LayoutEdge[] edges = [new(0, 1), new(1, 2), new(3, 4), new(5, 4)];
        var points = Layout.Layout(sizes, edges, CancellationToken.None);

        AssertNoOverlap(points, sizes);
        // The isolated ones (6–11) go below the connected components.
        var bottomOfComponents = Enumerable.Range(0, 6).Max(i => points[i].Y + sizes[i].Height);
        Assert.All(Enumerable.Range(6, 6), i => Assert.True(points[i].Y >= bottomOfComponents));
    }

    [Fact]
    public void FilhoAlinhadoComOPaiQuandoHaEspaco()
    {
        LayoutSize[] sizes = [new(100, 300), new(100, 60)];
        var points = Layout.Layout(sizes, [new LayoutEdge(0, 1)], CancellationToken.None);

        // The child's centre aligns with the parent's centre.
        Assert.Equal(points[0].Y + 150, points[1].Y + 30, 1);
    }

    [Fact]
    public void DeterministicoECancelavel()
    {
        var (sizes, edges) = Schema(200, seed: 7);
        Assert.Equal(Layout.Layout(sizes, edges, CancellationToken.None), Layout.Layout(sizes, edges, CancellationToken.None));
        Assert.ThrowsAny<OperationCanceledException>(() => Layout.Layout(sizes, edges, new CancellationToken(canceled: true)));
    }

    [Theory]
    [InlineData(300)]
    [InlineData(1000)]
    public void EsquemasGrandesSemSobreposicoesEDentroDoTempo(int tables)
    {
        var (sizes, edges) = Schema(tables, seed: tables);
        Layout.Layout(sizes, edges, CancellationToken.None);
        var clock = Stopwatch.StartNew();
        var points = Layout.Layout(sizes, edges, CancellationToken.None);
        clock.Stop();

        AssertNoOverlap(points, sizes);
        TestContext.Current.TestOutputHelper?.WriteLine($"{tables} tabelas, {edges.Length} FKs: {clock.ElapsedMilliseconds} ms");
        // Section 17.5: 300 tables ≤ 2 s (with room for slow machines and Debug, 1,000 too).
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(tables <= 300 ? 2 : 6), $"{clock.ElapsedMilliseconds} ms");
    }

    /// <summary>Synthetic schema: tables with 3 to 30 columns, ~1.3 FKs per table to earlier tables, some isolated.</summary>
    internal static (LayoutSize[] Sizes, LayoutEdge[] Edges) Schema(int tables, int seed)
    {
        var random = new Random(seed);
        var sizes = Enumerable.Range(0, tables).Select(_ => new LayoutSize(160 + random.Next(80), 26 + (18 * random.Next(3, 31)))).ToArray();
        var edges = new List<LayoutEdge>();
        for (var t = 1; t < tables; t++)
        {
            if (random.NextDouble() < 0.1)
            {
                continue;
            }
            var fks = random.Next(0, 3);
            for (var f = 0; f < fks; f++)
            {
                // Parents more likely among the "central" tables (the first ones).
                var parent = (int)(Math.Pow(random.NextDouble(), 2) * t);
                edges.Add(new LayoutEdge(parent, t));
            }
        }
        return (sizes, [.. edges]);
    }
}
