using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Eunomia.UI.Controls.Graph;
using Eunomia.UI.Controls.Graph.Layout;

namespace Eunomia.UI.Headless.Tests;

/// <summary>Canvas do Schema Visualizer (Fase 9; SV-01–SV-04, SV-07, SV-08, SV-13, SV-15).</summary>
public sealed class SchemaCanvasTests
{
    /// <summary>clientes ← encomendas → produtos, e encomenda_linhas → encomendas (1:N) e perfis → clientes (1:1).</summary>
    internal static (List<GraphNode> Nodes, List<GraphEdge> Edges) Shop()
    {
        GraphNode Node(string name, params GraphRow[] rows) => new(name, name, rows);
        var clientes = Node("clientes", new("id", "int(10) unsigned", GraphRowFlags.PrimaryKey, 0), new("nome", "varchar(120)", GraphRowFlags.None, 2), new("email", "varchar(200)", GraphRowFlags.Unique | GraphRowFlags.Nullable, 2));
        var produtos = Node("produtos", new("id", "int(10) unsigned", GraphRowFlags.PrimaryKey, 0), new("nome", "varchar(120)", GraphRowFlags.None, 2), new("preco", "decimal(10,2)", GraphRowFlags.None, 1));
        var encomendas = Node("encomendas", new("id", "int(10) unsigned", GraphRowFlags.PrimaryKey, 0), new("cliente_id", "int(10) unsigned", GraphRowFlags.ForeignKey, 0), new("criada", "datetime", GraphRowFlags.None, 4));
        var linhas = Node("encomenda_linhas", new("encomenda_id", "int(10) unsigned", GraphRowFlags.PrimaryKey | GraphRowFlags.ForeignKey, 0),
            new("produto_id", "int(10) unsigned", GraphRowFlags.PrimaryKey | GraphRowFlags.ForeignKey, 0), new("quantidade", "smallint", GraphRowFlags.None, 0));
        var perfis = Node("perfis", new("cliente_id", "int(10) unsigned", GraphRowFlags.PrimaryKey | GraphRowFlags.ForeignKey, 0), new("bio", "text", GraphRowFlags.Nullable, 2));
        var logs = Node("logs", new("id", "bigint", GraphRowFlags.PrimaryKey, 0), new("texto", "text", GraphRowFlags.Nullable, 2));
        List<GraphNode> nodes = [clientes, produtos, encomendas, linhas, perfis, logs];
        List<GraphEdge> edges =
        [
            new("fk_encomenda_cliente", encomendas, [1], clientes, [0], GraphCardinality.OneToMany),
            new("fk_linha_encomenda", linhas, [0], encomendas, [0], GraphCardinality.OneToMany),
            new("fk_linha_produto", linhas, [1], produtos, [0], GraphCardinality.OneToMany),
            new("fk_perfil_cliente", perfis, [0], clientes, [0], GraphCardinality.OneToOne),
        ];
        return (nodes, edges);
    }

    /// <summary>Layout esquerda → direita com as tabelas referenciadas à esquerda.</summary>
    internal static void LayoutOf(List<GraphNode> nodes, List<GraphEdge> edges)
    {
        foreach (var n in nodes)
        {
            n.Position = default;
        }
        var sizes = nodes.Select(n => SchemaCanvas.Measure(n)).Select(s => new LayoutSize(s.Width, s.Height)).ToList();
        var links = edges.Select(e => new LayoutEdge(nodes.IndexOf(e.To), nodes.IndexOf(e.From))).ToList();
        var points = new LayeredLayout().Layout(sizes, links, CancellationToken.None);
        for (var i = 0; i < nodes.Count; i++)
        {
            nodes[i].Position = new Point(points[i].X, points[i].Y);
        }
    }

    private static (Window Window, SchemaCanvas Canvas) Show(string theme = "light")
    {
        var (nodes, edges) = Shop();
        var canvas = new SchemaCanvas { TypeColor = (category, dark) => dark ? 0xFF8597FFu : category == 2 ? 0xFF008000u : 0xFF0000FFu };
        canvas.SetGraph(nodes, edges);
        LayoutOf(nodes, edges);
        var window = new Window { Width = 1000, Height = 560, Content = canvas, RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light };
        window.Show();
        window.CaptureRenderedFrame();
        return (window, canvas);
    }

    [AvaloniaTheory]
    [InlineData("schema-canvas-light", "light")]
    [InlineData("schema-canvas-dark", "dark")]
    public void Snapshot(string name, string theme)
    {
        using var culture = new CultureScope(CultureInfo.InvariantCulture);
        var (window, canvas) = Show(theme);
        canvas.FitToView();
        canvas.Select([canvas.Nodes.Single(n => n.Title == "encomendas")]);
        canvas.Nodes.Single(n => n.Title == "produtos").IsMatch = true;
        canvas.Nodes.Single(n => n.Title == "logs").IsMatch = false;
        window.CaptureRenderedFrame();
        Tests.Snapshot.Match(window.CaptureRenderedFrame(), name);
        window.Close();
    }

    [AvaloniaFact]
    public void SelecaoArrastarZoomENavegacaoPorFk()
    {
        var (window, canvas) = Show();
        canvas.ZoomTo(1);
        var encomendas = canvas.Nodes.Single(n => n.Title == "encomendas");
        var clientes = canvas.Nodes.Single(n => n.Title == "clientes");
        canvas.CenterOn(encomendas);
        window.CaptureRenderedFrame();
        var header = canvas.ToScreen(encomendas.Position + new Point(20, 10));

        // Clique no cabeçalho seleciona; Ctrl+clique noutro junta.
        window.MouseDown(header, MouseButton.Left);
        window.MouseUp(header, MouseButton.Left);
        Assert.Equal([encomendas], canvas.SelectedNodes);
        canvas.CenterOn(clientes);
        window.MouseDown(canvas.ToScreen(clientes.Position + new Point(20, 10)), MouseButton.Left, RawInputModifiers.Control);
        window.MouseUp(canvas.ToScreen(clientes.Position + new Point(20, 10)), MouseButton.Left, RawInputModifiers.Control);
        Assert.Equal(2, canvas.SelectedNodes.Count);

        // Arrastar move os selecionados; com snap, as posições ficam na grelha.
        canvas.SnapToGrid = true;
        var moved = 0;
        canvas.NodesMoved += (_, _) => moved++;
        // Longe do clique anterior (senão conta como duplo clique).
        var start = canvas.ToScreen(clientes.Position + new Point(90, 12));
        var before = encomendas.Position;
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(start + new Point(30, 0));
        window.MouseMove(start + new Point(63, 41));
        window.MouseUp(start + new Point(63, 41), MouseButton.Left);
        Assert.Equal(1, moved);
        Assert.NotEqual(before, encomendas.Position);
        Assert.Equal(0, clientes.Position.X % 20);
        Assert.Equal(0, encomendas.Position.Y % 20);

        // Roda: zoom no ponto do rato; 0 volta a 100%; F ajusta.
        canvas.Focus();
        window.MouseWheel(new Point(500, 280), new Vector(0, 2));
        Assert.True(canvas.Zoom > 1.2);
        window.KeyPress(Key.D0, RawInputModifiers.None, PhysicalKey.Digit0, "0");
        Assert.Equal(1, canvas.Zoom, 3);
        window.KeyPress(Key.F, RawInputModifiers.None, PhysicalKey.F, "f");
        Assert.True(canvas.Nodes.All(n => new Rect(canvas.Bounds.Size).Contains(canvas.ToScreen(n.Bounds.Center))));

        // Clique numa coluna FK (cliente_id) ativa a relação para clientes.
        canvas.ZoomTo(1);
        canvas.CenterOn(encomendas);
        GraphEdge? activated = null;
        canvas.RelationActivated += (_, e) => activated = e;
        var fkRow = canvas.ToScreen(encomendas.Position + new Point(40, SchemaCanvas.HeaderHeight + SchemaCanvas.RowHeight + 5));
        window.MouseDown(fkRow, MouseButton.Left);
        window.MouseUp(fkRow, MouseButton.Left);
        Assert.Same(clientes, activated?.To);

        // Duplo clique ativa o nó.
        GraphNode? opened = null;
        canvas.NodeActivated += (_, e) => opened = e.Node;
        window.MouseDown(header = canvas.ToScreen(encomendas.Position + new Point(20, 10)), MouseButton.Left);
        window.MouseUp(header, MouseButton.Left);
        window.MouseDown(header, MouseButton.Left);
        window.MouseUp(header, MouseButton.Left);
        Assert.Same(encomendas, opened);

        // Export PNG do diagrama inteiro.
        using var png = new MemoryStream();
        canvas.ExportPng(png, wholeDiagram: true);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], png.ToArray()[..4]);
        window.Close();
    }
}
