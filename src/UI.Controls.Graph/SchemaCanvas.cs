using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using Eunomia.UI.Controls.Common.Input;
using Eunomia.UI.Controls.Common.Theming;

namespace Eunomia.UI.Controls.Graph;

/// <summary>
/// Canvas do Schema Visualizer (secção 17; SV-01–SV-04, SV-07, SV-08, SV-13–SV-15): tabelas como nós com as colunas,
/// relações em curva da linha da FK para a linha referenciada, crow's foot no lado "muitos" e barra no lado "um" (1:1
/// com barras nos dois). Zoom com a roda (e Ctrl+roda, +/−), pan arrastando o fundo, com espaço+arrastar e com as
/// setas, F ajusta tudo ao ecrã e 0 volta a 100%. Clique seleciona (Ctrl junta ou tira), as relações da seleção ficam
/// realçadas (a saída numa cor, a entrada noutra); arrastar move os nós selecionados, com snap opcional. Só desenha o
/// que está visível; com zoom baixo mostra só os nomes (level of detail). Só leitura: nunca muda o schema (SV-18).
/// </summary>
public sealed class SchemaCanvas : Control
{
    public const double HeaderHeight = 26;
    public const double RowHeight = 18;
    private const double MinNodeWidth = 150;
    private const double MaxNodeWidth = 380;
    private const double Padding = 8;
    private const double IconWidth = 18;
    private const double MinZoom = 0.05;
    private const double MaxZoom = 3;

    private static readonly Typeface Regular = new(UiFonts.Ui);
    private static readonly Typeface Bold = new(UiFonts.Ui, FontStyle.Normal, FontWeight.SemiBold);

    private readonly List<GraphNode> _nodes = [];
    private readonly List<GraphEdge> _edges = [];
    private readonly HashSet<GraphNode> _selected = [];
    private readonly Dictionary<GraphNode, NodeText> _text = [];
    private double _zoom = 1;
    private Vector _offset = new(20, 20);
    private Point? _pressed;
    private Point _last;
    private bool _panning;
    private bool _dragging;
    private bool _spaceDown;
    private GraphNode? _pressedNode;
    private int _pressedRow = -1;
    private bool _themeDark;

    public SchemaCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        ContextRequested += OnContextRequested;
        ActualThemeVariantChanged += (_, _) =>
        {
            _text.Clear();
            InvalidateVisual();
        };
    }

    // ---------- Dados ----------

    public IReadOnlyList<GraphNode> Nodes => _nodes;

    public IReadOnlyList<GraphEdge> Edges => _edges;

    public IReadOnlyCollection<GraphNode> SelectedNodes => _selected;

    /// <summary>Cor de uma categoria de tipo (claro/escuro); por omissão, a cor do texto.</summary>
    public Func<int, bool, uint>? TypeColor { get; set; }

    /// <summary>Cor da sessão: moldura do canvas (secção 17.3).</summary>
    public uint SessionColor { get; set; }

    /// <summary>Ao largar nós arrastados, as posições ficam numa grelha (SV-08).</summary>
    public bool SnapToGrid { get; set; }

    public double GridSize { get; set; } = 20;

    public double Zoom => _zoom;

    public Vector Offset => _offset;

    public event EventHandler? SelectionChanged;

    /// <summary>Duplo clique num nó (abre o Data tab).</summary>
    public event EventHandler<GraphNodeEventArgs>? NodeActivated;

    public event EventHandler<GraphNodeEventArgs>? NodeContextRequested;

    /// <summary>Clique numa relação ou numa coluna FK (SV-15): o diagrama centra a tabela referenciada.</summary>
    public event EventHandler<GraphEdge>? RelationActivated;

    /// <summary>Nós largados depois de arrastar (para guardar as posições).</summary>
    public event EventHandler? NodesMoved;

    public event EventHandler? ViewChanged;

    public void SetGraph(IEnumerable<GraphNode> nodes, IEnumerable<GraphEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);
        _nodes.Clear();
        _nodes.AddRange(nodes);
        _edges.Clear();
        _edges.AddRange(edges);
        _selected.RemoveWhere(n => !_nodes.Contains(n));
        _text.Clear();
        foreach (var node in _nodes)
        {
            node.Size = Measure(node);
        }
        InvalidateVisual();
    }

    /// <summary>Tamanho do nó para o layout (antes de estar no ecrã).</summary>
    public static Size Measure(GraphNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var width = Text(node.Title, Bold, 12, Brushes.Black).Width + (2 * Padding);
        foreach (var row in node.Rows)
        {
            var w = IconWidth + Text(row.Name, Regular, 11.5, Brushes.Black).Width + 16 + Text(TypeText(row), Regular, 11, Brushes.Black).Width + (2 * Padding);
            width = Math.Max(width, w);
        }
        return new Size(Math.Ceiling(Math.Clamp(width, MinNodeWidth, MaxNodeWidth)), HeaderHeight + (RowHeight * node.Rows.Count) + 4);
    }

    private static string TypeText(GraphRow row) => (row.Flags & GraphRowFlags.Nullable) != 0 ? row.Type + " NULL" : row.Type;

    // ---------- Seleção e vista ----------

    public void Select(IEnumerable<GraphNode> nodes, bool add = false)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (!add)
        {
            _selected.Clear();
        }
        foreach (var node in nodes.Where(_nodes.Contains))
        {
            _selected.Add(node);
        }
        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Centra um nó sem mudar o zoom (árvore → visualizer; navegação por FK).</summary>
    public void CenterOn(GraphNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var center = node.Bounds.Center;
        _offset = new Vector((Bounds.Width / 2) - (center.X * _zoom), (Bounds.Height / 2) - (center.Y * _zoom));
        ViewChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>Ajusta todos os nós visíveis ao ecrã (tecla F).</summary>
    public void FitToView()
    {
        var visible = _nodes.Where(n => !n.IsHidden).ToList();
        if (visible.Count == 0 || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }
        var world = visible.Select(n => n.Bounds).Aggregate((a, b) => a.Union(b)).Inflate(30);
        _zoom = Math.Clamp(Math.Min(Bounds.Width / world.Width, Bounds.Height / world.Height), MinZoom, 1);
        _offset = new Vector((Bounds.Width - (world.Width * _zoom)) / 2 - (world.X * _zoom), (Bounds.Height - (world.Height * _zoom)) / 2 - (world.Y * _zoom));
        ViewChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>Muda o zoom mantendo o ponto <paramref name="anchor"/> (do controlo) no mesmo sítio.</summary>
    public void ZoomTo(double zoom, Point? anchor = null)
    {
        var at = anchor ?? new Point(Bounds.Width / 2, Bounds.Height / 2);
        var world = ToWorld(at);
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        _offset = new Vector(at.X - (world.X * _zoom), at.Y - (world.Y * _zoom));
        ViewChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    public void PanBy(double dx, double dy)
    {
        _offset += new Vector(dx, dy);
        ViewChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    public Point ToWorld(Point point) => new((point.X - _offset.X) / _zoom, (point.Y - _offset.Y) / _zoom);

    public Point ToScreen(Point world) => new((world.X * _zoom) + _offset.X, (world.Y * _zoom) + _offset.Y);

    /// <summary>Nó no ponto do controlo (o de cima primeiro).</summary>
    public GraphNode? NodeAt(Point point)
    {
        var world = ToWorld(point);
        for (var i = _nodes.Count - 1; i >= 0; i--)
        {
            if (!_nodes[i].IsHidden && _nodes[i].Bounds.Contains(world))
            {
                return _nodes[i];
            }
        }
        return null;
    }

    /// <summary>Índice da coluna no ponto (−1 no cabeçalho).</summary>
    public static int RowAt(GraphNode node, Point world)
    {
        ArgumentNullException.ThrowIfNull(node);
        var y = world.Y - node.Position.Y - HeaderHeight;
        return y < 0 ? -1 : Math.Min(node.Rows.Count - 1, (int)(y / RowHeight));
    }

    /// <summary>Relação perto do ponto do controlo (5 pixels).</summary>
    public GraphEdge? EdgeAt(Point point)
    {
        foreach (var edge in _edges)
        {
            if (edge.From.IsHidden || edge.To.IsHidden)
            {
                continue;
            }
            var (a, c1, c2, b) = Curve(edge);
            var previous = ToScreen(a);
            for (var i = 1; i <= 24; i++)
            {
                var t = i / 24.0;
                var p = ToScreen(Bezier(a, c1, c2, b, t));
                if (DistanceToSegment(point, previous, p) <= 5)
                {
                    return edge;
                }
                previous = p;
            }
        }
        return null;
    }

    // ---------- Render ----------

    private sealed record NodeText(FormattedText Title, FormattedText[] Names, FormattedText[] Types);

    private bool IsDark => ActualThemeVariant == ThemeVariant.Dark;

    private Palette Colors => IsDark ? Palette.Dark : Palette.Light;

    public override void Render(DrawingContext context)
    {
        var colors = Colors;
        if (_themeDark != IsDark)
        {
            _themeDark = IsDark;
            _text.Clear();
        }
        context.FillRectangle(colors.Background, new Rect(Bounds.Size));
        using (context.PushTransform(Matrix.CreateScale(_zoom, _zoom) * Matrix.CreateTranslation(_offset.X, _offset.Y)))
        {
            var world = new Rect(ToWorld(default), ToWorld(new Point(Bounds.Width, Bounds.Height)));
            RenderScene(context, world, _zoom, colors);
        }
        if (SessionColor != 0)
        {
            context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromUInt32(SessionColor | 0xFF000000)), 3), new Rect(Bounds.Size).Deflate(1.5));
        }
    }

    /// <summary>Desenha os nós e relações que cruzam <paramref name="world"/> (coordenadas do diagrama).</summary>
    private void RenderScene(DrawingContext context, Rect world, double zoom, Palette colors)
    {
        var detailed = zoom >= 0.45;
        var anySelected = _selected.Count > 0;
        foreach (var edge in _edges)
        {
            if (edge.From.IsHidden || edge.To.IsHidden)
            {
                continue;
            }
            var (a, c1, c2, b) = Curve(edge);
            var box = new Rect(a, b).Normalize().Union(new Rect(c1, c2).Normalize());
            if (!box.Intersects(world))
            {
                continue;
            }
            var outgoing = _selected.Contains(edge.From);
            var incoming = _selected.Contains(edge.To);
            var dimmed = (anySelected && !outgoing && !incoming) || edge.From.IsMatch == false || edge.To.IsMatch == false;
            var pen = outgoing ? colors.EdgeOut : incoming ? colors.EdgeIn : dimmed ? colors.EdgeDim : colors.Edge;
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(a, false);
                g.CubicBezierTo(c1, c2, b);
                g.EndFigure(false);
            }
            context.DrawGeometry(null, pen, geometry);
            if (detailed)
            {
                // Lado "muitos" (a FK): crow's foot; lado "um": barra. 1:1 tem barra nos dois.
                var fromSide = Math.Sign(c1.X - a.X);
                var toSide = Math.Sign(c2.X - b.X);
                if (edge.Cardinality == GraphCardinality.OneToMany)
                {
                    var tip = new Point(a.X + (fromSide * 12), a.Y);
                    context.DrawLine(pen, tip, new Point(a.X, a.Y - 6));
                    context.DrawLine(pen, tip, new Point(a.X, a.Y + 6));
                    context.DrawLine(pen, tip, a);
                }
                else
                {
                    context.DrawLine(pen, new Point(a.X + (fromSide * 8), a.Y - 6), new Point(a.X + (fromSide * 8), a.Y + 6));
                }
                context.DrawLine(pen, new Point(b.X + (toSide * 8), b.Y - 6), new Point(b.X + (toSide * 8), b.Y + 6));
            }
        }

        foreach (var node in _nodes.Where(n => !_selected.Contains(n)).Concat(_nodes.Where(_selected.Contains)))
        {
            if (node.IsHidden || !node.Bounds.Intersects(world))
            {
                continue;
            }
            DrawNode(context, node, detailed, colors);
        }
    }

    private void DrawNode(DrawingContext context, GraphNode node, bool detailed, Palette colors)
    {
        var bounds = node.Bounds;
        var selected = _selected.Contains(node);
        var dim = node.IsMatch == false;
        using (dim ? context.PushOpacity(0.3) : default(DrawingContext.PushedState?))
        {
            context.DrawRectangle(colors.NodeBackground, selected ? colors.SelectedBorder : node.IsNew ? colors.NewBorder : colors.NodeBorder, bounds, 3, 3);
            var header = new Rect(bounds.X, bounds.Y, bounds.Width, HeaderHeight);
            context.DrawRectangle(selected ? colors.HeaderSelected : colors.Header, null, header, 3, 3);
            if (node.IsMatch == true)
            {
                context.DrawRectangle(null, colors.MatchBorder, bounds.Inflate(2), 4, 4);
            }
            var text = TextOf(node, colors);
            using (context.PushClip(header.Deflate(new Thickness(Padding, 0))))
            {
                context.DrawText(text.Title, new Point(bounds.X + Padding, bounds.Y + ((HeaderHeight - text.Title.Height) / 2)));
            }
            if (!detailed)
            {
                return;
            }
            using (context.PushClip(bounds))
            {
                for (var i = 0; i < node.Rows.Count; i++)
                {
                    var row = node.Rows[i];
                    var y = bounds.Y + HeaderHeight + (i * RowHeight);
                    var flags = row.Flags;
                    var icon = (flags & GraphRowFlags.PrimaryKey) != 0 ? colors.PrimaryKey
                        : (flags & GraphRowFlags.Unique) != 0 ? colors.UniqueKey
                        : (flags & GraphRowFlags.ForeignKey) != 0 ? colors.ForeignKey
                        : null;
                    if (icon is not null)
                    {
                        context.DrawEllipse(icon, null, new Point(bounds.X + Padding + 4, y + (RowHeight / 2)), 4, 4);
                    }
                    if ((flags & GraphRowFlags.ForeignKey) != 0 && (flags & (GraphRowFlags.PrimaryKey | GraphRowFlags.Unique)) != 0)
                    {
                        context.DrawEllipse(null, new Pen(colors.ForeignKey, 1.5), new Point(bounds.X + Padding + 4, y + (RowHeight / 2)), 5.5, 5.5);
                    }
                    context.DrawText(text.Names[i], new Point(bounds.X + Padding + IconWidth, y + ((RowHeight - text.Names[i].Height) / 2)));
                    var type = text.Types[i];
                    context.DrawText(type, new Point(bounds.Right - Padding - type.Width, y + ((RowHeight - type.Height) / 2)));
                }
            }
        }
    }

    private NodeText TextOf(GraphNode node, Palette colors)
    {
        if (_text.TryGetValue(node, out var cached))
        {
            return cached;
        }
        var dark = IsDark;
        var names = new FormattedText[node.Rows.Count];
        var types = new FormattedText[node.Rows.Count];
        for (var i = 0; i < node.Rows.Count; i++)
        {
            var row = node.Rows[i];
            var bold = (row.Flags & GraphRowFlags.PrimaryKey) != 0;
            names[i] = Text(row.Name, bold ? Bold : Regular, 11.5, colors.Text);
            var color = TypeColor?.Invoke(row.Category, dark);
            types[i] = Text(TypeText(row), Regular, 11, color is { } c ? new ImmutableSolidColorBrush(c) : colors.Muted);
        }
        var result = new NodeText(Text(node.Title, Bold, 12, colors.HeaderText), names, types);
        _text[node] = result;
        return result;
    }

    private static FormattedText Text(string text, Typeface typeface, double size, IBrush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush);

    /// <summary>Âncoras e pontos de controlo da curva: do lado de cada nó virado para o outro, à altura da coluna.</summary>
    private static (Point A, Point C1, Point C2, Point B) Curve(GraphEdge edge)
    {
        var from = edge.From.Bounds;
        var to = edge.To.Bounds;
        var fromRow = edge.FromRows.Count > 0 ? edge.FromRows[0] : -1;
        var toRow = edge.ToRows.Count > 0 ? edge.ToRows[0] : -1;
        double RowY(Rect r, int row) => row < 0 ? r.Y + (HeaderHeight / 2) : r.Y + HeaderHeight + (row * RowHeight) + (RowHeight / 2);
        var toRight = to.Center.X >= from.Center.X;
        // Nós sobrepostos na horizontal: as duas pontas do mesmo lado (direito), curva para fora.
        var stacked = to.X < from.Right && from.X < to.Right;
        var a = new Point(stacked || toRight ? from.Right : from.X, RowY(from, fromRow));
        var b = new Point(stacked ? to.Right : toRight ? to.X : to.Right, RowY(to, toRow));
        var bend = Math.Max(40, Math.Abs(b.X - a.X) / 2);
        var c1 = new Point(a.X + ((stacked || toRight ? 1 : -1) * bend), a.Y);
        var c2 = new Point(b.X + ((stacked ? 1 : toRight ? -1 : 1) * bend), b.Y);
        return (a, c1, c2, b);
    }

    private static Point Bezier(Point a, Point c1, Point c2, Point b, double t)
    {
        var u = 1 - t;
        return new Point(
            (u * u * u * a.X) + (3 * u * u * t * c1.X) + (3 * u * t * t * c2.X) + (t * t * t * b.X),
            (u * u * u * a.Y) + (3 * u * u * t * c1.Y) + (3 * u * t * t * c2.Y) + (t * t * t * b.Y));
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        var ab = b - a;
        var length = (ab.X * ab.X) + (ab.Y * ab.Y);
        var t = length == 0 ? 0 : Math.Clamp((((p.X - a.X) * ab.X) + ((p.Y - a.Y) * ab.Y)) / length, 0, 1);
        var q = a + (ab * t);
        return Math.Sqrt(((p.X - q.X) * (p.X - q.X)) + ((p.Y - q.Y) * (p.Y - q.Y)));
    }

    // ---------- Export ----------

    /// <summary>
    /// PNG da vista atual ou do diagrama inteiro (SV-13), em escala 1:1; o diagrama inteiro fica limitado a 16.000 px de
    /// lado (escala reduzida se for preciso).
    /// </summary>
    public void ExportPng(Stream stream, bool wholeDiagram)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var colors = Colors;
        Rect world;
        double zoom;
        if (wholeDiagram)
        {
            var visible = _nodes.Where(n => !n.IsHidden).ToList();
            world = visible.Count == 0 ? new Rect(0, 0, 100, 100) : visible.Select(n => n.Bounds).Aggregate((a, b) => a.Union(b)).Inflate(20);
            zoom = Math.Min(1, 16000 / Math.Max(world.Width, world.Height));
        }
        else
        {
            world = new Rect(ToWorld(default), ToWorld(new Point(Bounds.Width, Bounds.Height)));
            zoom = _zoom;
        }
        var size = new PixelSize(Math.Max(1, (int)Math.Ceiling(world.Width * zoom)), Math.Max(1, (int)Math.Ceiling(world.Height * zoom)));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            context.FillRectangle(colors.Background, new Rect(0, 0, size.Width, size.Height));
            using (context.PushTransform(Matrix.CreateTranslation(-world.X, -world.Y) * Matrix.CreateScale(zoom, zoom)))
            {
                RenderScene(context, world, Math.Max(zoom, 0.45), colors);
            }
        }
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
    }

    // ---------- Entrada ----------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetPosition(this);
        var properties = e.GetCurrentPoint(this).Properties;
        var node = _spaceDown ? null : NodeAt(point);
        if (properties.IsRightButtonPressed)
        {
            if (node is not null && !_selected.Contains(node))
            {
                Select([node]);
            }
            return;
        }
        if (!properties.IsLeftButtonPressed && !properties.IsMiddleButtonPressed)
        {
            return;
        }
        if (e.ClickCount == 2 && node is not null)
        {
            NodeActivated?.Invoke(this, new GraphNodeEventArgs(node, point));
            e.Handled = true;
            return;
        }
        _pressed = point;
        _last = point;
        _pressedNode = node;
        _pressedRow = node is null ? -1 : RowAt(node, ToWorld(point));
        _panning = node is null || properties.IsMiddleButtonPressed;
        _dragging = false;
        if (node is not null && properties.IsLeftButtonPressed)
        {
            var ctrl = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
            var shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
            if (ctrl)
            {
                if (!_selected.Remove(node))
                {
                    _selected.Add(node);
                }
                InvalidateVisual();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
            else if (!_selected.Contains(node))
            {
                Select([node], add: shift);
            }
        }
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_pressed is not { } pressed)
        {
            return;
        }
        var point = e.GetPosition(this);
        var delta = point - _last;
        _last = point;
        if (!_dragging && Math.Abs(point.X - pressed.X) + Math.Abs(point.Y - pressed.Y) < 4)
        {
            return;
        }
        _dragging = true;
        if (_panning)
        {
            PanBy(delta.X, delta.Y);
            return;
        }
        foreach (var node in _selected)
        {
            node.Position += delta / _zoom;
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressed is null)
        {
            return;
        }
        var point = e.GetPosition(this);
        if (_dragging && !_panning)
        {
            if (SnapToGrid && GridSize > 0)
            {
                foreach (var node in _selected)
                {
                    node.Position = new Point(Math.Round(node.Position.X / GridSize) * GridSize, Math.Round(node.Position.Y / GridSize) * GridSize);
                }
            }
            foreach (var node in _selected)
            {
                node.IsNew = false;
            }
            InvalidateVisual();
            NodesMoved?.Invoke(this, EventArgs.Empty);
        }
        else if (!_dragging)
        {
            if (_pressedNode is { } node && _pressedRow >= 0 && (node.Rows[_pressedRow].Flags & GraphRowFlags.ForeignKey) != 0
                && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift)) == 0
                && _edges.FirstOrDefault(edge => ReferenceEquals(edge.From, node) && edge.FromRows.Contains(_pressedRow)) is { } fk)
            {
                RelationActivated?.Invoke(this, fk);
            }
            else if (_pressedNode is null && EdgeAt(point) is { } edge)
            {
                RelationActivated?.Invoke(this, edge);
            }
            else if (_pressedNode is null && _selected.Count > 0 && e.InitialPressMouseButton == MouseButton.Left)
            {
                Select([]);
            }
        }
        _pressed = null;
        _pressedNode = null;
        _panning = false;
        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        // Roda e Ctrl+roda fazem zoom no ponto do rato (SV-04); Shift+roda faz pan na horizontal.
        if ((e.KeyModifiers & KeyModifiers.Shift) != 0)
        {
            PanBy(e.Delta.Y * 40, 0);
        }
        else
        {
            ZoomTo(_zoom * Math.Pow(1.15, e.Delta.Y), e.GetPosition(this));
        }
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.F:
                FitToView();
                break;
            case Key.D0 or Key.NumPad0:
                ZoomTo(1);
                break;
            case Key.OemPlus or Key.Add:
                ZoomTo(_zoom * 1.2);
                break;
            case Key.OemMinus or Key.Subtract:
                ZoomTo(_zoom / 1.2);
                break;
            case Key.Left:
                PanBy(40, 0);
                break;
            case Key.Right:
                PanBy(-40, 0);
                break;
            case Key.Up:
                PanBy(0, 40);
                break;
            case Key.Down:
                PanBy(0, -40);
                break;
            case Key.Space:
                _spaceDown = true;
                break;
            case Key.A when PlatformKeys.IsCommand(e.KeyModifiers):
                Select(_nodes.Where(n => !n.IsHidden));
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key == Key.Space)
        {
            _spaceDown = false;
        }
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        var point = e.TryGetPosition(this, out var p) ? p : new Point(Bounds.Width / 2, Bounds.Height / 2);
        var node = NodeAt(point) ?? _selected.FirstOrDefault();
        if (node is not null)
        {
            NodeContextRequested?.Invoke(this, new GraphNodeEventArgs(node, point));
            e.Handled = true;
        }
    }

    // ---------- Cores ----------

    private sealed class Palette
    {
        public required IImmutableBrush Background { get; init; }

        public required IImmutableBrush NodeBackground { get; init; }

        public required IImmutableBrush Header { get; init; }

        public required IImmutableBrush HeaderSelected { get; init; }

        public required IImmutableBrush HeaderText { get; init; }

        public required IImmutableBrush Text { get; init; }

        public required IImmutableBrush Muted { get; init; }

        public required ImmutablePen NodeBorder { get; init; }

        public required ImmutablePen SelectedBorder { get; init; }

        public required ImmutablePen NewBorder { get; init; }

        public required ImmutablePen MatchBorder { get; init; }

        public required ImmutablePen Edge { get; init; }

        public required ImmutablePen EdgeDim { get; init; }

        public required ImmutablePen EdgeOut { get; init; }

        public required ImmutablePen EdgeIn { get; init; }

        public required IImmutableBrush PrimaryKey { get; init; }

        public required IImmutableBrush UniqueKey { get; init; }

        public required IImmutableBrush ForeignKey { get; init; }

        private static ImmutableSolidColorBrush B(uint argb) => new(argb);

        private static ImmutablePen P(uint argb, double width) => new(argb, width);

        public static Palette Light { get; } = new()
        {
            Background = B(0xFFF7F7F7),
            NodeBackground = B(0xFFFFFFFF),
            Header = B(0xFFE8EEF5),
            HeaderSelected = B(0xFFCCE8FF),
            HeaderText = B(0xFF000000),
            Text = B(0xFF000000),
            Muted = B(0xFF808080),
            NodeBorder = P(0xFFB4BCC4, 1),
            SelectedBorder = P(0xFF0078D7, 2),
            NewBorder = P(0xFF06B025, 2),
            MatchBorder = P(0xFFE0A800, 2),
            Edge = P(0xFF8A96A3, 1.2),
            EdgeDim = P(0x408A96A3, 1),
            EdgeOut = P(0xFF0078D7, 2),
            EdgeIn = P(0xFFD9822B, 2),
            PrimaryKey = B(0xFFC89B00),
            UniqueKey = B(0xFF8E5CC2),
            ForeignKey = B(0xFF2F7FD0),
        };

        public static Palette Dark { get; } = new()
        {
            Background = B(0xFF263238),
            NodeBackground = B(0xFF37474F),
            Header = B(0xFF2A353B),
            HeaderSelected = B(0xFF3D5A80),
            HeaderText = B(0xFFE0E6E9),
            Text = B(0xFFE0E6E9),
            Muted = B(0xFF9AA8AF),
            NodeBorder = P(0xFF5A6A72, 1),
            SelectedBorder = P(0xFF4FA3FF, 2),
            NewBorder = P(0xFF3CCB5A, 2),
            MatchBorder = P(0xFFFFC83D, 2),
            Edge = P(0xFF8796A0, 1.2),
            EdgeDim = P(0x408796A0, 1),
            EdgeOut = P(0xFF4FA3FF, 2),
            EdgeIn = P(0xFFF0A050, 2),
            PrimaryKey = B(0xFFFFC83D),
            UniqueKey = B(0xFFB48CE6),
            ForeignKey = B(0xFF6FB1FF),
        };
    }
}
