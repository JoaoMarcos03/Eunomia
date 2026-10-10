using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Eunomia.UI.Controls.Common.Formatting;
using Eunomia.UI.Controls.Common.Theming;

namespace Eunomia.UI.Controls.Tree;

/// <summary>
/// Virtualised database tree, drawn row by row on a single visual element (section 15.3):
/// only visible rows are drawn, with the session colour as the background, the path to the focused node in
/// bold, a favourite star in the left margin and a Size column with a proportional bar (sections 4.2 and 5.6).
/// Keys: arrows, PgUp/PgDn, Home/End, ← collapses or goes up, → expands or goes down, + expands, - collapses,
/// * expands everything, incremental search while typing.
/// </summary>
public sealed class DatabaseTreeControl : Control
{
    /// <summary>Row height at 96 DPI (<c>size.row.tree</c>).</summary>
    public const double RowHeight = 21;
    public const double IndentWidth = 16;
    public const double StarWidth = 16;
    public const double GlyphWidth = 14;
    public const double IconWidth = 20;
    public const double SizeColumnWidth = 76;

    /// <summary>Gap between the end of the name and the right margin (or the Size column): about 1.5 characters.</summary>
    private double NamePadding => Math.Ceiling(FontSize * 0.85);

    private bool _showSizes = true;
    private static readonly TimeSpan SearchReset = TimeSpan.FromSeconds(1);

    private readonly Dictionary<TextKey, FormattedText> _textCache = [];
    private readonly Dictionary<uint, ImmutableSolidColorBrush> _brushes = [];
    private readonly Dictionary<TreeNode, long> _maxChildSize = [];
    private readonly HashSet<TreeNode> _clipped = [];
    private TreeModel? _model;
    private TreeNode? _focused;
    private TreeNode? _hover;
    private (PointerPressedEventArgs Press, Point Origin, TreeNode Node)? _dragStart;
    private int _topRow;
    private string _search = string.Empty;
    private DateTime _lastSearch;
    private TreePalette _palette = TreePalette.Light;
    private readonly Typeface _normal = new(UiFonts.Ui, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal);
    private readonly Typeface _bold = new(UiFonts.Ui, FontStyle.Normal, FontWeight.Bold, FontStretch.Normal);
    private readonly Typeface _italic = new(UiFonts.Ui, FontStyle.Italic, FontWeight.Normal, FontStretch.Normal);

    public DatabaseTreeControl()
    {
        Focusable = true;
        ClipToBounds = true;
        ActualThemeVariantChanged += (_, _) => ApplyTheme();
    }

    /// <summary>Font size in DIPs (Segoe UI 9 pt = 12 px).</summary>
    public double FontSize { get; set; } = 12;

    /// <summary>Display › Display size of objects: Size column visible (by default, as in HeidiSQL).</summary>
    /// <summary>The node's name was cut in the last drawing (it shows in a tooltip).</summary>
    public bool IsNameClipped(TreeNode node) => _clipped.Contains(node);

    public bool ShowSizes
    {
        get => _showSizes;
        set
        {
            _showSizes = value;
            InvalidateVisual();
        }
    }

    public CultureInfo Culture { get; set; } = CultureInfo.CurrentCulture;

    public TreeModel? Model
    {
        get => _model;
        set
        {
            if (_model is not null)
            {
                _model.Changed -= OnModelChanged;
            }
            _model = value;
            if (_model is not null)
            {
                _model.Changed += OnModelChanged;
            }
            _focused = null;
            _topRow = 0;
            OnModelChanged(this, EventArgs.Empty);
        }
    }

    public TreeNode? FocusedNode
    {
        get => _focused;
        set
        {
            if (ReferenceEquals(_focused, value))
            {
                return;
            }
            _focused = value;
            EnsureVisible(value);
            InvalidateVisual();
            FocusedNodeChanged?.Invoke(this, EventArgs.Empty);
            if (AutoExpand && value is { CanExpand: true, IsExpanded: false } && _model is not null)
            {
                _ = ExpandQuietlyAsync(value);
            }
        }
    }

    /// <summary>
    /// In-process format of the tree drag (appendix D A12): only the query editor accepts it, and it decides the text on drop
    /// (Shift qualifies with the database).
    /// </summary>
    public static DataFormat<TreeNode> NodeFormat { get; } = DataFormat.CreateInProcessFormat<TreeNode>("Eunomia.TreeNode");

    private static void StartDrag(PointerPressedEventArgs press, TreeNode node)
    {
        if (node.Kind is not (TreeNodeKind.Database or TreeNodeKind.Column) && !node.IsObject)
        {
            return;
        }
        var item = new DataTransferItem();
        item.Set(NodeFormat, node);
        var transfer = new DataTransfer();
        transfer.Add(item);
        _ = DragDrop.DoDragDropAsync(press, transfer, DragDropEffects.Copy);
    }

    /// <summary>Tree style options › Auto expand on click: the selected node expands (appendix D A3; off by default).</summary>
    public bool AutoExpand { get; set; }

    private async Task ExpandQuietlyAsync(TreeNode node, bool all = false)
    {
        try
        {
            await (all ? _model!.ExpandAllAsync(node) : _model!.ExpandAsync(node)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ExpandFailed?.Invoke(this, ex);
        }
    }

    /// <summary>An expand requested by the tree (keyboard, mouse, auto expand) failed; the message goes to the SQL log.</summary>
    public event EventHandler<Exception>? ExpandFailed;

    /// <summary>First visible row (index in <see cref="TreeModel.Visible"/>).</summary>
    public int TopRow
    {
        get => _topRow;
        set
        {
            var clamped = Math.Clamp(value, 0, MaxTopRow);
            if (clamped == _topRow)
            {
                return;
            }
            _topRow = clamped;
            InvalidateVisual();
            ScrollChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public int ViewportRows => Math.Max(1, (int)(Bounds.Height / RowHeight));

    public int RowCount => _model?.Visible.Count ?? 0;

    public int MaxTopRow => Math.Max(0, RowCount - ViewportRows);

    public event EventHandler? FocusedNodeChanged;

    /// <summary>Double click on a node (outside the star and the expand button).</summary>
    public event EventHandler<TreeNodeDoubleClickEventArgs>? NodeDoubleClicked;

    /// <summary>The scroll, the row count or the viewport size changed.</summary>
    public event EventHandler? ScrollChanged;

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        context.FillRectangle(_palette.Background, new Rect(bounds.Size));
        if (_model is null)
        {
            return;
        }

        var visible = _model.Visible;
        var last = Math.Min(visible.Count, _topRow + ViewportRows + 1);
        _maxChildSize.Clear();
        _clipped.Clear();
        for (var i = _topRow; i < last; i++)
        {
            DrawRow(context, visible[i], (i - _topRow) * RowHeight, bounds.Width);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyTheme();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsKeyboardFocusWithinProperty)
        {
            InvalidateVisual();
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _topRow = Math.Clamp(_topRow, 0, MaxTopRow);
        EnsureVisible(_focused);
        ScrollChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_model is null || RowCount == 0)
        {
            return;
        }
        var index = _focused is null ? -1 : _model.IndexOf(_focused);
        var node = _focused;
        switch (e.Key)
        {
            case Key.Down:
                Focus(index + 1);
                break;
            case Key.Up:
                Focus(Math.Max(0, index - 1));
                break;
            case Key.PageDown:
                Focus(index + ViewportRows - 1);
                break;
            case Key.PageUp:
                Focus(Math.Max(0, index - ViewportRows + 1));
                break;
            case Key.Home:
                Focus(0);
                break;
            case Key.End:
                Focus(RowCount - 1);
                break;
            case Key.Right when node is not null:
                if (node.CanExpand && !node.IsExpanded)
                {
                    _ = ExpandQuietlyAsync(node);
                }
                else if (node.IsExpanded)
                {
                    Focus(index + 1);
                }
                break;
            case Key.Left when node is not null:
                if (node.IsExpanded)
                {
                    _model.Collapse(node);
                }
                else if (node.Parent is not null)
                {
                    FocusedNode = node.Parent;
                }
                break;
            case Key.Add or Key.OemPlus when node is not null:
                _ = ExpandQuietlyAsync(node);
                break;
            case Key.Subtract or Key.OemMinus when node is not null:
                _model.Collapse(node);
                break;
            case Key.Multiply when node is not null:
                _ = ExpandQuietlyAsync(node, all: true);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (_model is null || string.IsNullOrEmpty(e.Text) || e.Text is "+" or "-" or "*")
        {
            return;
        }
        var now = DateTime.UtcNow;
        _search = now - _lastSearch > SearchReset ? e.Text : _search + e.Text;
        _lastSearch = now;
        var start = _focused is null ? 0 : _model.IndexOf(_focused);
        var found = _model.FindByPrefix(_search, Math.Max(0, start));
        if (found >= 0)
        {
            Focus(found);
        }
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetCurrentPoint(this);
        _dragStart = null;
        if (_model is null)
        {
            return;
        }
        var row = RowAt(point.Position.Y);
        if (point.Properties.IsRightButtonPressed)
        {
            // toRightClickSelect: the right button selects the node before opening popupDB.
            if (row >= 0)
            {
                FocusedNode = _model.Visible[row];
            }
            return;
        }
        if (!point.Properties.IsLeftButtonPressed || row < 0)
        {
            return;
        }
        var node = _model.Visible[row];
        var x = point.Position.X;
        var glyphLeft = StarWidth + (node.Depth * IndentWidth);
        if (x < StarWidth && node.IsObject)
        {
            _model.ToggleFavorite(node);
        }
        else if (x >= glyphLeft && x < glyphLeft + GlyphWidth && node.CanExpand)
        {
            Toggle(node);
        }
        else if (e.ClickCount == 2)
        {
            FocusedNode = node;
            // "Doubleclick inserts node text" (4.2, FAM-T07): whoever handles the event decides; otherwise it expands/collapses.
            var args = new TreeNodeDoubleClickEventArgs(node, e.KeyModifiers);
            NodeDoubleClicked?.Invoke(this, args);
            if (!args.Handled && node.CanExpand)
            {
                Toggle(node);
            }
            e.Handled = true;
            return;
        }
        FocusedNode = node;
        _dragStart = (e, point.Position, node);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragStart = null;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragStart is { } drag && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var delta = e.GetPosition(this) - drag.Origin;
            if (Math.Abs(delta.X) + Math.Abs(delta.Y) > 4)
            {
                _dragStart = null;
                StartDrag(drag.Press, drag.Node);
                return;
            }
        }
        var row = RowAt(e.GetPosition(this).Y);
        var node = row >= 0 ? _model!.Visible[row] : null;
        if (!ReferenceEquals(node, _hover))
        {
            _hover = node;
            // Tooltip with just the node's text, and only when it is cut (appendix D A14).
            ToolTip.SetTip(this, node is not null && _clipped.Contains(node) ? node.DisplayText : null);
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        TopRow -= (int)Math.Round(e.Delta.Y * 3);
        e.Handled = true;
    }

    private void Toggle(TreeNode node)
    {
        if (node.IsExpanded)
        {
            _model!.Collapse(node);
        }
        else
        {
            _ = ExpandQuietlyAsync(node);
        }
    }

    private void Focus(int index)
    {
        if (_model is null || RowCount == 0)
        {
            return;
        }
        FocusedNode = _model.Visible[Math.Clamp(index, 0, RowCount - 1)];
    }

    private int RowAt(double y)
    {
        if (_model is null || y < 0)
        {
            return -1;
        }
        var row = _topRow + (int)(y / RowHeight);
        return row < RowCount ? row : -1;
    }

    private void EnsureVisible(TreeNode? node)
    {
        // Before the first layout there is no viewport; OnSizeChanged calls this again.
        if (_model is null || node is null || Bounds.Height <= 0)
        {
            return;
        }
        var index = _model.IndexOf(node);
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

    private void OnModelChanged(object? sender, EventArgs e)
    {
        if (_focused is not null && _model is not null && _model.IndexOf(_focused) < 0)
        {
            _focused = null;
        }
        _topRow = Math.Clamp(_topRow, 0, MaxTopRow);
        if (_textCache.Count > 4096)
        {
            _textCache.Clear();
        }
        InvalidateVisual();
        ScrollChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyTheme()
    {
        _palette = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? TreePalette.Dark : TreePalette.Light;
        _textCache.Clear();
        InvalidateVisual();
    }

    private void DrawRow(DrawingContext context, TreeNode node, double y, double width)
    {
        var row = new Rect(0, y, width, RowHeight);
        IImmutableBrush foreground = _palette.Foreground;
        var sessionColor = node.Session?.SessionColor ?? 0;
        if (sessionColor != 0)
        {
            // Session colour (section 5.6): a soft tint on the row and a bar with the full colour in the left margin.
            context.FillRectangle(BrushFor(ColorMath.SessionTint(sessionColor, _palette.BackgroundArgb)), row);
            context.FillRectangle(BrushFor(sessionColor | 0xFF000000), new Rect(0, y, 3, RowHeight));
        }
        if (node.Kind == TreeNodeKind.Column && node.TextCategory >= 0 && node.TextCategory < _palette.Categories.Count)
        {
            foreground = _palette.Categories[node.TextCategory];
        }
        // Nodes of sessions that are not the active one in grey (appendix D A6).
        if (_focused?.Session is { } active && node.Session is { } own && !ReferenceEquals(active, own))
        {
            foreground = _palette.Muted;
        }

        var focused = ReferenceEquals(node, _focused);
        if (focused)
        {
            // Focused node: selection colour with the tree focused, grey without focus (like Windows lists).
            var treeFocused = IsKeyboardFocusWithin;
            context.FillRectangle(treeFocused ? _palette.Selection : _palette.InactiveSelection, row);
            if (sessionColor != 0)
            {
                context.FillRectangle(BrushFor(sessionColor | 0xFF000000), new Rect(0, y, 3, RowHeight));
            }
            if (treeFocused)
            {
                foreground = _palette.SelectionForeground;
            }
        }
        else if (ReferenceEquals(node, _hover))
        {
            context.FillRectangle(_palette.Hover, row);
        }

        // Favourite star in the left margin (filled if favourite, hollow on hover).
        if (node.IsObject && (node.IsFavorite || ReferenceEquals(node, _hover)))
        {
            DrawStar(context, new Point(StarWidth / 2, y + (RowHeight / 2)), node.IsFavorite);
        }

        var x = StarWidth + (node.Depth * IndentWidth);
        if (node.CanExpand)
        {
            DrawExpandGlyph(context, new Point(x + (GlyphWidth / 2), y + (RowHeight / 2)), node.IsExpanded, focused ? foreground : _palette.Glyph);
        }
        x += GlyphWidth;
        var iconRect = new Rect(x + 2, y + 4, 13, 13);
        var onColor = false;
        if (node.IsGhosted)
        {
            using (context.PushOpacity(0.4))
            {
                DrawIcon(context, node, iconRect, onColor);
            }
        }
        else
        {
            DrawIcon(context, node, iconRect, onColor);
        }
        DrawOverlays(context, node, iconRect);
        x += IconWidth;

        // The Size column only takes space on rows that have a size to show; on the others the name goes up to near the
        // right margin, with a gap of about 1.5 characters.
        var sizeLeft = width;
        if (ShowSizes && node.SizeBytes is { } size)
        {
            sizeLeft = width - SizeColumnWidth;
            DrawSize(context, node, size, new Rect(sizeLeft, y, SizeColumnWidth, RowHeight), focused ? foreground : null);
        }

        var typeface = node.Kind == TreeNodeKind.Placeholder ? _italic : IsOnFocusedPath(node) ? _bold : _normal;
        var text = TextFor(node.DisplayText, typeface, foreground);
        var available = Math.Max(1, sizeLeft - x - NamePadding);
        if (text.WidthIncludingTrailingWhitespace > available)
        {
            _clipped.Add(node);
        }
        text.MaxTextWidth = available;
        text.Trimming = TextTrimming.CharacterEllipsis;
        text.MaxLineCount = 1;
        context.DrawText(text, new Point(x, y + ((RowHeight - text.Height) / 2)));
    }

    /// <summary>Marker of the database in <c>USE</c> and the table's engine overlay, in the icon's corner (appendix D A2).</summary>
    private void DrawOverlays(DrawingContext context, TreeNode node, Rect icon)
    {
        if (node.IsCurrent)
        {
            var dot = new Rect(icon.Right - 5, icon.Bottom - 5, 6, 6);
            context.DrawEllipse(BrushFor(0xFF2ECC40), new ImmutablePen(BrushFor(0xFFFFFFFF), 1), dot);
        }
        if (node.EngineOverlay is { Length: > 0 } engine)
        {
            var badge = new Rect(icon.Right - 6, icon.Bottom - 6, 8, 8);
            context.FillRectangle(BrushFor(EngineColor(engine)), badge);
            var letter = TextFor(engine[..1], _bold, BrushFor(0xFFFFFFFF), 7);
            context.DrawText(letter, new Point(badge.Center.X - (letter.Width / 2), badge.Center.Y - (letter.Height / 2)));
        }
    }

    private static uint EngineColor(string engine) => engine.ToUpperInvariant() switch
    {
        "MEMORY" or "HEAP" => 0xFF16A085,
        "CSV" => 0xFF7F8C8D,
        "FEDERATED" => 0xFF8E44AD,
        "ARIA" => 0xFFD35400,
        "PERFORMANCE_SCHEMA" => 0xFF2C3E50,
        "BLACKHOLE" => 0xFF000000,
        _ => 0xFFC0392B,
    };

    /// <summary>
    /// Size column: grey text on the right; proportional bar only on objects, relative to the largest object in the database
    /// (appendix D A2, A8).
    /// </summary>
    private void DrawSize(DrawingContext context, TreeNode node, long size, Rect cell, IImmutableBrush? textBrush)
    {
        if (node.IsObject && node.Database is { } database)
        {
            if (!_maxChildSize.TryGetValue(database, out var max))
            {
                max = 0;
                foreach (var obj in TreeModel.ObjectsOf(database))
                {
                    max = Math.Max(max, obj.SizeBytes ?? 0);
                }
                _maxChildSize[database] = max;
            }
            if (max > 0)
            {
                var barWidth = (cell.Width - 4) * size / max;
                context.FillRectangle(_palette.SizeBar, new Rect(cell.X + 2, cell.Y + 3, Math.Max(1, barWidth), cell.Height - 6));
            }
        }
        var text = TextFor(SizeFormatter.Format(size, Culture), _normal, textBrush ?? _palette.Muted);
        context.DrawText(text, new Point(cell.Right - text.Width - 4, cell.Y + ((cell.Height - text.Height) / 2)));
    }

    private bool IsOnFocusedPath(TreeNode node)
    {
        for (var n = _focused; n is not null; n = n.Parent)
        {
            if (ReferenceEquals(n, node))
            {
                return true;
            }
        }
        return false;
    }

    private FormattedText TextFor(string text, Typeface typeface, IImmutableBrush brush, double? size = null)
    {
        var key = new TextKey(text, typeface, brush, size ?? FontSize);
        if (!_textCache.TryGetValue(key, out var formatted))
        {
            formatted = new FormattedText(text, Culture, FlowDirection.LeftToRight, typeface, key.Size, brush);
            _textCache[key] = formatted;
        }
        return formatted;
    }

    private ImmutableSolidColorBrush BrushFor(uint argb)
    {
        if (!_brushes.TryGetValue(argb, out var brush))
        {
            brush = new ImmutableSolidColorBrush(argb);
            _brushes[argb] = brush;
        }
        return brush;
    }

    /// <summary>Expand arrow: "›" closed, "⌄" open.</summary>
    private static void DrawExpandGlyph(DrawingContext context, Point center, bool expanded, IImmutableBrush brush)
    {
        var pen = new ImmutablePen(brush, 1.4, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
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

    private void DrawStar(DrawingContext context, Point center, bool filled)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            for (var i = 0; i < 10; i++)
            {
                var radius = i % 2 == 0 ? 6.0 : 2.6;
                var angle = (-Math.PI / 2) + (i * Math.PI / 5);
                var p = new Point(center.X + (radius * Math.Cos(angle)), center.Y + (radius * Math.Sin(angle)));
                if (i == 0)
                {
                    g.BeginFigure(p, true);
                }
                else
                {
                    g.LineTo(p);
                }
            }
            g.EndFigure(true);
        }
        context.DrawGeometry(filled ? _palette.Star : null, new ImmutablePen(_palette.Star, 1), geometry);
    }

    /// <summary>
    /// Simple vector icons of our own (not Icons8), one per node kind. Groups use the kind's icon; columns, a key
    /// coloured by the index kind or a plain field.
    /// </summary>
    private void DrawIcon(DrawingContext context, TreeNode node, Rect r, bool onColor)
    {
        var kind = node.Kind == TreeNodeKind.Group && node.GroupOf != TreeNodeKind.Group ? node.GroupOf : node.Kind;
        if (kind == TreeNodeKind.Column)
        {
            DrawColumnIcon(context, node.KeyMarker, r);
            return;
        }
        uint color = kind switch
        {
            TreeNodeKind.Session => 0xFF3A7BD5,
            TreeNodeKind.Database => 0xFFD08A1E,
            TreeNodeKind.Group => 0xFF8C8C8C,
            TreeNodeKind.Table => 0xFF2E86C1,
            TreeNodeKind.View => 0xFF8E44AD,
            TreeNodeKind.Procedure or TreeNodeKind.Function => 0xFF27AE60,
            TreeNodeKind.Trigger or TreeNodeKind.Event => 0xFFC0392B,
            _ => 0,
        };
        if (color == 0)
        {
            return;
        }
        var fill = BrushFor(color);
        // Outline: the icon colour darkened (readable in light and dark).
        var edge = new ImmutablePen(onColor ? BrushFor(0xFFFFFFFF) : BrushFor(ColorMath.Mix(color, 0xFF000000, 0.65)), 1);
        switch (kind)
        {
            case TreeNodeKind.Database:
                context.DrawRectangle(fill, edge, new Rect(r.X + 1, r.Y + 2, r.Width - 2, r.Height - 3), 2, 2);
                context.DrawEllipse(fill, edge, new Rect(r.X + 1, r.Y, r.Width - 2, 4));
                break;
            case TreeNodeKind.Table or TreeNodeKind.View or TreeNodeKind.Group:
                context.DrawRectangle(fill, edge, new Rect(r.X, r.Y + 1, r.Width, r.Height - 2));
                var line = new ImmutablePen(BrushFor(0xFFFFFFFF), 1);
                context.DrawLine(line, new Point(r.X + 1, r.Y + 5.5), new Point(r.Right - 1, r.Y + 5.5));
                context.DrawLine(line, new Point(r.X + 1, r.Y + 8.5), new Point(r.Right - 1, r.Y + 8.5));
                context.DrawLine(line, new Point(r.X + 5.5, r.Y + 2), new Point(r.X + 5.5, r.Bottom - 2));
                break;
            case TreeNodeKind.Session:
                context.DrawRectangle(fill, edge, new Rect(r.X, r.Y + 1, r.Width, r.Height - 5));
                context.DrawRectangle(fill, null, new Rect(r.X + 3, r.Bottom - 3, r.Width - 6, 2));
                break;
            default:
                context.DrawEllipse(fill, edge, r.Deflate(1));
                break;
        }
    }

    private void DrawColumnIcon(DrawingContext context, ColumnKeyMarker marker, Rect r)
    {
        if (marker == ColumnKeyMarker.None)
        {
            // Field: a narrow rectangle with a line.
            var field = new Rect(r.X + 2, r.Y + 3, r.Width - 4, r.Height - 6);
            context.DrawRectangle(BrushFor(0xFFDDE6EE), new ImmutablePen(BrushFor(0xFF6B7C88), 1), field);
            return;
        }
        uint color = marker switch
        {
            ColumnKeyMarker.Primary => 0xFFE8B400,
            ColumnKeyMarker.Unique => 0xFFD35400,
            ColumnKeyMarker.FullText => 0xFF27AE60,
            ColumnKeyMarker.Spatial => 0xFF8E44AD,
            _ => 0xFF2E86C1,
        };
        // Key: ring and shaft.
        var brush = BrushFor(color);
        var pen = new ImmutablePen(brush, 2);
        context.DrawEllipse(null, pen, new Rect(r.X + 1, r.Y + 1, 6, 6));
        context.DrawLine(pen, new Point(r.X + 6, r.Y + 6), new Point(r.Right - 1, r.Bottom - 1));
        context.DrawLine(pen, new Point(r.Right - 4, r.Bottom - 4), new Point(r.Right - 1, r.Bottom - 7));
    }

    private readonly record struct TextKey(string Text, Typeface Typeface, IImmutableBrush Brush, double Size);
}

/// <summary>Double click on a tree node. <see cref="Handled"/> prevents the default expand/collapse.</summary>
public sealed class TreeNodeDoubleClickEventArgs(TreeNode node, KeyModifiers modifiers) : EventArgs
{
    public TreeNode Node { get; } = node;

    public KeyModifiers Modifiers { get; } = modifiers;

    public bool Handled { get; set; }
}
