using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Eunomia.UI.Controls.Common.Icons;
using Eunomia.UI.Controls.Common.Input;
using Eunomia.UI.Controls.Common.Theming;

namespace Eunomia.UI.Controls.Common.TreeList;

/// <summary>
/// Tree list with columns, drawn in a single visual (like HeidiSQL's VirtualStringTree lists: Session
/// Manager, Host tab, Database tab, helpers). Virtualised per row; header with sorting (click) and draggable
/// widths; background colour per row; tree keyboard; incremental search; in-place rename (F2); drag &amp; drop.
/// </summary>
public sealed class TreeListControl : Control, Input.IEditTarget
{
    public const double DefaultRowHeight = 19;
    public const double HeaderHeight = 22;
    public const double Indent = 16;
    private const double CellPadding = 4;
    private const double DragThreshold = 4;

    private readonly List<TreeListNode> _visible = [];
    private readonly Dictionary<(string, bool, uint), FormattedText> _textCache = [];
    private List<TreeListNode> _roots = [];
    private TreeListNode? _focused;
    private readonly HashSet<TreeListNode> _selected = [];
    private TreeListNode? _anchor;
    private int _topRow;
    private double _scrollX;
    private int _sortColumn = 0;
    private bool _sortDescending;
    private Func<TreeListNode, bool>? _filter;
    private Point? _pressPoint;
    private TreeListNode? _pressNode;
    private TreeListNode? _dragNode;
    private TreeListNode? _dropTarget;
    private IBrush _barBrush = Brushes.LightSteelBlue;
    private IBrush _highlightBrush = Brushes.BlanchedAlmond;
    private bool _dropOnRoot;
    private int _resizingColumn = -1;
    private double _resizeStartX;
    private double _resizeStartWidth;
    private TextBox? _editor;
    private TreeListNode? _editing;
    private string _search = string.Empty;
    private DateTime _lastSearchKey;

    public TreeListControl()
    {
        Focusable = true;
        ClipToBounds = true;
        ActualThemeVariantChanged += (_, _) =>
        {
            _textCache.Clear();
            InvalidateVisual();
        };
    }

    public List<TreeListColumn> Columns { get; } = [];

    public double RowHeight { get; set; } = DefaultRowHeight;

    public bool ShowHeader { get; set; } = true;

    /// <summary>Groups (folders) before the other nodes in any sort order ("Folders at top").</summary>
    public bool GroupsFirst { get; set; } = true;

    /// <summary>Allows dragging nodes (the <see cref="DropRequested"/> handler decides).</summary>
    public bool AllowDrag { get; set; }

    /// <summary>Allows in-place rename with F2.</summary>
    public bool AllowRename { get; set; }

    /// <summary>Sort column (-1 = insertion order).</summary>
    public int SortColumn => _sortColumn;

    public bool SortDescending => _sortDescending;

    public IReadOnlyList<TreeListNode> Roots => _roots;

    /// <summary>Visible (flattened) nodes, in drawing order.</summary>
    public IReadOnlyList<TreeListNode> VisibleNodes => _visible;

    /// <summary>Bars on numeric columns (Preferences › General › Display bars).</summary>
    public bool ShowBars
    {
        get => _showBars;
        set
        {
            _showBars = value;
            InvalidateVisual();
        }
    }

    private bool _showBars = true;

    public TreeListNode? FocusedNode
    {
        get => _focused;
        set
        {
            if (ReferenceEquals(_focused, value))
            {
                return;
            }
            SetFocus(value, select: true);
        }
    }

    /// <summary>Multi-selection with Ctrl and Shift (Database tab object list, Host tab processes).</summary>
    public bool MultiSelect { get; set; }

    /// <summary>Selected nodes in drawing order; without multi-selection, the focused node.</summary>
    public IReadOnlyList<TreeListNode> SelectedNodes => MultiSelect
        ? _visible.Where(_selected.Contains).ToList()
        : _focused is null ? [] : [_focused];

    public event EventHandler? SelectionChanged;

    public bool CanCopy => SelectedNodes.Count > 0;

    public bool CanCut => false;

    public bool CanPaste => false;

    public bool CanSelectAll => MultiSelect && _visible.Count > 0;

    public bool CanUndo => false;

    /// <summary>Copy: the visible columns of the selected rows, separated by tabs, one line per node.</summary>
    public async Task CopyAsync()
    {
        var visible = Columns.Select((c, i) => (c, i)).Where(x => x.c.Visible).Select(x => x.i).ToList();
        var text = string.Join(Environment.NewLine, SelectedNodes.Select(n => string.Join("\t", visible.Select(i => n[i]))));
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
        }
    }

    public Task CutAsync() => Task.CompletedTask;

    public Task PasteAsync() => Task.CompletedTask;

    public void Undo()
    {
    }

    /// <summary>Selects every visible node (Ctrl+A).</summary>
    public void SelectAll()
    {
        if (!MultiSelect)
        {
            return;
        }
        _selected.Clear();
        _selected.UnionWith(_visible);
        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetFocus(TreeListNode? value, bool select, bool extend = false, bool toggle = false)
    {
        var focusChanged = !ReferenceEquals(_focused, value);
        _focused = value;
        if (select)
        {
            if (toggle && value is not null)
            {
                if (!_selected.Remove(value))
                {
                    _selected.Add(value);
                }
                _anchor = value;
            }
            else if (extend && value is not null && _anchor is not null && _visible.IndexOf(_anchor) is var from and >= 0)
            {
                var to = _visible.IndexOf(value);
                _selected.Clear();
                for (var i = Math.Min(from, to); i <= Math.Max(from, to); i++)
                {
                    _selected.Add(_visible[i]);
                }
            }
            else
            {
                _selected.Clear();
                if (value is not null)
                {
                    _selected.Add(value);
                }
                _anchor = value;
            }
        }
        if (value is not null)
        {
            ExpandParents(value);
            EnsureVisible(value);
        }
        InvalidateVisual();
        if (focusChanged)
        {
            FocusedNodeChanged?.Invoke(this, EventArgs.Empty);
        }
        if (select)
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public int TopRow
    {
        get => _topRow;
        set
        {
            var clamped = Math.Clamp(value, 0, MaxTopRow);
            if (clamped != _topRow)
            {
                _topRow = clamped;
                InvalidateVisual();
                ScrollChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public double ScrollX
    {
        get => _scrollX;
        set
        {
            var clamped = Math.Clamp(value, 0, MaxScrollX);
            if (Math.Abs(clamped - _scrollX) > 0.1)
            {
                _scrollX = clamped;
                InvalidateVisual();
                ScrollChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public int ViewportRows => Math.Max(1, (int)((Bounds.Height - (ShowHeader ? HeaderHeight : 0)) / RowHeight));

    public int MaxTopRow => Math.Max(0, _visible.Count - ViewportRows);

    public double TotalWidth => Columns.Where(c => c.Visible).Sum(c => c.Width);

    public double MaxScrollX => Math.Max(0, TotalWidth - Bounds.Width);

    public event EventHandler? FocusedNodeChanged;

    /// <summary>Double click or Enter on a node.</summary>
    public event EventHandler<TreeListNode>? NodeActivated;

    public event EventHandler<TreeListDropEventArgs>? DropRequested;

    public event EventHandler<TreeListRenameEventArgs>? RenameRequested;

    /// <summary>Right click on the header (columns menu).</summary>
    public event EventHandler<Point>? HeaderContextRequested;

    public event EventHandler? ScrollChanged;

    public event EventHandler? SortChanged;

    /// <summary>Replaces the nodes (keeps the focus if the same <see cref="TreeListNode.Tag"/> is still there).</summary>
    public void SetRoots(IEnumerable<TreeListNode> roots)
    {
        var selectedTags = _selected.Select(n => n.Tag).Where(t => t is not null).ToHashSet();
        var focusedTag = _focused?.Tag;
        _roots = [.. roots];
        ApplySort();
        Rebuild();
        if (focusedTag is not null)
        {
            _focused = AllNodes().FirstOrDefault(n => Equals(n.Tag, focusedTag));
        }
        else if (_focused is not null && !AllNodes().Contains(_focused))
        {
            _focused = null;
        }
        _selected.Clear();
        if (selectedTags.Count > 0)
        {
            _selected.UnionWith(AllNodes().Where(n => n.Tag is not null && selectedTags.Contains(n.Tag)));
        }
        else if (_focused is not null)
        {
            _selected.Add(_focused);
        }
        InvalidateVisual();
    }

    /// <summary>Row filter: matching nodes and their ancestors stay visible.</summary>
    public void SetFilter(Func<TreeListNode, bool>? filter)
    {
        _filter = filter;
        Rebuild();
        InvalidateVisual();
    }

    public void SortBy(int column, bool descending)
    {
        _sortColumn = column;
        _sortDescending = descending;
        ApplySort();
        Rebuild();
        InvalidateVisual();
        SortChanged?.Invoke(this, EventArgs.Empty);
    }

    public IEnumerable<TreeListNode> AllNodes() => _roots.SelectMany(r => new[] { r }.Concat(r.Descendants()));

    public void Expand(TreeListNode node, bool expanded = true)
    {
        if (node.Children.Count == 0 || node.IsExpanded == expanded)
        {
            return;
        }
        node.IsExpanded = expanded;
        Rebuild();
        InvalidateVisual();
    }

    public void BeginRename(TreeListNode? node = null)
    {
        node ??= _focused;
        if (!AllowRename || node is null)
        {
            return;
        }
        EnsureVisible(node);
        var row = _visible.IndexOf(node) - _topRow;
        var x = NameColumnTextLeft(node) - _scrollX;
        var width = Math.Max(80, FirstVisibleColumnWidth() - (x + _scrollX));
        _editing = node;
        _editor = new TextBox
        {
            Text = node[0],
            MinHeight = 0,
            Height = RowHeight + 2,
            Padding = new Thickness(2, 0),
            FontSize = 12,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _editor.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                CommitRename();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CancelRename();
            }
        };
        _editor.LostFocus += (_, _) => CommitRename();
        LogicalChildren.Add(_editor);
        VisualChildren.Add(_editor);
        _editor.Measure(new Size(width, RowHeight + 2));
        _editor.Arrange(new Rect(x, RowTop(row) - 1, width, RowHeight + 2));
        Dispatcher.UIThread.Post(() =>
        {
            _editor?.Focus();
            _editor?.SelectAll();
        });
    }

    public bool IsRenaming => _editor is not null;

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? TotalWidth : availableSize.Width, double.IsInfinity(availableSize.Height) ? 200 : availableSize.Height);

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_editor is not null && _editing is not null)
        {
            var row = _visible.IndexOf(_editing) - _topRow;
            var x = NameColumnTextLeft(_editing) - _scrollX;
            var width = Math.Max(80, FirstVisibleColumnWidth() - (x + _scrollX));
            _editor.Arrange(new Rect(x, RowTop(row) - 1, width, RowHeight + 2));
        }
        TopRow = _topRow;
        return finalSize;
    }

    public override void Render(DrawingContext context)
    {
        var theme = ThemeTokens.Of(ActualThemeVariant);
        var background = theme.Color("surface.content");
        var text = theme.Color("text.primary");
        var muted = theme.Color("text.muted");
        var header = theme.Color("surface.header");
        var line = theme.Color("grid.line");
        var accent = theme.Color("accent");
        var focusedBack = IsKeyboardFocusWithin ? theme.Color("selection.focus") : theme.Color("selection.inactive");
        var focusedText = IsKeyboardFocusWithin ? theme.Color("selection.focus.text") : text;
        _barBrush = new SolidColorBrush(theme.Color("tree.sizebar"));
        _highlightBrush = new SolidColorBrush(theme.Color("list.highlight"));
        context.FillRectangle(new SolidColorBrush(background), new Rect(Bounds.Size));

        var top = ShowHeader ? HeaderHeight : 0;
        var rows = Math.Min(_visible.Count - _topRow, ViewportRows + 1);
        using (context.PushClip(new Rect(0, top, Bounds.Width, Math.Max(0, Bounds.Height - top))))
        {
            for (var r = 0; r < rows; r++)
            {
                var node = _visible[_topRow + r];
                var y = RowTop(r);
                var rowRect = new Rect(0, y, Bounds.Width, RowHeight);
                var isFocused = ReferenceEquals(node, _focused);
                Color rowText;
                if (isFocused)
                {
                    context.FillRectangle(new SolidColorBrush(focusedBack), rowRect);
                    rowText = focusedText;
                }
                else if (MultiSelect && _selected.Contains(node))
                {
                    // Selected without focus: the unfocused selection colour (section 5.7).
                    context.FillRectangle(new SolidColorBrush(theme.Color("selection.row")), rowRect);
                    rowText = text;
                }
                else if (node.Background is { } rgb)
                {
                    // Session colour: a soft tint (the bar with the full colour is drawn next).
                    context.FillRectangle(new SolidColorBrush(Color.FromUInt32(ColorMath.SessionTint(rgb, background.ToUInt32()))), rowRect);
                    rowText = text;
                }
                else
                {
                    rowText = node.Muted ? muted : text;
                }
                if (node.Background is { } session)
                {
                    context.FillRectangle(new SolidColorBrush(Color.FromUInt32(session | 0xFF000000)), new Rect(0, y, 3, RowHeight));
                }
                if (ReferenceEquals(node, _dropTarget))
                {
                    context.DrawRectangle(new Pen(new SolidColorBrush(accent), 2), rowRect.Deflate(1));
                }
                DrawRow(context, node, y, rowText, line);
            }
            if (_dropOnRoot && _dragNode is not null)
            {
                context.DrawRectangle(new Pen(new SolidColorBrush(accent), 2), new Rect(1, top + 1, Bounds.Width - 2, Bounds.Height - top - 2));
            }
        }
        if (ShowHeader)
        {
            DrawHeader(context, header, text, line);
        }
    }

    private void DrawRow(DrawingContext context, TreeListNode node, double y, Color textColor, Color lineColor)
    {
        var x = -_scrollX;
        var first = true;
        for (var c = 0; c < Columns.Count; c++)
        {
            var column = Columns[c];
            if (!column.Visible)
            {
                continue;
            }
            var cell = new Rect(x, y, column.Width, RowHeight);
            if (node.Highlighted?.Contains(c) == true && !ReferenceEquals(node, _focused))
            {
                context.FillRectangle(_highlightBrush, cell);
            }
            if (ShowBars && node.Bars is { } bars && bars.TryGetValue(c, out var fraction) && fraction > 0)
            {
                var bar = new Rect(x + 1, y + 3, Math.Max(1, (column.Width - 2) * Math.Clamp(fraction, 0, 1)), RowHeight - 6);
                context.FillRectangle(_barBrush, bar);
            }
            var textLeft = x + CellPadding;
            if (first)
            {
                var indent = node.Depth * Indent;
                var glyphLeft = x + indent + 2;
                if (node.Children.Count > 0)
                {
                    DrawExpander(context, new Point(glyphLeft + 6, y + (RowHeight / 2)), node.IsExpanded, textColor);
                }
                textLeft = glyphLeft + 14;
                if (IconFactory.Get(node.Icon) is { } icon)
                {
                    context.DrawImage(icon, new Rect(textLeft, y + ((RowHeight - 16) / 2), 16, 16));
                    textLeft += 19;
                }
                first = false;
            }
            var value = node[c];
            if (value.Length > 0 && !(ReferenceEquals(node, _editing) && c == 0))
            {
                var available = Math.Max(1, cell.Right - CellPadding - textLeft);
                var formatted = Text(value, node.Bold && c == 0, textColor);
                formatted.MaxTextWidth = available;
                formatted.MaxLineCount = 1;
                formatted.Trimming = TextTrimming.CharacterEllipsis;
                var left = column.Alignment == TextAlignment.Right ? Math.Max(textLeft, cell.Right - CellPadding - formatted.WidthIncludingTrailingWhitespace) : textLeft;
                context.DrawText(formatted, new Point(left, y + ((RowHeight - formatted.Height) / 2)));
            }
            x += column.Width;
        }
    }

    private void DrawHeader(DrawingContext context, Color background, Color text, Color line)
    {
        context.FillRectangle(new SolidColorBrush(background), new Rect(0, 0, Bounds.Width, HeaderHeight));
        var pen = new Pen(new SolidColorBrush(line), 1);
        context.DrawLine(pen, new Point(0, HeaderHeight - 0.5), new Point(Bounds.Width, HeaderHeight - 0.5));
        var x = -_scrollX;
        for (var c = 0; c < Columns.Count; c++)
        {
            var column = Columns[c];
            if (!column.Visible)
            {
                continue;
            }
            var caption = column.Header + (c == _sortColumn ? (_sortDescending ? " ▼" : " ▲") : string.Empty);
            var formatted = Text(caption, false, text);
            formatted.MaxTextWidth = Math.Max(1, column.Width - (2 * CellPadding));
            formatted.MaxLineCount = 1;
            formatted.Trimming = TextTrimming.CharacterEllipsis;
            var left = column.Alignment == TextAlignment.Right ? x + column.Width - CellPadding - formatted.WidthIncludingTrailingWhitespace : x + CellPadding;
            context.DrawText(formatted, new Point(left, (HeaderHeight - formatted.Height) / 2));
            x += column.Width;
            context.DrawLine(pen, new Point(x - 0.5, 3), new Point(x - 0.5, HeaderHeight - 3));
        }
    }

    /// <summary>Expand arrow: "›" closed, "⌄" open (like the database tree).</summary>
    private static void DrawExpander(DrawingContext context, Point center, bool expanded, Color color)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(0xB0, color.R, color.G, color.B)), 1.4, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var cx = Math.Round(center.X);
        var cy = Math.Round(center.Y);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            if (expanded)
            {
                g.BeginFigure(new Point(cx - 3.5, cy - 1.5), false);
                g.LineTo(new Point(cx, cy + 2));
                g.LineTo(new Point(cx + 3.5, cy - 1.5));
            }
            else
            {
                g.BeginFigure(new Point(cx - 1.5, cy - 3.5), false);
                g.LineTo(new Point(cx + 2, cy));
                g.LineTo(new Point(cx - 1.5, cy + 3.5));
            }
            g.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geometry);
    }

    private FormattedText Text(string value, bool bold, Color color)
    {
        var key = (value, bold, (uint)color.ToUInt32());
        if (_textCache.TryGetValue(key, out var cached))
        {
            return cached;
        }
        if (_textCache.Count > 4000)
        {
            _textCache.Clear();
        }
        var text = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, bold ? FontWeight.Bold : FontWeight.Normal), 12, new SolidColorBrush(color));
        _textCache[key] = text;
        return text;
    }

    /// <summary>Black or white text depending on the luminance of the background colour (session colours).</summary>
    public static Color ContrastText(uint rgb)
    {
        var r = (rgb >> 16) & 0xFF;
        var g = (rgb >> 8) & 0xFF;
        var b = rgb & 0xFF;
        return (0.299 * r) + (0.587 * g) + (0.114 * b) > 150 ? Colors.Black : Colors.White;
    }

    private double RowTop(int visibleIndex) => (ShowHeader ? HeaderHeight : 0) + (visibleIndex * RowHeight);

    private double FirstVisibleColumnWidth() => Columns.FirstOrDefault(c => c.Visible)?.Width ?? Bounds.Width;

    private double NameColumnTextLeft(TreeListNode node) =>
        (node.Depth * Indent) + 2 + 14 + (IconFactory.Get(node.Icon) is null ? 0 : 19);

    private int RowAt(double y)
    {
        var top = ShowHeader ? HeaderHeight : 0;
        if (y < top)
        {
            return -1;
        }
        var row = _topRow + (int)((y - top) / RowHeight);
        return row < _visible.Count ? row : -1;
    }

    private int ColumnBoundaryAt(double x)
    {
        var edge = -_scrollX;
        for (var c = 0; c < Columns.Count; c++)
        {
            if (!Columns[c].Visible)
            {
                continue;
            }
            edge += Columns[c].Width;
            if (Math.Abs(x - edge) <= 3)
            {
                return c;
            }
        }
        return -1;
    }

    private int ColumnAt(double x)
    {
        var edge = -_scrollX;
        for (var c = 0; c < Columns.Count; c++)
        {
            if (!Columns[c].Visible)
            {
                continue;
            }
            if (x < edge + Columns[c].Width)
            {
                return c;
            }
            edge += Columns[c].Width;
        }
        return -1;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        if (ShowHeader && point.Y < HeaderHeight)
        {
            if (props.IsRightButtonPressed)
            {
                HeaderContextRequested?.Invoke(this, point);
                e.Handled = true;
                return;
            }
            var boundary = ColumnBoundaryAt(point.X);
            if (boundary >= 0)
            {
                _resizingColumn = boundary;
                _resizeStartX = point.X;
                _resizeStartWidth = Columns[boundary].Width;
                e.Pointer.Capture(this);
            }
            else if (ColumnAt(point.X) is var column and >= 0)
            {
                SortBy(column, column == _sortColumn && !_sortDescending);
            }
            e.Handled = true;
            return;
        }
        var row = RowAt(point.Y);
        if (row < 0)
        {
            return;
        }
        var node = _visible[row];
        var glyphLeft = (node.Depth * Indent) + 2 - _scrollX;
        if (node.Children.Count > 0 && point.X >= glyphLeft && point.X < glyphLeft + 14 && props.IsLeftButtonPressed)
        {
            Expand(node, !node.IsExpanded);
            e.Handled = true;
            return;
        }
        if (MultiSelect && props.IsRightButtonPressed && _selected.Contains(node))
        {
            // Right click on an already selected row keeps the selection (for the context menu).
            SetFocus(node, select: false);
        }
        else if (MultiSelect)
        {
            SetFocus(node, select: true, extend: e.KeyModifiers.HasFlag(KeyModifiers.Shift), toggle: PlatformKeys.IsCommand(e.KeyModifiers));
        }
        else
        {
            FocusedNode = node;
        }
        if (e.ClickCount == 2 && props.IsLeftButtonPressed)
        {
            if (node.IsGroup && node.Children.Count > 0)
            {
                Expand(node, !node.IsExpanded);
            }
            else
            {
                NodeActivated?.Invoke(this, node);
            }
            e.Handled = true;
            return;
        }
        if (props.IsLeftButtonPressed && AllowDrag)
        {
            _pressPoint = point;
            _pressNode = node;
        }
        e.Handled = !props.IsRightButtonPressed;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        if (_resizingColumn >= 0)
        {
            var column = Columns[_resizingColumn];
            column.Width = Math.Max(column.MinWidth, _resizeStartWidth + point.X - _resizeStartX);
            InvalidateVisual();
            return;
        }
        Cursor = ShowHeader && point.Y < HeaderHeight && ColumnBoundaryAt(point.X) >= 0 ? new Cursor(StandardCursorType.SizeWestEast) : null;
        if (_pressPoint is { } start && _pressNode is not null && _dragNode is null
            && (Math.Abs(point.X - start.X) > DragThreshold || Math.Abs(point.Y - start.Y) > DragThreshold))
        {
            _dragNode = _pressNode;
            e.Pointer.Capture(this);
        }
        if (_dragNode is not null)
        {
            var row = RowAt(point.Y);
            var target = row >= 0 ? _visible[row] : null;
            _dropOnRoot = target is null;
            _dropTarget = target is not null && !ReferenceEquals(target, _dragNode) ? target : null;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_resizingColumn >= 0)
        {
            _resizingColumn = -1;
            e.Pointer.Capture(null);
            return;
        }
        if (_dragNode is not null)
        {
            var source = _dragNode;
            var target = _dropTarget;
            var root = _dropOnRoot;
            _dragNode = null;
            _dropTarget = null;
            _dropOnRoot = false;
            e.Pointer.Capture(null);
            InvalidateVisual();
            if (target is not null || root)
            {
                DropRequested?.Invoke(this, new TreeListDropEventArgs(source, target));
            }
        }
        _pressPoint = null;
        _pressNode = null;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            ScrollX -= e.Delta.Y * 40;
        }
        else
        {
            TopRow -= (int)Math.Round(e.Delta.Y * 3);
        }
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _editor is not null)
        {
            return;
        }
        var index = _focused is null ? -1 : _visible.IndexOf(_focused);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (e.Key)
        {
            case Key.Down:
                MoveTo(index + 1, shift);
                break;
            case Key.Up:
                MoveTo(Math.Max(0, index - 1), shift);
                break;
            case Key.Home:
                MoveTo(0, shift);
                break;
            case Key.End:
                MoveTo(_visible.Count - 1, shift);
                break;
            case Key.PageDown:
                MoveTo(index + ViewportRows - 1, shift);
                break;
            case Key.PageUp:
                MoveTo(Math.Max(0, index - ViewportRows + 1), shift);
                break;
            case Key.A when MultiSelect && PlatformKeys.Normalize(e.KeyModifiers) == KeyModifiers.Control:
                SelectAll();
                break;
            case Key.Right or Key.Add when _focused is not null:
                if (_focused.Children.Count > 0 && !_focused.IsExpanded)
                {
                    Expand(_focused);
                }
                else if (e.Key == Key.Right && _focused.Children.Count > 0)
                {
                    MoveTo(index + 1);
                }
                break;
            case Key.Left or Key.Subtract when _focused is not null:
                if (_focused.IsExpanded)
                {
                    Expand(_focused, false);
                }
                else if (e.Key == Key.Left && _focused.Parent is { } parent)
                {
                    FocusedNode = parent;
                }
                break;
            case Key.Enter when _focused is not null:
                NodeActivated?.Invoke(this, _focused);
                break;
            case Key.F2 when AllowRename && _focused is not null:
                BeginRename();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (_editor is not null || string.IsNullOrEmpty(e.Text) || e.Text == "+" || e.Text == "-")
        {
            return;
        }
        // Incremental search on the first column; starts over after 1 s without keys.
        var now = DateTime.UtcNow;
        _search = now - _lastSearchKey > TimeSpan.FromSeconds(1) ? e.Text : _search + e.Text;
        _lastSearchKey = now;
        var start = _focused is null ? 0 : Math.Max(0, _visible.IndexOf(_focused));
        for (var i = 0; i < _visible.Count; i++)
        {
            var node = _visible[(start + i) % _visible.Count];
            if (node[0].StartsWith(_search, StringComparison.CurrentCultureIgnoreCase))
            {
                FocusedNode = node;
                break;
            }
        }
        e.Handled = true;
    }

    private void MoveTo(int index, bool extend = false)
    {
        if (_visible.Count == 0)
        {
            return;
        }
        SetFocus(_visible[Math.Clamp(index, 0, _visible.Count - 1)], select: true, extend: extend && MultiSelect);
    }

    private void CommitRename()
    {
        if (_editor is null || _editing is null)
        {
            return;
        }
        var node = _editing;
        var text = _editor.Text ?? string.Empty;
        RemoveEditor();
        if (text != node[0])
        {
            var args = new TreeListRenameEventArgs(node, text);
            RenameRequested?.Invoke(this, args);
        }
        Focus();
    }

    private void CancelRename()
    {
        RemoveEditor();
        Focus();
    }

    private void RemoveEditor()
    {
        if (_editor is null)
        {
            return;
        }
        var editor = _editor;
        _editor = null;
        _editing = null;
        VisualChildren.Remove(editor);
        LogicalChildren.Remove(editor);
        InvalidateVisual();
    }

    private void EnsureVisible(TreeListNode node)
    {
        var index = _visible.IndexOf(node);
        if (index < 0)
        {
            return;
        }
        if (index < _topRow)
        {
            TopRow = index;
        }
        else if (index >= _topRow + ViewportRows)
        {
            TopRow = index - ViewportRows + 1;
        }
    }

    private void ExpandParents(TreeListNode node)
    {
        var changed = false;
        for (var p = node.Parent; p is not null; p = p.Parent)
        {
            if (!p.IsExpanded)
            {
                p.IsExpanded = true;
                changed = true;
            }
        }
        if (changed)
        {
            Rebuild();
        }
    }

    private void ApplySort()
    {
        if (_sortColumn < 0 || _sortColumn >= Columns.Count)
        {
            return;
        }
        var column = Columns[_sortColumn];
        var compare = column.Compare ?? CompareNatural;
        Comparison<TreeListNode> comparison = (a, b) =>
        {
            if (GroupsFirst && a.IsGroup != b.IsGroup)
            {
                return a.IsGroup ? -1 : 1;
            }
            var result = CompareKeys(a, b, _sortColumn) ?? compare(a[_sortColumn], b[_sortColumn]);
            return _sortDescending ? -result : result;
        };
        _roots.Sort(comparison);
        foreach (var r in _roots)
        {
            r.Sort(comparison);
        }
    }

    /// <summary>Sort keys: with a key on both, compares them; with a key on only one, the empty one comes first.</summary>
    private static int? CompareKeys(TreeListNode a, TreeListNode b, int column)
    {
        IComparable? x = null;
        IComparable? y = null;
        a.SortKeys?.TryGetValue(column, out x);
        b.SortKeys?.TryGetValue(column, out y);
        if (x is null && y is null)
        {
            return null;
        }
        if (x is null || y is null)
        {
            return x is null ? -1 : 1;
        }
        return x.CompareTo(y);
    }

    /// <summary>Numbers as numbers, the rest as case-insensitive text.</summary>
    public static int CompareNatural(string a, string b)
    {
        if (double.TryParse(a, NumberStyles.Float, CultureInfo.CurrentCulture, out var x) && double.TryParse(b, NumberStyles.Float, CultureInfo.CurrentCulture, out var y))
        {
            return x.CompareTo(y);
        }
        return string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase);
    }

    private void Rebuild()
    {
        _visible.Clear();
        foreach (var root in _roots)
        {
            Add(root);
        }
        if (_focused is not null && !_visible.Contains(_focused))
        {
            _focused = null;
        }
        TopRow = _topRow;
        ScrollChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Add(TreeListNode node)
    {
        if (_filter is not null && !Matches(node))
        {
            return;
        }
        _visible.Add(node);
        if (node.IsExpanded || _filter is not null)
        {
            foreach (var child in node.Children)
            {
                Add(child);
            }
        }
    }

    private bool Matches(TreeListNode node) => _filter!(node) || node.Children.Any(Matches);
}

/// <summary><see cref="TreeListControl"/> with vertical and horizontal scrollbars.</summary>
public sealed class TreeListView : DockPanel
{
    private readonly ScrollBar _vertical = new() { Orientation = Orientation.Vertical, AllowAutoHide = false, SmallChange = 1 };
    private readonly ScrollBar _horizontal = new() { Orientation = Orientation.Horizontal, AllowAutoHide = false, SmallChange = 20 };
    private bool _syncing;

    public TreeListView()
    {
        SetDock(_horizontal, Dock.Bottom);
        SetDock(_vertical, Dock.Right);
        Children.Add(_horizontal);
        Children.Add(_vertical);
        Children.Add(List);
        List.ScrollChanged += (_, _) => Sync();
        List.SizeChanged += (_, _) => Sync();
        _vertical.ValueChanged += (_, e) =>
        {
            if (!_syncing)
            {
                List.TopRow = (int)Math.Round(e.NewValue);
            }
        };
        _horizontal.ValueChanged += (_, e) =>
        {
            if (!_syncing)
            {
                List.ScrollX = e.NewValue;
            }
        };
    }

    public TreeListControl List { get; } = new();

    private void Sync()
    {
        _syncing = true;
        try
        {
            _vertical.Maximum = List.MaxTopRow;
            _vertical.ViewportSize = List.ViewportRows;
            _vertical.LargeChange = Math.Max(1, List.ViewportRows - 1);
            _vertical.Value = List.TopRow;
            _vertical.IsVisible = List.MaxTopRow > 0;
            _horizontal.Maximum = List.MaxScrollX;
            _horizontal.ViewportSize = List.Bounds.Width;
            _horizontal.Value = List.ScrollX;
            _horizontal.IsVisible = List.MaxScrollX > 0;
        }
        finally
        {
            _syncing = false;
        }
    }
}
