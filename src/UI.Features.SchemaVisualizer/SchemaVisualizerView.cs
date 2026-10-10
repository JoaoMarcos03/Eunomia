using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Eunomia.App.Diagram;
using Eunomia.Engine.Results;
using Eunomia.Engine.Sql.Filters;
using Eunomia.UI.Controls.Common.Icons;
using Eunomia.UI.Controls.Common.Input;
using Eunomia.UI.Controls.Graph;
using Eunomia.UI.Controls.Graph.Layout;
using Eunomia.UI.Controls.Grid;

namespace Eunomia.UI.Features.SchemaVisualizer;

/// <summary>
/// "Diagram: database" tab (section 17): a bar with the search (Table filter syntax: regex, <c>;</c> = <c>|</c>; tables and
/// columns; the rest is dimmed), Fit, 100%, Relayout, Hide, Show only selected, Show all, Snap to grid, Sync with
/// tree and Export PNG; the canvas below. The layout runs off the UI thread and can be cancelled; tables without a position
/// (new in the schema or never shown yet) are placed to the right of the others, without moving them (SV-12). With more
/// than <see cref="MaxDefaultTables"/> tables and no saved layout, only the connected component of the focused table (or
/// the largest one) opens, and the rest stays hidden until "Show all" (section 17.5).
/// </summary>
public sealed class SchemaVisualizerView : DockPanel
{
    public const int MaxDefaultTables = 300;

    private readonly IGraphLayout _layoutEngine = new LayeredLayout();
    private readonly TextBox _search = new() { PlaceholderText = "Search tables and columns (regular expression, ; = or)", Width = 230, MinHeight = 22, Padding = new Thickness(4, 1) };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0), Foreground = Brushes.Gray };
    private readonly CheckBox _sync = new() { Content = Label("Sync with tree"), IsChecked = true, MinHeight = 22, Margin = new Thickness(8, 0, 0, 0) };
    private readonly CheckBox _snap = new() { Content = Label("Snap"), MinHeight = 22, Margin = new Thickness(8, 0, 0, 0) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly Dictionary<string, GraphNode> _nodes = new(StringComparer.Ordinal);
    private SchemaDiagram? _diagram;
    private CancellationTokenSource? _layout;
    private int _removed;
    private bool _highlighting;

    public SchemaVisualizerView(string database)
    {
        ArgumentNullException.ThrowIfNull(database);
        Database = database;
        Canvas = new SchemaCanvas
        {
            TypeColor = (category, dark) => ((ISolidColorBrush)(dark ? GridPalette.Dark : GridPalette.Light).TextFor((TypeCategory)Math.Clamp(category, 0, 6))).Color.ToUInt32(),
        };

        // Accessibility: names for the controls without visible text of their own.
        Avalonia.Automation.AutomationProperties.SetName(Canvas, "Schema diagram of " + database);
        Avalonia.Automation.AutomationProperties.SetName(_search, "Search tables and columns");
        var bar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 3) };
        bar.Children.Add(_search);
        bar.Children.Add(Button("Fit", "Fit to screen (F)", () => Canvas.FitToView()));
        bar.Children.Add(Button("100%", "Zoom 100% (0)", () => Canvas.ZoomTo(1)));
        bar.Children.Add(Button("Relayout", "Arrange the visible tables again", () => _ = RelayoutAsync()));
        bar.Children.Add(Button("Hide", "Hide the selected tables (Delete)", HideSelected));
        bar.Children.Add(Button("Only selected", "Show only the selected tables", ShowOnlySelected));
        bar.Children.Add(Button("Show all", "Show the hidden tables", () => _ = ShowAllAsync()));
        var export = Button("Export PNG", "Save the diagram as a PNG image", () => { });
        export.Flyout = new MenuFlyout
        {
            ItemsSource = new[]
            {
                MenuItem("Current view...", () => _ = ExportAsync(wholeDiagram: false)),
                MenuItem("Whole diagram...", () => _ = ExportAsync(wholeDiagram: true)),
            },
        };
        bar.Children.Add(export);
        bar.Children.Add(_snap);
        bar.Children.Add(_sync);
        bar.Children.Add(_status);
        DockPanel.SetDock(bar, Dock.Top);
        Children.Add(bar);
        Children.Add(Canvas);

        _search.TextChanged += (_, _) => ApplySearch();
        _search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                SelectMatches();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                _search.Text = string.Empty;
                Canvas.Focus();
                e.Handled = true;
            }
        };
        _snap.IsCheckedChanged += (_, _) => Canvas.SnapToGrid = _snap.IsChecked == true;
        Canvas.NodeActivated += (_, e) => OpenTableRequested?.Invoke(this, e.Node.Id);
        Canvas.NodeContextRequested += (_, e) => ShowNodeMenu(e);
        Canvas.RelationActivated += (_, edge) => Highlight(edge.To.Id, select: true);
        Canvas.NodesMoved += (_, _) => ScheduleSave();
        Canvas.ViewChanged += (_, _) => ScheduleSave();
        Canvas.SelectionChanged += (_, _) =>
        {
            if (!_highlighting && _sync.IsChecked == true && Canvas.SelectedNodes.Count == 1)
            {
                TableSelected?.Invoke(this, Canvas.SelectedNodes.First().Id);
            }
        };
        Canvas.KeyDown += OnCanvasKeyDown;
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            StateChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    public string Database { get; }

    public SchemaCanvas Canvas { get; }

    public SchemaDiagram? Diagram => _diagram;

    /// <summary>Selecting a node selects the table in the tree ("Sync with tree", on by default).</summary>
    public bool SyncWithTree => _sync.IsChecked == true;

    /// <summary>Layout in progress (tests wait for it).</summary>
    public Task Layouting { get; private set; } = Task.CompletedTask;

    /// <summary>Double click or Enter: opens the table's Data tab.</summary>
    public event EventHandler<string>? OpenTableRequested;

    /// <summary>Alt+Enter: opens the structure in the table editor.</summary>
    public event EventHandler<string>? EditTableRequested;

    public event EventHandler<string>? SelectTopRequested;

    /// <summary>F5: the shell reads the schema again and calls <see cref="ShowAsync"/> again.</summary>
    public event EventHandler? RefreshRequested;

    /// <summary>Positions, visibility or view changed (the shell saves <see cref="CurrentState"/>).</summary>
    public event EventHandler? StateChanged;

    /// <summary>A node was selected with "Sync with tree" on.</summary>
    public event EventHandler<string>? TableSelected;

    internal TextBox SearchBox => _search;

    internal TextBlock StatusText => _status;

    /// <summary>
    /// Shows the diagram: positions from the current diagram (refresh) or from the saved layout; the rest is placed by the
    /// layout. On a refresh, new tables are highlighted and removed ones are counted in the bar (SV-16).
    /// </summary>
    public async Task ShowAsync(SchemaDiagram diagram, DiagramLayoutState? saved, string? focusTable = null)
    {
        ArgumentNullException.ThrowIfNull(diagram);
        var previous = _nodes.ToDictionary(n => n.Key, n => n.Value, StringComparer.Ordinal);
        var refresh = _diagram is not null;
        _diagram = diagram;
        _nodes.Clear();
        foreach (var table in diagram.Tables)
        {
            var rows = table.Columns.Select(c => new GraphRow(c.Name, c.Type,
                (c.PrimaryKey ? GraphRowFlags.PrimaryKey : 0) | (c.Unique ? GraphRowFlags.Unique : 0) | (c.ForeignKey ? GraphRowFlags.ForeignKey : 0) | (c.Nullable ? GraphRowFlags.Nullable : 0),
                (int)c.Category)).ToList();
            var node = new GraphNode(table.Name, table.Name, rows);
            if (previous.TryGetValue(table.Name, out var old))
            {
                node.Position = old.Position;
                node.IsHidden = old.IsHidden;
                node.IsNew = old.IsNew;
                node.Tag = old.Tag;
            }
            else if (saved?.Nodes.TryGetValue(table.Name, out var state) == true)
            {
                node.Position = new Point(state.X, state.Y);
                node.IsHidden = state.Hidden;
                node.Tag = state.Placed ? Placed : null;
            }
            else
            {
                node.IsNew = refresh || saved is not null;
            }
            _nodes[table.Name] = node;
        }
        _removed = refresh ? previous.Keys.Count(k => !_nodes.ContainsKey(k)) : 0;
        var edges = new List<GraphEdge>();
        foreach (var relation in diagram.Relations)
        {
            if (!_nodes.TryGetValue(relation.Table, out var from) || !_nodes.TryGetValue(relation.ReferencedTable, out var to))
            {
                continue;
            }
            var fromRows = relation.Columns.Select(c => IndexOf(from, c)).Where(i => i >= 0).ToList();
            var toRows = relation.ReferencedColumns.Select(c => IndexOf(to, c)).Where(i => i >= 0).ToList();
            edges.Add(new GraphEdge(relation.Name, from, fromRows, to, toRows, relation.OneToOne ? GraphCardinality.OneToOne : GraphCardinality.OneToMany));
        }

        var nodes = _nodes.Values.ToList();
        var placedBefore = nodes.Any(n => ReferenceEquals(n.Tag, Placed)) || saved?.Nodes.Count > 0;
        if (!placedBefore && nodes.Count > MaxDefaultTables)
        {
            // Never 2,000 tables at once: the focused table's component, or the largest one.
            var keep = ComponentOf(nodes, edges, focusTable is not null && _nodes.TryGetValue(focusTable, out var f) ? f : null);
            foreach (var node in nodes)
            {
                node.IsHidden = !keep.Contains(node);
            }
        }
        Canvas.SetGraph(nodes, edges);
        await PlaceAsync(nodes.Where(n => !n.IsHidden && !ReferenceEquals(n.Tag, Placed)).ToList());
        ApplySearch();
        if (saved is not null && !refresh && placedBefore)
        {
            Canvas.ZoomTo(saved.Zoom, new Point(0, 0));
            Canvas.PanBy(saved.OffsetX - Canvas.Offset.X, saved.OffsetY - Canvas.Offset.Y);
        }
        else if (!refresh)
        {
            Dispatcher.UIThread.Post(() => Canvas.FitToView(), DispatcherPriority.Background);
        }
        if (focusTable is not null)
        {
            Highlight(focusTable, select: true);
        }
        UpdateStatus();
    }

    private static readonly object Placed = new();

    private static int IndexOf(GraphNode node, string column)
    {
        for (var i = 0; i < node.Rows.Count; i++)
        {
            if (string.Equals(node.Rows[i].Name, column, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    private static HashSet<GraphNode> ComponentOf(List<GraphNode> nodes, List<GraphEdge> edges, GraphNode? start)
    {
        var neighbours = nodes.ToDictionary(n => n, _ => new List<GraphNode>());
        foreach (var e in edges)
        {
            neighbours[e.From].Add(e.To);
            neighbours[e.To].Add(e.From);
        }
        HashSet<GraphNode> Walk(GraphNode from)
        {
            var seen = new HashSet<GraphNode> { from };
            var queue = new Queue<GraphNode>([from]);
            while (queue.Count > 0 && seen.Count < MaxDefaultTables)
            {
                foreach (var w in neighbours[queue.Dequeue()])
                {
                    if (seen.Count < MaxDefaultTables && seen.Add(w))
                    {
                        queue.Enqueue(w);
                    }
                }
            }
            return seen;
        }
        if (start is not null)
        {
            return Walk(start);
        }
        var best = new HashSet<GraphNode>();
        var visited = new HashSet<GraphNode>();
        foreach (var node in nodes.OrderByDescending(n => neighbours[n].Count))
        {
            if (visited.Contains(node))
            {
                continue;
            }
            var component = Walk(node);
            visited.UnionWith(component);
            if (component.Count > best.Count)
            {
                best = component;
            }
        }
        return best;
    }

    /// <summary>Places <paramref name="nodes"/> with the layout (off the UI thread), to the right of those that already have a position.</summary>
    private Task PlaceAsync(List<GraphNode> nodes)
    {
        _layout?.Cancel();
        if (nodes.Count == 0)
        {
            return Task.CompletedTask;
        }
        var cts = new CancellationTokenSource();
        _layout = cts;
        var index = nodes.Select((n, i) => (n, i)).ToDictionary(t => t.n, t => t.i);
        var sizes = nodes.Select(n => new LayoutSize(n.Size.Width, n.Size.Height)).ToList();
        // Left → right from the referenced tables.
        var links = Canvas.Edges.Where(e => index.ContainsKey(e.From) && index.ContainsKey(e.To)).Select(e => new LayoutEdge(index[e.To], index[e.From])).ToList();
        var others = Canvas.Nodes.Where(n => !n.IsHidden && ReferenceEquals(n.Tag, Placed) && !index.ContainsKey(n)).ToList();
        var origin = others.Count == 0 ? new Point(0, 0) : new Point(others.Max(n => n.Bounds.Right) + 120, others.Min(n => n.Position.Y));
        _status.Text = "Arranging " + nodes.Count.ToString(CultureInfo.CurrentCulture) + " tables...";
        Layouting = RunAsync();
        return Layouting;

        async Task RunAsync()
        {
            IReadOnlyList<LayoutPoint> points;
            try
            {
                points = await Task.Run(() => _layoutEngine.Layout(sizes, links, cts.Token), cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            for (var i = 0; i < nodes.Count; i++)
            {
                nodes[i].Position = new Point(origin.X + points[i].X, origin.Y + points[i].Y);
                nodes[i].Tag = Placed;
            }
            Canvas.InvalidateVisual();
            UpdateStatus();
            ScheduleSave();
        }
    }

    /// <summary>State to save: positions, hidden tables (only those already placed) and the view.</summary>
    public DiagramLayoutState CurrentState() => new(
        _diagram?.Hash ?? string.Empty,
        _nodes.Values.Where(n => ReferenceEquals(n.Tag, Placed) || n.IsHidden)
            .ToDictionary(n => n.Id, n => new DiagramNodeState(n.Position.X, n.Position.Y, n.IsHidden, ReferenceEquals(n.Tag, Placed)), StringComparer.Ordinal),
        Canvas.Zoom,
        Canvas.Offset.X,
        Canvas.Offset.Y);

    /// <summary>
    /// Tree → visualizer (section 17.3): selects and centres the table without stealing the keyboard focus; a hidden table
    /// shows again.
    /// </summary>
    public void Highlight(string table, bool select = true)
    {
        if (!_nodes.TryGetValue(table, out var node))
        {
            return;
        }
        if (node.IsHidden)
        {
            node.IsHidden = false;
            if (!ReferenceEquals(node.Tag, Placed))
            {
                _ = PlaceAsync([node]);
            }
        }
        _highlighting = true;
        try
        {
            if (select)
            {
                Canvas.Select([node]);
            }
            Canvas.CenterOn(node);
        }
        finally
        {
            _highlighting = false;
        }
        UpdateStatus();
    }

    // ---------- Actions ----------

    private async Task RelayoutAsync()
    {
        var visible = _nodes.Values.Where(n => !n.IsHidden).ToList();
        foreach (var node in visible)
        {
            node.Tag = null;
            node.IsNew = false;
        }
        await PlaceAsync(visible);
        Canvas.FitToView();
    }

    private void HideSelected()
    {
        foreach (var node in Canvas.SelectedNodes.ToList())
        {
            node.IsHidden = true;
        }
        Canvas.Select([]);
        UpdateStatus();
        ScheduleSave();
    }

    private void ShowOnlySelected()
    {
        if (Canvas.SelectedNodes.Count == 0)
        {
            return;
        }
        foreach (var node in _nodes.Values)
        {
            node.IsHidden = !Canvas.SelectedNodes.Contains(node);
        }
        Canvas.FitToView();
        UpdateStatus();
        ScheduleSave();
    }

    private async Task ShowAllAsync()
    {
        var unplaced = new List<GraphNode>();
        foreach (var node in _nodes.Values.Where(n => n.IsHidden))
        {
            node.IsHidden = false;
            if (!ReferenceEquals(node.Tag, Placed))
            {
                unplaced.Add(node);
            }
        }
        Canvas.InvalidateVisual();
        await PlaceAsync(unplaced);
        Canvas.FitToView();
        UpdateStatus();
        ScheduleSave();
    }

    /// <summary>Search with the Table filter syntax (SV-06): table name or a column name.</summary>
    private void ApplySearch()
    {
        var filter = new TreeFilterExpression(_search.Text ?? string.Empty);
        var matches = 0;
        foreach (var node in _nodes.Values)
        {
            if (!filter.IsActive)
            {
                node.IsMatch = null;
                continue;
            }
            node.IsMatch = filter.IsMatch(node.Title) || node.Rows.Any(r => filter.IsMatch(r.Name));
            if (node.IsMatch == true && !node.IsHidden)
            {
                matches++;
            }
        }
        if (filter.Error is null)
        {
            _search.ClearValue(TemplatedControl.ForegroundProperty);
        }
        else
        {
            _search.Foreground = Brushes.Red;
        }
        Canvas.InvalidateVisual();
        UpdateStatus(filter.IsActive ? matches : null);
    }

    private void SelectMatches()
    {
        var matches = _nodes.Values.Where(n => n.IsMatch == true && !n.IsHidden).ToList();
        if (matches.Count == 0)
        {
            return;
        }
        Canvas.Select(matches);
        Canvas.CenterOn(matches[0]);
    }

    private void UpdateStatus(int? matches = null)
    {
        var c = CultureInfo.CurrentCulture;
        var hidden = _nodes.Values.Count(n => n.IsHidden);
        var text = string.Create(c, $"{_nodes.Count:N0} tables, {Canvas.Edges.Count:N0} relations");
        if (hidden > 0)
        {
            text += string.Create(c, $", {hidden:N0} hidden");
        }
        var added = _nodes.Values.Count(n => n.IsNew);
        if (added > 0)
        {
            text += string.Create(c, $", {added:N0} new");
        }
        if (_removed > 0)
        {
            text += string.Create(c, $", {_removed:N0} removed");
        }
        if (matches is { } m)
        {
            text += string.Create(c, $" — {m:N0} matching");
        }
        _status.Text = text;
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void OnCanvasKeyDown(object? sender, KeyEventArgs e)
    {
        var single = Canvas.SelectedNodes.Count == 1 ? Canvas.SelectedNodes.First().Id : null;
        switch (e.Key)
        {
            case Key.Enter when (e.KeyModifiers & KeyModifiers.Alt) != 0 && single is not null:
                EditTableRequested?.Invoke(this, single);
                break;
            case Key.Enter when single is not null:
                OpenTableRequested?.Invoke(this, single);
                break;
            case Key.Delete:
                HideSelected();
                break;
            case Key.F5:
                RefreshRequested?.Invoke(this, EventArgs.Empty);
                break;
            case Key.F when PlatformKeys.IsCommand(e.KeyModifiers):
                _search.Focus();
                _search.SelectAll();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>Node menu (SV-14).</summary>
    private void ShowNodeMenu(GraphNodeEventArgs e)
    {
        var table = e.Node.Id;
        var menu = new ContextMenu
        {
            ItemsSource = new object[]
            {
                MenuItem("Open table", () => OpenTableRequested?.Invoke(this, table), "table"),
                MenuItem("Edit structure", () => EditTableRequested?.Invoke(this, table), "edit", "Alt+Enter"),
                MenuItem("Select top 1000 rows", () => SelectTopRequested?.Invoke(this, table), "run"),
                MenuItem("Copy name", () => _ = CopyAsync(table), "copy"),
                new Separator(),
                MenuItem("Hide", HideSelected),
                MenuItem("Show only selected", ShowOnlySelected),
                MenuItem("Show all", () => _ = ShowAllAsync()),
            },
        };
        menu.Open(Canvas);
    }

    private async Task CopyAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    private async Task ExportAsync(bool wholeDiagram)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export diagram",
            DefaultExtension = "png",
            SuggestedFileName = Database + ".png",
            FileTypeChoices = [new FilePickerFileType("PNG image") { Patterns = ["*.png"] }],
        });
        if (file?.TryGetLocalPath() is not { } path)
        {
            return;
        }
        await using var stream = File.Create(path);
        Canvas.ExportPng(stream, wholeDiagram);
    }

    private static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center };

    private static Button Button(string caption, string tip, Action click)
    {
        var button = new Button { Content = caption, Padding = new Thickness(6, 1), Margin = new Thickness(3, 0, 0, 0), MinHeight = 22, Focusable = false };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    private static MenuItem MenuItem(string header, Action click, string? icon = null, string? gesture = null)
    {
        var item = new MenuItem { Header = header, Icon = IconFactory.CreateImage(icon) };
        if (gesture is not null)
        {
            item.InputGesture = KeyGesture.Parse(gesture);
        }
        item.Click += (_, _) => click();
        return item;
    }
}
