using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Eunomia.Engine.Results;
using Eunomia.UI.Controls.Common.Input;

namespace Eunomia.UI.Controls.Grid;

/// <summary>
/// Eunomia's own data grid (section 15): drawn cell by cell on a single visual element, with row and column
/// virtualisation, no per-row objects. Row selection + a focused cell, in-place editing with a
/// <see cref="TextBox"/> over the cell (IME and the text control's accessibility), the section 4.4 keymap, a fixed
/// <c>#</c> column, a "no. name" header with a key icon, colours by type, <c>(NULL)</c>, PK in bold, numbers aligned
/// right, Ctrl+C of the focused cell (D10).
/// </summary>
public sealed partial class DataGridControl : Control, IEditTarget
{
    public const double CellPadding = 4;
    public const int RowNumberColumn = 4095;

    /// <summary>Block columns already in memory that a frame may format on the spot (the rest comes from the background prefetch).</summary>
    private const int SyncFormatBudget = 24;

    private static readonly TimeSpan SearchReset = TimeSpan.FromSeconds(1);
    private static bool _clipboardIsNull;

    private readonly CellTextCache _textCache = new(12_000);
    private static readonly Cursor ResizeCursor = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor AutoScrollCursor = new(StandardCursorType.SizeAll);
    private readonly GridAutoScroll _autoScroll = new();
    private TimeSpan _lastAutoScrollFrame;
    private readonly Dictionary<int, HeaderLayouts> _headerCache = [];
    private long _lastRowCount;
    private readonly Dictionary<string, Avalonia.Media.TextFormatting.TextLayout> _sortGlyphCache = new(StringComparer.Ordinal);
    private ImmutableSolidColorBrush? _sessionBrush;
    private uint _sessionColor;
    private IRowSource? _source;
    private FormattedCellCache? _cells;
    private GridPalette _palette = GridPalette.Light;
    private Typeface _typeface;
    private GlyphRunFactory? _glyphs;
    private GlyphRunFactory? _boldGlyphs;
    private Typeface _boldTypeface;
    private double _fontSize = 8 * 96.0 / 72.0;
    private long _topRow;
    private double _scrollX;
    private long _prefetchFrom = -1;
    private long _prefetchTo = -1;
    private int _prefetchFirstColumn = -1;
    private int _prefetchLastColumn = -1;
    private bool _invalidatePosted;
    private Control? _editor;
    private GridCellEditor? _editorSpec;
    private long _editRow = -1;
    private int _editColumn = -1;
    private bool _editOriginalNull;
    private string _editOriginalText = string.Empty;
    private string _search = string.Empty;
    private DateTime _lastSearch;
    private int _resizeColumn = -1;
    private double _resizeStartX;
    private double _resizeStartWidth;
    private int _syncFormatBudget;

    public DataGridControl()
    {
        Focusable = true;
        ClipToBounds = true;
        _typeface = new Typeface(_fontFamilyName);
        _boldTypeface = new Typeface(_fontFamilyName, FontStyle.Normal, FontWeight.Bold);
        ActualThemeVariantChanged += (_, _) => ApplyTheme();
        Selection.Changed += (_, _) =>
        {
            InvalidateVisual();
            StatusChanged?.Invoke(this, EventArgs.Empty);
        };
        Edits.RowCommitted += (_, _) => InvalidateVisual();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    public GridSelection Selection { get; } = new();

    public GridEditBuffer Edits { get; } = new();

    public GridColumns Columns { get; } = new();

    public GridKeymap Keymap { get; } = new();

    public CultureInfo Culture { get; set; } = CultureInfo.CurrentCulture;

    /// <summary>Grid font name (<c>font.grid</c>: Tahoma 8 pt; Preferences › Grid formatting).</summary>
    public string FontFamilyName
    {
        get => _fontFamilyName;
        set
        {
            _fontFamilyName = string.IsNullOrWhiteSpace(value) ? "Tahoma" : value;
            _typeface = new Typeface(_fontFamilyName);
            _boldTypeface = new Typeface(_fontFamilyName, FontStyle.Normal, FontWeight.Bold);
            _glyphs = null;
            _boldGlyphs = null;
            _textCache.Clear();
            ClearHeaderCache();
            InvalidateVisual();
        }
    }

    private string _fontFamilyName = "Tahoma";

    /// <summary>Maximum column width for automatic sizing (Preferences › Grid formatting › Maximum column width).</summary>
    public double MaxAutoColumnWidth { get; set; } = GridColumns.MaxAutoWidth;

    /// <summary>Font size in DIPs (8 pt = 10.67 px). Ctrl+wheel zooms.</summary>
    public double FontSize
    {
        get => _fontSize;
        set
        {
            _fontSize = Math.Clamp(value, 6, 40);
            _glyphs = null;
            _boldGlyphs = null;
            _textCache.Clear();
            ClearHeaderCache();
            InvalidateVisual();
            ScrollChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Fixed row height: text height + 7 px, like HeidiSQL (18 px with Tahoma 8 pt).</summary>
    public double RowHeight => Math.Ceiling(_fontSize) + 7;

    public double HeaderHeight => RowHeight;

    /// <summary>Session colour in ARGB, background of the <c>#</c> column. 0 = theme colour.</summary>
    public uint SessionColor
    {
        get => _sessionColor;
        set
        {
            _sessionColor = value;
            _sessionBrush = value == 0 ? null : new ImmutableSolidColorBrush(value | 0xFF000000);
            InvalidateVisual();
        }
    }

    public IRowSource? Source => _source;

    public FormattedCellCache? Cells => _cells;

    public long SourceRows => _source?.RowCount.Count ?? 0;

    /// <summary>Source rows plus new rows.</summary>
    public long RowCount => SourceRows + Edits.InsertedRows;

    public long TopRow
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

    public double ScrollX
    {
        get => _scrollX;
        set
        {
            var clamped = Math.Clamp(value, 0, MaxScrollX);
            if (clamped == _scrollX)
            {
                return;
            }
            _scrollX = clamped;
            InvalidateVisual();
            ScrollChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public double RowNumberWidth { get; private set; } = 40;

    public int ViewportRows => Math.Max(1, (int)((Bounds.Height - HeaderHeight) / RowHeight));

    public double DataViewportWidth => Math.Max(0, Bounds.Width - RowNumberWidth);

    public long MaxTopRow => Math.Max(0, RowCount - ViewportRows);

    public double MaxScrollX => Math.Max(0, Columns.TotalWidth - DataViewportWidth);

    public bool IsEditing => _editor is not null;

    /// <summary>Text of the open in-place editor (for tests).</summary>
    public TextBox? Editor => _editor as TextBox;

    /// <summary>Control of the open editor (text, list, SET, date/time).</summary>
    public Control? EditorControl => _editor;

    /// <summary>
    /// Per-cell editor (FAM-G07): receives the row, the column and the current value; <c>null</c> = plain text. Text with 256
    /// characters or more, or with line breaks, opens the window editor (<see cref="PopupEditRequested"/>).
    /// </summary>
    public Func<long, int, string?, Task<GridCellEditor?>>? EditorProvider { get; set; }

    /// <summary>Request for the window editor (long text, "…", binary): the grid's user shows it and sets the value.</summary>
    public event EventHandler<GridPopupEditEventArgs>? PopupEditRequested;

    /// <summary>UI thread time spent in the last <see cref="Render"/> (for benchmarks).</summary>
    public TimeSpan LastRenderTime { get; private set; }

    /// <summary><see cref="Stopwatch.GetTimestamp"/> at the end of the last <see cref="Render"/> (for benchmarks).</summary>
    public long LastRenderEndTimestamp { get; private set; }

    /// <summary>Cell texts prepared since the start (for benchmarks).</summary>
    public long TextLayoutsCreated => _textCache.Created;

    /// <summary>Of those, how many needed full shaping (<c>TextLayout</c>).</summary>
    public long TextLayoutsShaped => _textCache.CreatedWithShaping;

    /// <summary>Free scroll with the middle button (for tests).</summary>
    public GridAutoScroll AutoScroll => _autoScroll;

    public event EventHandler? ScrollChanged;

    public event EventHandler? StatusChanged;

    /// <summary>PgDn on the last row: a request for "Next" (more rows), as in HeidiSQL's Data tab.</summary>
    public event EventHandler? NextPageRequested;

    /// <summary>Automatic sizing in progress (the one from <see cref="SetSource"/> or from the arrival of the first rows).</summary>
    public Task PendingAutoSize { get; private set; } = Task.CompletedTask;

    /// <summary>Ctrl+End (last row): in the Data tab, a request for "Show all".</summary>
    public event EventHandler? LastRowRequested;

    /// <summary>No editing and no new rows (a result without a sufficient key, or without a source table).</summary>
    public bool IsReadOnly { get; set; }

    /// <summary>
    /// Saves the pending row to the server (FAM-G08): returns whether it saved. Without a handler, the save stays in the edit
    /// buffer only. A failure keeps the row changed (triangles) and the focus goes back to it.
    /// </summary>
    public Func<GridRowCommit, Task<GridCommitResult>>? CommitHandler { get; set; }

    /// <summary>A save is running (editing waits for it).</summary>
    public bool IsCommitting { get; private set; }

    /// <summary>Attempt to edit a read-only grid (the grid's user explains why).</summary>
    public event EventHandler? EditRefused;

    /// <summary>Saves the pending row (Ctrl+Enter, moving to another row, click on empty space).</summary>
    public async Task<bool> CommitPendingAsync()
    {
        if (!Edits.HasPending)
        {
            return true;
        }
        if (CommitHandler is not { } handler)
        {
            Edits.Commit();
            return true;
        }
        if (IsCommitting)
        {
            return false;
        }
        var commit = new GridRowCommit(Edits.PendingRow, Edits.PendingIsInsert, new Dictionary<int, CellValue>(Edits.PendingValues));
        IsCommitting = true;
        GridCommitResult result;
        try
        {
            result = await handler(commit).ConfigureAwait(true);
        }
        finally
        {
            IsCommitting = false;
        }
        var ok = result.Ok;
        if (ok && Edits.PendingRow == commit.Row)
        {
            Edits.Commit();
            foreach (var (column, value) in result.Row ?? new Dictionary<int, CellValue>())
            {
                Edits.SetCommitted(commit.Row, column, value);
            }
        }
        else if (!ok && Edits.PendingRow == commit.Row && Selection.FocusedRow != commit.Row)
        {
            // The row stays changed and the focus goes back to it (like HeidiSQL).
            FocusCell(commit.Row, Math.Max(0, Selection.FocusedColumn));
        }
        InvalidateVisual();
        return ok;
    }

    /// <summary>Post (Ctrl+Enter): closes the editor keeping the value and saves the row.</summary>
    public Task<bool> PostAsync()
    {
        EndEdit(commit: true);
        return CommitPendingAsync();
    }

    /// <summary>Cancel editing (Esc): closes the editor without keeping the value; with no editor open, discards the row's changes.</summary>
    public void CancelEditing()
    {
        if (IsEditing)
        {
            EndEdit(commit: false);
        }
        else
        {
            Execute(GridCommand.DiscardRow);
        }
    }

    /// <summary>
    /// New row at the end with <paramref name="values"/> (Insert row, Duplicate row), after saving the pending one; returns
    /// the index or -1.
    /// </summary>
    public async Task<long> InsertRowAsync(IReadOnlyDictionary<int, CellValue>? values = null)
    {
        if (_source is null)
        {
            return -1;
        }
        if (IsReadOnly)
        {
            EditRefused?.Invoke(this, EventArgs.Empty);
            return -1;
        }
        EndEdit(commit: true);
        if (!await CommitPendingAsync().ConfigureAwait(true))
        {
            return -1;
        }
        var row = Edits.InsertRow(SourceRows);
        foreach (var (column, value) in values ?? new Dictionary<int, CellValue>())
        {
            Edits.SetValue(row, column, value);
        }
        Selection.Focus(row, Math.Max(0, Selection.FocusedColumn));
        EnsureVisible(row, Selection.FocusedColumn);
        InvalidateVisual();
        ScrollChanged?.Invoke(this, EventArgs.Empty);
        return row;
    }

    /// <summary>Puts a value in a cell (Insert value ▸, Ctrl+V, Ctrl+X), saving the pending row of another row first.</summary>
    public async Task<bool> SetCellValueAsync(long row, int column, CellValue value)
    {
        if (_source is null || row < 0 || row >= RowCount || column < 0 || column >= Columns.Count)
        {
            return false;
        }
        if (IsReadOnly)
        {
            EditRefused?.Invoke(this, EventArgs.Empty);
            return false;
        }
        EndEdit(commit: true);
        if (Edits.HasPending && Edits.PendingRow != row && !await CommitPendingAsync().ConfigureAwait(true))
        {
            return false;
        }
        Edits.SetValue(row, column, value);
        InvalidateVisual();
        return true;
    }

    /// <summary>Left click on a header (outside the resize margin), with the modifiers.</summary>
    public event EventHandler<GridHeaderEventArgs>? HeaderClicked;

    /// <summary>Right click on a header (columns menu in the Data tab).</summary>
    public event EventHandler<GridHeaderEventArgs>? HeaderContextRequested;

    private IReadOnlyDictionary<int, GridSortIndicator> _sortIndicators = new Dictionary<int, GridSortIndicator>();

    /// <summary>
    /// Sort indicators per column: ▲ or ▼ and the position in the ORDER BY as a superscript (¹²³…⁹, ⁺ from the 10th on),
    /// as in HeidiSQL (appendix D B7).
    /// </summary>
    public IReadOnlyDictionary<int, GridSortIndicator> SortIndicators
    {
        get => _sortIndicators;
        set
        {
            _sortIndicators = value ?? new Dictionary<int, GridSortIndicator>();
            InvalidateVisual();
        }
    }

    /// <summary>Binds the grid to a source. Display formatting uses <see cref="Culture"/>.</summary>
    public void SetSource(IRowSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_source is not null)
        {
            _source.BlocksAvailable -= OnDataArrived;
        }
        if (_cells is not null)
        {
            _cells.Formatted -= OnDataArrived;
        }
        _source = source;
        _cells = new FormattedCellCache(source, new DisplayFormatter(Culture));
        _source.BlocksAvailable += OnDataArrived;
        _cells.Formatted += OnDataArrived;
        _textCache.Clear();
        ClearHeaderCache();
        _topRow = 0;
        _scrollX = 0;
        _prefetchFrom = _prefetchTo = -1;
        _lastRowCount = source.RowCount.Count;
        Edits.Reset();
        Selection.Clear();
        UpdateRowNumberWidth();
        Columns.SetWidths(DefaultWidths(source.Schema));
        if (source.RowCount.Count > 0)
        {
            Selection.Focus(0, 0);
        }
        PendingAutoSize = AutoSizeColumnsAsync(0);
        InvalidateVisual();
        ScrollChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Automatic width (section 4.4): the maximum of the header and up to 100 rows around <paramref name="aroundRow"/>,
    /// capped at 300 px. Measuring runs in the background.
    /// </summary>
    public async Task AutoSizeColumnsAsync(long aroundRow)
    {
        if (_source is null || _cells is null)
        {
            return;
        }
        var source = _source;
        var estimator = new TextWidthEstimator(_typeface, _fontSize);
        var bold = new TextWidthEstimator(_boldTypeface, _fontSize);
        var first = Math.Max(0, Math.Min(aroundRow - 50, source.RowCount.Count - 100));
        var count = (int)Math.Min(100, source.RowCount.Count - first);
        if (count > 0)
        {
            await source.RequestRangeAsync(first, count).ConfigureAwait(true);
        }
        if (!ReferenceEquals(source, _source))
        {
            return;
        }
        var cells = _cells;
        var schema = source.Schema;
        var headers = Enumerable.Range(0, schema.Count).Select(c => HeaderText(c)).ToArray();
        var sorted = _sortIndicators.Keys.ToHashSet();
        var widths = await Task.Run(() =>
        {
            var result = new double[schema.Count];
            for (var c = 0; c < schema.Count; c++)
            {
                var measure = schema[c].IsPrimaryKey ? bold : estimator;
                var w = measure.Measure(headers[c]) + (schema[c].IsPrimaryKey ? 14 : 0) + (sorted.Contains(c) ? 18 : 0);
                for (var r = first; r < first + count; r++)
                {
                    var block = (int)(r / source.BlockSize);
                    if (cells.TryGet(block, c, formatIfResident: true, out var texts))
                    {
                        w = Math.Max(w, measure.Measure(texts[(int)(r - ((long)block * source.BlockSize))]));
                    }
                }
                result[c] = Math.Min(MaxAutoColumnWidth, Math.Ceiling(w + (2 * CellPadding) + 2));
            }
            return result;
        }).ConfigureAwait(true);
        if (!ReferenceEquals(source, _source))
        {
            return;
        }
        Columns.SetWidths(widths);
        _textCache.Clear();
        ClearHeaderCache();
        InvalidateVisual();
        ScrollChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Cell rectangle in control coordinates (may be outside the viewport).</summary>
    public Rect CellBounds(long row, int column)
    {
        var x = RowNumberWidth + Columns.OffsetOf(column) - _scrollX;
        var y = HeaderHeight + ((row - _topRow) * RowHeight);
        return new Rect(x, y, Columns.WidthOf(column), RowHeight);
    }

    /// <summary>
    /// Value of a source cell for editing and copying, lossless (<see cref="DisplayFormatter.FormatRaw"/>): never the
    /// display text (culture separators, cut binary), which saved back would change the value. With an
    /// <see cref="IFullValueSource"/>, cells cut in the SELECT come complete from the server.
    /// </summary>
    public async ValueTask<string?> GetRawTextAsync(long row, int column)
    {
        if (_source is null || row >= SourceRows)
        {
            return null;
        }
        if (_source is IFullValueSource full)
        {
            return await full.GetFullTextAsync(row, column).ConfigureAwait(true);
        }
        var block = await _source.GetBlockAsync((int)(row / _source.BlockSize)).ConfigureAwait(true);
        return DisplayFormatter.FormatRaw(block, (int)(row - block.FirstRow), column);
    }

    /// <summary>Display text of a cell, if it is available without I/O (for tests and accessibility).</summary>
    public string? GetDisplayText(long row, int column)
    {
        if (Edits.TryGetValue(row, column, out var edited))
        {
            return edited.Text ?? DisplayFormatter.NullText;
        }
        if (row >= SourceRows)
        {
            return _source!.Schema[column].IsNullable ? DisplayFormatter.NullText : string.Empty;
        }
        if (_cells is null)
        {
            return null;
        }
        var block = (int)(row / _source!.BlockSize);
        return _cells.TryGet(block, column, formatIfResident: true, out var texts)
            ? texts[(int)(row - ((long)block * _source.BlockSize))]
            : null;
    }

    /// <summary>Runs a keymap command (also used by tests and benchmarks).</summary>
    public bool Execute(GridCommand command)
    {
        if (_source is null)
        {
            return false;
        }
        var row = Math.Max(0, Selection.FocusedRow);
        var column = Selection.FocusedColumn;
        var last = RowCount - 1;
        switch (command)
        {
            case GridCommand.MoveUp:
                EndEdit(commit: true);
                MoveTo(row - 1, column, SelectMode.Replace);
                return true;
            case GridCommand.MoveDown:
                EndEdit(commit: true);
                if (row >= last)
                {
                    if (!IsReadOnly)
                    {
                        _ = InsertRowAsync();
                    }
                }
                else
                {
                    MoveTo(row + 1, column, SelectMode.Replace);
                }
                return true;
            case GridCommand.MoveLeft:
                Selection.FocusColumn(Math.Max(0, column - 1));
                EnsureVisible(row, Selection.FocusedColumn);
                return true;
            case GridCommand.MoveRight:
                Selection.FocusColumn(Math.Min(Columns.Count - 1, column + 1));
                EnsureVisible(row, Selection.FocusedColumn);
                return true;
            case GridCommand.PageDown:
                if (row >= last)
                {
                    NextPageRequested?.Invoke(this, EventArgs.Empty);
                    return true;
                }
                MoveTo(row + ViewportRows - 1, column, SelectMode.Replace);
                return true;
            case GridCommand.PageUp:
                MoveTo(row - ViewportRows + 1, column, SelectMode.Replace);
                return true;
            case GridCommand.FirstColumn:
                Selection.FocusColumn(0);
                EnsureVisible(row, 0);
                return true;
            case GridCommand.LastColumn:
                Selection.FocusColumn(Columns.Count - 1);
                EnsureVisible(row, Columns.Count - 1);
                return true;
            case GridCommand.FirstRow:
                MoveTo(0, column, SelectMode.Replace);
                return true;
            case GridCommand.LastRow:
                // In the Data tab, Ctrl+End is also "Show all" (appendix D B3): the grid's user loads the rest.
                LastRowRequested?.Invoke(this, EventArgs.Empty);
                MoveTo(last, column, SelectMode.Replace);
                return true;
            case GridCommand.ExtendUp:
                MoveTo(row - 1, column, SelectMode.Extend);
                return true;
            case GridCommand.ExtendDown:
                MoveTo(row + 1, column, SelectMode.Extend);
                return true;
            case GridCommand.ExtendPageUp:
                MoveTo(row - ViewportRows + 1, column, SelectMode.Extend);
                return true;
            case GridCommand.ExtendPageDown:
                MoveTo(row + ViewportRows - 1, column, SelectMode.Extend);
                return true;
            case GridCommand.ExtendFirstRow:
                MoveTo(0, column, SelectMode.Extend);
                return true;
            case GridCommand.ExtendLastRow:
                MoveTo(last, column, SelectMode.Extend);
                return true;
            case GridCommand.FocusUp:
                MoveTo(row - 1, column, SelectMode.FocusOnly);
                return true;
            case GridCommand.FocusDown:
                MoveTo(row + 1, column, SelectMode.FocusOnly);
                return true;
            case GridCommand.BeginEdit:
                _ = BeginEditAsync();
                return true;
            case GridCommand.EndEdit:
                EndEdit(commit: true);
                return true;
            case GridCommand.CancelEdit:
                EndEdit(commit: false);
                return true;
            case GridCommand.NextCell:
            case GridCommand.PreviousCell:
                EndEdit(commit: true);
                var next = column + (command == GridCommand.NextCell ? 1 : -1);
                if (next >= 0 && next < Columns.Count)
                {
                    Selection.FocusColumn(next);
                    EnsureVisible(row, next);
                    _ = BeginEditAsync();
                }
                return true;
            case GridCommand.CommitRow:
                EndEdit(commit: true);
                _ = CommitPendingAsync();
                return true;
            case GridCommand.DiscardRow:
                if (Edits.HasPending)
                {
                    var wasInsert = Edits.PendingIsInsert;
                    Edits.Discard();
                    if (wasInsert)
                    {
                        MoveTo(Math.Min(row, RowCount - 1), column, SelectMode.Replace);
                    }
                    InvalidateVisual();
                }
                return true;
            case GridCommand.SelectAll:
                Selection.SelectAll(RowCount);
                return true;
            case GridCommand.InvertSelection:
                Selection.Invert(RowCount);
                return true;
            case GridCommand.Copy:
                _ = CopyFocusedCellAsync();
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Text Ctrl+C puts on the clipboard (section 4.6): the full value of the focused cell, integers without a
    /// separator, NULL as empty text, without NUL characters. Also returns whether the value was NULL.
    /// </summary>
    public async Task<(string Text, bool IsNull)> GetCopyTextAsync()
    {
        var row = Selection.FocusedRow;
        var column = Selection.FocusedColumn;
        if (_source is null || row < 0)
        {
            return (string.Empty, false);
        }
        string? value;
        if (Edits.TryGetValue(row, column, out var edited))
        {
            value = edited.Text;
        }
        else if (row >= SourceRows)
        {
            value = null;
        }
        else
        {
            value = await GetRawTextAsync(row, column).ConfigureAwait(true);
        }
        return value is null ? (string.Empty, true) : (value.Replace("\0", string.Empty, StringComparison.Ordinal), false);
    }

    /// <summary>The last copy was a NULL (HeidiSQL uses this to paste NULL).</summary>
    public static bool ClipboardIsNull => _clipboardIsNull;

    public async Task CopyFocusedCellAsync()
    {
        var (text, isNull) = await GetCopyTextAsync().ConfigureAwait(true);
        _clipboardIsNull = isNull;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
        }
    }

    public Task BeginEditAsync() => BeginEditAsync(null);

    /// <summary>Opens the editor of the focused cell; <paramref name="editor"/> forces one (SQL function, no FK lookup).</summary>
    public async Task BeginEditAsync(GridCellEditor? editor)
    {
        if (_source is null || IsEditing || IsCommitting || Selection.FocusedRow < 0)
        {
            return;
        }
        if (IsReadOnly)
        {
            EditRefused?.Invoke(this, EventArgs.Empty);
            return;
        }
        var row = Selection.FocusedRow;
        var column = Selection.FocusedColumn;
        string? value;
        if (Edits.TryGetValue(row, column, out var edited))
        {
            value = edited.Text;
        }
        else if (row >= SourceRows)
        {
            value = null;
        }
        else
        {
            value = await GetRawTextAsync(row, column).ConfigureAwait(true);
        }
        if (row != Selection.FocusedRow || column != Selection.FocusedColumn || IsEditing)
        {
            return;
        }
        var spec = editor ?? (EditorProvider is { } provider ? await provider(row, column, value).ConfigureAwait(true) : null) ?? new TextCellEditor();
        if (row != Selection.FocusedRow || column != Selection.FocusedColumn || IsEditing)
        {
            return;
        }
        if (spec is PopupCellEditor || (spec is TextCellEditor && value is { } long1 && (long1.Length >= 256 || long1.AsSpan().IndexOfAny('\r', '\n') >= 0)))
        {
            PopupEditRequested?.Invoke(this, new GridPopupEditEventArgs(row, column, value, spec is PopupCellEditor { IsBinary: true }));
            return;
        }
        if (spec is TextCellEditor text)
        {
            text.OnPopup = current =>
            {
                EndEdit(commit: false);
                PopupEditRequested?.Invoke(this, new GridPopupEditEventArgs(row, column, current, IsBinary: false));
            };
        }
        EnsureVisible(row, column);
        _editRow = row;
        _editColumn = column;
        _editOriginalNull = value is null;
        _editOriginalText = value ?? string.Empty;
        _editorSpec = spec;
        _editor = spec.Create(value, _fontSize, new FontFamily(FontFamilyName));
        _editor.LostFocus += (_, _) =>
        {
            // An open list has the focus in its popup; the editor only closes when the focus really leaves.
            if (_editor is not null && !_editor.IsKeyboardFocusWithin && _editor is not ComboBox { IsDropDownOpen: true })
            {
                EndEdit(commit: true);
            }
        };
        VisualChildren.Add(_editor);
        LogicalChildren.Add(_editor);
        InvalidateArrange();
        _editor.Focus();
    }

    /// <summary>Closes the in-place editor; with <paramref name="commit"/>, keeps the value in the edit buffer.</summary>
    public void EndEdit(bool commit)
    {
        if (_editor is null)
        {
            return;
        }
        var editor = _editor;
        var spec = _editorSpec;
        _editor = null;
        _editorSpec = null;
        if (commit && spec?.Read(editor, _editOriginalNull ? null : _editOriginalText) is { } value
            && (!Edits.HasPending || Edits.PendingRow == _editRow))
        {
            Edits.SetValue(_editRow, _editColumn, value);
        }
        VisualChildren.Remove(editor);
        LogicalChildren.Remove(editor);
        _editRow = -1;
        _editColumn = -1;
        Focus();
        InvalidateVisual();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_editor is not null)
        {
            var cell = CellBounds(_editRow, _editColumn);
            var rows = _editorSpec?.Rows ?? 1;
            _editor.Arrange(new Rect(cell.X, cell.Y - 1, Math.Max(cell.Width + 1, 40), (cell.Height * rows) + 2));
        }
        return finalSize;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _editor?.Measure(availableSize);
        return base.MeasureOverride(availableSize);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _topRow = Math.Clamp(_topRow, 0, MaxTopRow);
        _scrollX = Math.Clamp(_scrollX, 0, MaxScrollX);
        ScrollChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyTheme();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new DataGridAutomationPeer(this);

    public override void Render(DrawingContext context)
    {
        var started = Stopwatch.GetTimestamp();
        var bounds = new Rect(Bounds.Size);
        var p = _palette;
        context.FillRectangle(p.Background, bounds);
        if (_source is null || _cells is null || Columns.Count == 0)
        {
            return;
        }

        _syncFormatBudget = SyncFormatBudget;
        _glyphs ??= new GlyphRunFactory(_typeface, _fontSize);
        _boldGlyphs ??= new GlyphRunFactory(_boldTypeface, _fontSize);
        var rowHeight = RowHeight;
        var dataLeft = RowNumberWidth;
        var dataTop = HeaderHeight;
        var visibleRows = (int)Math.Ceiling((bounds.Height - dataTop) / rowHeight);
        var lastRow = Math.Min(RowCount - 1, _topRow + visibleRows - 1);
        var (firstColumn, lastColumn) = Columns.VisibleRange(_scrollX, bounds.Width - dataLeft);
        Prefetch(_topRow, lastRow, firstColumn, lastColumn);

        var schema = _source.Schema;
        var blockSize = _source.BlockSize;
        var sourceRows = SourceRows;
        var focusedRow = Selection.FocusedRow;
        var focusedColumn = Selection.FocusedColumn;
        var hasFocus = IsKeyboardFocusWithin;

        using (context.PushClip(new Rect(dataLeft, dataTop, Math.Max(0, bounds.Width - dataLeft), Math.Max(0, bounds.Height - dataTop))))
        {
            for (var row = _topRow; row <= lastRow; row++)
            {
                var y = dataTop + ((row - _topRow) * rowHeight);
                if (Selection.IsSelected(row))
                {
                    context.FillRectangle(p.SelectedRow, new Rect(dataLeft, y, bounds.Width - dataLeft, rowHeight));
                }
                var block = (int)(row / blockSize);
                var local = (int)(row - ((long)block * blockSize));
                for (var c = firstColumn; c <= lastColumn; c++)
                {
                    var x = dataLeft + Columns.OffsetOf(c) - _scrollX;
                    var width = Columns.WidthOf(c);
                    var info = schema[c];
                    var focused = row == focusedRow && c == focusedColumn;
                    if (focused)
                    {
                        context.FillRectangle(hasFocus ? p.FocusedCell : p.InactiveCell, new Rect(x, y, width, rowHeight));
                    }
                    if (focused && IsEditing)
                    {
                        continue;
                    }

                    string? text;
                    bool isNull;
                    if (Edits.TryGetValue(row, c, out var edited))
                    {
                        isNull = edited.IsNull;
                        text = edited.Text ?? DisplayFormatter.NullText;
                    }
                    else if (row >= sourceRows)
                    {
                        isNull = info.IsNullable;
                        text = isNull ? DisplayFormatter.NullText : string.Empty;
                    }
                    else if (TryGetFormatted(block, c, out var texts))
                    {
                        text = texts[local];
                        // The formatter returns its own constant for NULL; a "(NULL)" text value is another instance.
                        isNull = ReferenceEquals(text, DisplayFormatter.NullText);
                    }
                    else
                    {
                        continue; // Placeholder: no data yet, the cell stays empty until the block arrives.
                    }

                    if (text.Length > 0)
                    {
                        IBrush brush = focused && hasFocus ? p.FocusedText : isNull ? p.NullFor(info.Category) : p.TextFor(info.Category);
                        var typeface = info.IsPrimaryKey ? _boldTypeface : _typeface;
                        var glyphs = info.IsPrimaryKey ? _boldGlyphs! : _glyphs!;
                        _textCache.Draw(context, row, c, text, glyphs, typeface, _fontSize, brush, info.IsNumeric && !isNull, width - (2 * CellPadding), new Point(x + CellPadding, y), rowHeight);
                    }
                    if (Edits.IsModified(row, c))
                    {
                        DrawModifiedMarker(context, x, y);
                    }
                }
            }

            // Grid lines: one per row and one per column.
            var right = Math.Min(bounds.Width, dataLeft + Columns.TotalWidth - _scrollX);
            for (var row = _topRow; row <= lastRow; row++)
            {
                var y = Math.Floor(dataTop + ((row - _topRow + 1) * rowHeight)) - 0.5;
                context.DrawLine(p.GridLine, new Point(dataLeft, y), new Point(right, y));
            }
            var bottom = Math.Min(bounds.Height, dataTop + ((lastRow - _topRow + 1) * rowHeight));
            for (var c = firstColumn; c <= lastColumn; c++)
            {
                var x = Math.Floor(dataLeft + Columns.OffsetOf(c) + Columns.WidthOf(c) - _scrollX) - 0.5;
                context.DrawLine(p.GridLine, new Point(x, dataTop), new Point(x, bottom));
            }
        }

        DrawHeader(context, bounds, firstColumn, lastColumn);
        DrawRowNumbers(context, bounds, lastRow);
        if (_autoScroll.IsActive)
        {
            DrawAutoScrollMarker(context);
        }
        _textCache.EndFrame();
        LastRenderEndTimestamp = Stopwatch.GetTimestamp();
        LastRenderTime = Stopwatch.GetElapsedTime(started, LastRenderEndTimestamp);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_autoScroll.IsActive)
        {
            // Any key ends the free scroll (Esc included), as on Windows.
            StopAutoScroll(null);
            e.Handled = true;
            return;
        }
        if (e.Handled || IsEditing)
        {
            return;
        }
        var command = Keymap.Resolve(e.Key, e.KeyModifiers, GridKeyContext.Navigation);
        if (command != GridCommand.None && Execute(command))
        {
            e.Handled = true;
        }
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Handled || IsEditing || string.IsNullOrEmpty(e.Text) || _source is null)
        {
            return;
        }
        // Incremental search on the focused column, in the blocks already in memory.
        var now = DateTime.UtcNow;
        _search = now - _lastSearch > SearchReset ? e.Text : _search + e.Text;
        _lastSearch = now;
        var column = Selection.FocusedColumn;
        var start = Math.Max(0, Selection.FocusedRow);
        var limit = Math.Min(SourceRows, start + (8L * _source.BlockSize));
        for (var row = start; row < limit; row++)
        {
            var text = GetDisplayText(row, column);
            if (text is not null && text.StartsWith(_search, StringComparison.CurrentCultureIgnoreCase))
            {
                MoveTo(row, column, SelectMode.Replace);
                break;
            }
        }
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (_autoScroll.IsActive)
        {
            // With free scroll active, any click just ends it.
            StopAutoScroll(e.Pointer);
            e.Handled = true;
            return;
        }
        if (_source is not null && point.Properties.IsMiddleButtonPressed)
        {
            StartAutoScroll(point.Position, e.Pointer);
            e.Handled = true;
            return;
        }
        if (_source is null)
        {
            return;
        }
        var pos = point.Position;
        if (point.Properties.IsRightButtonPressed)
        {
            if (pos.Y < HeaderHeight)
            {
                // Right button on the header: columns menu (appendix D B7).
                var headerColumn = Columns.ColumnAt(pos.X - RowNumberWidth + _scrollX);
                HeaderContextRequested?.Invoke(this, new GridHeaderEventArgs(headerColumn, e.KeyModifiers));
                e.Handled = true;
                return;
            }
            // Right button on a cell: focuses it (keeps the selection if the row was already selected) and lets the menu open.
            var rightColumn = Columns.ColumnAt(pos.X - RowNumberWidth + _scrollX);
            var rightRow = _topRow + (long)((pos.Y - HeaderHeight) / RowHeight);
            if (rightRow < RowCount && rightColumn >= 0 && pos.X >= RowNumberWidth)
            {
                Focus();
                MoveTo(rightRow, rightColumn, Selection.IsSelected(rightRow) ? SelectMode.FocusOnly : SelectMode.Replace);
            }
            return;
        }
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }
        if (pos.Y < HeaderHeight)
        {
            var border = ColumnBorderAt(pos.X);
            if (border >= 0)
            {
                if (e.ClickCount == 2)
                {
                    _ = AutoSizeColumnAsync(border);
                }
                else
                {
                    _resizeColumn = border;
                    _resizeStartX = pos.X;
                    _resizeStartWidth = Columns.WidthOf(border);
                    e.Pointer.Capture(this);
                }
            }
            else if (pos.X >= RowNumberWidth && Columns.ColumnAt(pos.X - RowNumberWidth + _scrollX) is var headerColumn and >= 0)
            {
                // Click on the header: the grid's user decides (server-side sorting in the Data tab, appendix D B7).
                HeaderClicked?.Invoke(this, new GridHeaderEventArgs(headerColumn, e.KeyModifiers));
            }
            e.Handled = true;
            return;
        }

        if (IsEditing)
        {
            EndEdit(commit: true);
        }
        Focus();
        var column = Columns.ColumnAt(pos.X - RowNumberWidth + _scrollX);
        var row = _topRow + (long)((pos.Y - HeaderHeight) / RowHeight);
        if (row >= RowCount || column < 0 || pos.X < RowNumberWidth)
        {
            // Click on empty space saves the pending row (section 4.4).
            _ = CommitPendingAsync();
            e.Handled = true;
            return;
        }
        var mode = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? SelectMode.Extend
            : PlatformKeys.IsCommand(e.KeyModifiers) ? SelectMode.Toggle
            : SelectMode.Replace;
        MoveTo(row, column, mode);
        if (e.ClickCount == 2)
        {
            _ = BeginEditAsync();
        }
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_autoScroll.IsActive)
        {
            var p = e.GetPosition(this);
            _autoScroll.Move(p.X, p.Y);
            return;
        }
        if (_resizeColumn >= 0)
        {
            Columns.SetWidth(_resizeColumn, _resizeStartWidth + e.GetPosition(this).X - _resizeStartX);
            InvalidateVisual();
            ScrollChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        var pos = e.GetPosition(this);
        Cursor = pos.Y < HeaderHeight && ColumnBorderAt(pos.X) >= 0 ? ResizeCursor : Cursor.Default;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_autoScroll.IsActive && e.InitialPressMouseButton == MouseButton.Middle)
        {
            // Press and drag: stops on release. Press and release without moving: stays active until the next click.
            if (_autoScroll.Release())
            {
                // Stays active. Avalonia releases the capture after this event; it is captured again right after,
                // to receive the moves even outside the grid.
                var pointer = e.Pointer;
                Dispatcher.UIThread.Post(() =>
                {
                    if (_autoScroll.IsActive)
                    {
                        pointer.Capture(this);
                    }
                }, DispatcherPriority.Input);
            }
            else
            {
                StopAutoScroll(e.Pointer);
            }
            e.Handled = true;
            return;
        }
        if (_resizeColumn >= 0)
        {
            _resizeColumn = -1;
            e.Pointer.Capture(null);
        }
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        // Switching window or control ends the free scroll (pointer capture is no use as a signal:
        // Avalonia releases it whenever the button is released).
        if (_autoScroll.IsActive && !IsKeyboardFocusWithin)
        {
            StopAutoScroll(null);
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var notches = e.Delta.Y;
        if (PlatformKeys.IsCommand(e.KeyModifiers))
        {
            FontSize += Math.Sign(notches);
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            // Alt+wheel changes row.
            MoveTo(Selection.FocusedRow - (long)Math.Round(notches), Selection.FocusedColumn, SelectMode.Replace);
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.Delta.X != 0)
        {
            ScrollX -= (e.Delta.X != 0 ? e.Delta.X : notches) * 60;
        }
        else
        {
            TopRow -= (long)Math.Round(notches * 3);
        }
        e.Handled = true;
    }

    private void StartAutoScroll(Point origin, IPointer pointer)
    {
        if (IsEditing)
        {
            EndEdit(commit: true);
        }
        Focus();
        _autoScroll.Start(origin.X, origin.Y);
        pointer.Capture(this);
        Cursor = AutoScrollCursor;
        _lastAutoScrollFrame = TimeSpan.Zero;
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnAutoScrollFrame);
        InvalidateVisual();
    }

    private void StopAutoScroll(IPointer? pointer)
    {
        _autoScroll.Stop();
        if (pointer?.Captured == this)
        {
            pointer.Capture(null);
        }
        Cursor = Cursor.Default;
        InvalidateVisual();
    }

    /// <summary>One free-scroll step per frame, with the real time between frames.</summary>
    private void OnAutoScrollFrame(TimeSpan now)
    {
        if (!_autoScroll.IsActive)
        {
            return;
        }
        var seconds = _lastAutoScrollFrame == TimeSpan.Zero ? 0 : (now - _lastAutoScrollFrame).TotalSeconds;
        _lastAutoScrollFrame = now;
        ApplyAutoScroll(Math.Min(seconds, 0.1));
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnAutoScrollFrame);
    }

    /// <summary>Applies <paramref name="seconds"/> of free scroll (public for tests).</summary>
    public void ApplyAutoScroll(double seconds)
    {
        var (rows, pixels) = _autoScroll.Step(seconds, RowHeight);
        if (rows != 0)
        {
            TopRow += rows;
        }
        if (pixels != 0)
        {
            ScrollX += pixels;
        }
    }

    private void DrawAutoScrollMarker(DrawingContext context)
    {
        // Origin marker: a circle with four arrows (our own drawing).
        var (x, y) = _autoScroll.Origin;
        var p = _palette;
        var pen = new ImmutablePen(p.HeaderText, 1);
        context.DrawEllipse(p.Header, pen, new Point(x, y), 13, 13);
        context.DrawEllipse(p.HeaderText, null, new Point(x, y), 2, 2);
        void Arrow(double dx, double dy)
        {
            var tip = new Point(x + (dx * 10), y + (dy * 10));
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(tip, true);
                g.LineTo(new Point(tip.X - (dx * 4) - (dy * 3.5), tip.Y - (dy * 4) - (dx * 3.5)));
                g.LineTo(new Point(tip.X - (dx * 4) + (dy * 3.5), tip.Y - (dy * 4) + (dx * 3.5)));
                g.EndFigure(true);
            }
            context.DrawGeometry(p.HeaderText, null, geometry);
        }
        Arrow(0, -1);
        Arrow(0, 1);
        Arrow(-1, 0);
        Arrow(1, 0);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsEditing)
        {
            return;
        }
        var command = Keymap.Resolve(e.Key, e.KeyModifiers, GridKeyContext.Editing);
        // An open list handles its own keys; editors with arrows use ↑/↓.
        if (_editor is ComboBox { IsDropDownOpen: true }
            || (_editorSpec?.UsesArrows == true && command is GridCommand.MoveUp or GridCommand.MoveDown))
        {
            return;
        }
        if (command != GridCommand.None && Execute(command))
        {
            e.Handled = true;
        }
    }

    private void MoveTo(long row, int column, SelectMode mode)
    {
        if (RowCount == 0)
        {
            return;
        }
        row = Math.Clamp(row, 0, RowCount - 1);
        CommitIfOtherRow(row);
        Selection.Focus(row, Math.Clamp(column, 0, Columns.Count - 1), mode);
        EnsureVisible(row, Selection.FocusedColumn);
    }

    /// <summary>Save when moving to another row (FAM-G08); if it fails, the focus goes back to the changed row.</summary>
    private void CommitIfOtherRow(long row)
    {
        if (Edits.HasPending && Edits.PendingRow != row)
        {
            _ = CommitPendingAsync();
        }
    }

    /// <summary>Focuses a cell and shows it (Find in the grid).</summary>
    public void FocusCell(long row, int column)
    {
        Selection.Focus(row, column);
        EnsureVisible(row, column);
        InvalidateVisual();
    }

    private void EnsureVisible(long row, int column)
    {
        if (row < _topRow)
        {
            TopRow = row;
        }
        else if (row >= _topRow + ViewportRows)
        {
            TopRow = row - ViewportRows + 1;
        }
        if (column >= 0 && column < Columns.Count)
        {
            var left = Columns.OffsetOf(column);
            var right = left + Columns.WidthOf(column);
            if (left < _scrollX)
            {
                ScrollX = left;
            }
            else if (right > _scrollX + DataViewportWidth)
            {
                ScrollX = right - DataViewportWidth;
            }
        }
        InvalidateArrange();
    }

    private bool TryGetFormatted(int block, int column, out string[] texts)
    {
        if (_cells!.TryGet(block, column, formatIfResident: false, out var cached))
        {
            texts = cached;
            return true;
        }
        if (_syncFormatBudget > 0 && _cells.TryGet(block, column, formatIfResident: true, out cached))
        {
            _syncFormatBudget--;
            texts = cached;
            return true;
        }
        texts = [];
        return false;
    }

    /// <summary>Asks the source for the visible area + 2 screens above and below (section 11.3) and for formatting the visible columns.</summary>
    private void Prefetch(long firstRow, long lastRow, int firstColumn, int lastColumn)
    {
        if (_source is null || _cells is null || lastRow < firstRow)
        {
            return;
        }
        var screen = Math.Max(1, lastRow - firstRow + 1);
        var inside = firstRow >= _prefetchFrom && lastRow <= _prefetchTo
            && firstColumn >= _prefetchFirstColumn && lastColumn <= _prefetchLastColumn;
        // Requests again when the visible area gets within one screen of the edge of the requested window.
        var nearEdge = (firstRow - _prefetchFrom < screen && _prefetchFrom > 0)
            || (_prefetchTo - lastRow < screen && _prefetchTo < SourceRows - 1);
        if (inside && !nearEdge)
        {
            return;
        }
        var from = Math.Max(0, firstRow - (2 * screen));
        var to = Math.Min(SourceRows - 1, lastRow + (2 * screen));
        if (to < from)
        {
            return;
        }
        _prefetchFrom = from;
        _prefetchTo = to;
        _prefetchFirstColumn = firstColumn;
        _prefetchLastColumn = lastColumn;
        _ = _source.RequestRangeAsync(from, (int)(to - from + 1));
        var columns = new int[lastColumn - firstColumn + 1];
        for (var i = 0; i < columns.Length; i++)
        {
            columns[i] = firstColumn + i;
        }
        _cells.Prefetch((int)(from / _source.BlockSize), (int)(to / _source.BlockSize), columns);
    }

    private void OnDataArrived(object? sender, EventArgs e)
    {
        if (_invalidatePosted)
        {
            return;
        }
        _invalidatePosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _invalidatePosted = false;
            if (_source is { } source && source.RowCount.Count != _lastRowCount)
            {
                // New rows streaming in (Next, Show all, query): the scrollbar, the # column and, on the first arrival, the
                // automatic widths and the focus follow.
                var first = _lastRowCount == 0;
                _lastRowCount = source.RowCount.Count;
                UpdateRowNumberWidth();
                if (first)
                {
                    if (Selection.FocusedRow < 0)
                    {
                        Selection.Focus(0, 0);
                    }
                    PendingAutoSize = AutoSizeColumnsAsync(0);
                }
                ScrollChanged?.Invoke(this, EventArgs.Empty);
            }
            InvalidateVisual();
        }, DispatcherPriority.Render);
    }

    private void DrawHeader(DrawingContext context, Rect bounds, int firstColumn, int lastColumn)
    {
        var p = _palette;
        var height = HeaderHeight;
        context.FillRectangle(p.Header, new Rect(0, 0, bounds.Width, height));
        using (context.PushClip(new Rect(RowNumberWidth, 0, Math.Max(0, bounds.Width - RowNumberWidth), height)))
        {
            for (var c = firstColumn; c <= lastColumn; c++)
            {
                var x = RowNumberWidth + Columns.OffsetOf(c) - _scrollX;
                var width = Columns.WidthOf(c);
                var info = _source!.Schema[c];
                var textLeft = x + CellPadding;
                if (info.IsPrimaryKey)
                {
                    DrawKeyIcon(context, new Point(textLeft, (height / 2) - 4));
                    textLeft += 12;
                }
                var sortWidth = 0.0;
                if (_sortIndicators.TryGetValue(c, out var indicator))
                {
                    var glyph = SortGlyph(indicator);
                    sortWidth = glyph.WidthIncludingTrailingWhitespace + 2;
                    glyph.Draw(context, new Point(x + width - CellPadding - glyph.WidthIncludingTrailingWhitespace, (height - glyph.Height) / 2));
                }
                var header = HeaderLayout(c, width - (textLeft - x) - CellPadding - sortWidth);
                header.Number.Draw(context, new Point(textLeft, (height - header.Number.Height) / 2));
                header.Name.Draw(context, new Point(textLeft + header.Number.WidthIncludingTrailingWhitespace, (height - header.Name.Height) / 2));
                var line = Math.Floor(x + width) - 0.5;
                context.DrawLine(p.GridLine, new Point(line, 0), new Point(line, height));
            }
        }
        context.DrawLine(p.GridLine, new Point(0, height - 0.5), new Point(bounds.Width, height - 0.5));
    }

    private void DrawRowNumbers(DrawingContext context, Rect bounds, long lastRow)
    {
        var p = _palette;
        var area = new Rect(0, HeaderHeight, RowNumberWidth, Math.Max(0, bounds.Height - HeaderHeight));
        if (_sessionBrush is { } session)
        {
            // Session colour: a soft tint on the # column and a bar with the full colour on the left.
            context.FillRectangle(new ImmutableSolidColorBrush(Common.Theming.ColorMath.SessionTint(_sessionColor, p.RowNumberArgb)), area);
            context.FillRectangle(session, new Rect(0, HeaderHeight, 3, area.Height));
        }
        else
        {
            context.FillRectangle(p.RowNumberBackground, area);
        }
        IBrush textBrush = p.Muted;
        Span<char> text = stackalloc char[32];
        for (var row = _topRow; row <= lastRow; row++)
        {
            var y = HeaderHeight + ((row - _topRow) * RowHeight);
            (row + 1).TryFormat(text, out var length, "N0", Culture);
            _textCache.Draw(context, row, RowNumberColumn, text[..length], _glyphs!, _typeface, _fontSize, textBrush, true, RowNumberWidth - (2 * CellPadding), new Point(CellPadding, y), RowHeight);
        }
        context.DrawLine(p.GridLine, new Point(RowNumberWidth - 0.5, 0), new Point(RowNumberWidth - 0.5, bounds.Height));
    }

    private void DrawModifiedMarker(DrawingContext context, double x, double y)
    {
        // Red triangle in the top-left corner (section 5.6).
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(x + 1, y + 1), true);
            g.LineTo(new Point(x + 7, y + 1));
            g.LineTo(new Point(x + 1, y + 7));
            g.EndFigure(true);
        }
        context.DrawGeometry(_palette.Modified, null, geometry);
    }

    private void DrawKeyIcon(DrawingContext context, Point origin)
    {
        // Simple drawn key (our own icon, not Icons8): ring + shaft.
        var brush = _palette.KeyIcon;
        var pen = new ImmutablePen(brush, 1.5);
        context.DrawEllipse(null, pen, new Rect(origin.X, origin.Y, 6, 6));
        context.DrawLine(pen, new Point(origin.X + 5, origin.Y + 5), new Point(origin.X + 10, origin.Y + 10));
        context.DrawLine(pen, new Point(origin.X + 8, origin.Y + 8), new Point(origin.X + 9.5, origin.Y + 6.5));
    }

    private HeaderLayouts HeaderLayout(int column, double maxWidth)
    {
        if (_headerCache.TryGetValue(column, out var cached))
        {
            if (cached.MaxWidth == maxWidth)
            {
                return cached;
            }
            cached.Number.Dispose();
            cached.Name.Dispose();
        }
        var info = _source!.Schema[column];
        var number = new Avalonia.Media.TextFormatting.TextLayout((column + 1).ToString(CultureInfo.InvariantCulture) + " ", _typeface, _fontSize, _palette.Muted, maxLines: 1);
        var name = new Avalonia.Media.TextFormatting.TextLayout(
            info.Name,
            info.IsPrimaryKey ? _boldTypeface : _typeface,
            _fontSize,
            _palette.HeaderText,
            textTrimming: TextTrimming.CharacterEllipsis,
            maxWidth: Math.Max(1, maxWidth - number.WidthIncludingTrailingWhitespace),
            maxLines: 1);
        var layout = new HeaderLayouts(number, name, maxWidth);
        _headerCache[column] = layout;
        return layout;
    }

    private void ClearHeaderCache()
    {
        foreach (var h in _headerCache.Values)
        {
            h.Number.Dispose();
            h.Name.Dispose();
        }
        _headerCache.Clear();
        foreach (var g in _sortGlyphCache.Values)
        {
            g.Dispose();
        }
        _sortGlyphCache.Clear();
    }

    private static readonly string[] Superscripts = ["¹", "²", "³", "⁴", "⁵", "⁶", "⁷", "⁸", "⁹"];

    /// <summary>▲ or ▼ with the position as a superscript; ⁺ from the 10th column on.</summary>
    private Avalonia.Media.TextFormatting.TextLayout SortGlyph(GridSortIndicator indicator)
    {
        var text = (indicator.Descending ? "▼" : "▲") + (indicator.Position >= 1 && indicator.Position <= 9 ? Superscripts[indicator.Position - 1] : "⁺");
        if (!_sortGlyphCache.TryGetValue(text, out var layout))
        {
            layout = new Avalonia.Media.TextFormatting.TextLayout(text, _typeface, _fontSize, _palette.HeaderText, maxLines: 1);
            _sortGlyphCache[text] = layout;
        }
        return layout;
    }

    private string HeaderText(int column) => $"{column + 1} {_source!.Schema[column].Name}";

    private int ColumnBorderAt(double x)
    {
        var dataX = x - RowNumberWidth + _scrollX;
        var column = Columns.ColumnAt(dataX);
        if (column < 0)
        {
            return dataX >= Columns.TotalWidth && dataX < Columns.TotalWidth + 4 ? Columns.Count - 1 : -1;
        }
        var right = Columns.OffsetOf(column) + Columns.WidthOf(column);
        if (right - dataX <= 4)
        {
            return column;
        }
        var left = Columns.OffsetOf(column);
        return dataX - left <= 3 && column > 0 ? column - 1 : -1;
    }

    private async Task AutoSizeColumnAsync(int column)
    {
        if (_source is null || _cells is null)
        {
            return;
        }
        var estimator = new TextWidthEstimator(_source.Schema[column].IsPrimaryKey ? _boldTypeface : _typeface, _fontSize);
        var first = Math.Max(0, Selection.FocusedRow - 50);
        var w = estimator.Measure(HeaderText(column)) + (_source.Schema[column].IsPrimaryKey ? 14 : 0);
        for (var r = first; r < Math.Min(SourceRows, first + 100); r++)
        {
            if (GetDisplayText(r, column) is { } text)
            {
                w = Math.Max(w, estimator.Measure(text));
            }
        }
        await Task.Yield();
        Columns.SetWidth(column, Math.Min(MaxAutoColumnWidth, Math.Ceiling(w + (2 * CellPadding) + 2)));
        InvalidateVisual();
        ScrollChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateRowNumberWidth()
    {
        var estimator = new TextWidthEstimator(_typeface, _fontSize);
        var widest = Math.Max(1, RowCount + 1000).ToString("N0", Culture);
        RowNumberWidth = Math.Ceiling(estimator.Measure(widest) + (2 * CellPadding) + 4);
    }

    private void ApplyTheme()
    {
        _palette = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? GridPalette.Dark : GridPalette.Light;
        _textCache.Clear();
        ClearHeaderCache();
        InvalidateVisual();
    }

    private static double[] DefaultWidths(ResultSchema schema)
    {
        var widths = new double[schema.Count];
        for (var c = 0; c < schema.Count; c++)
        {
            widths[c] = schema[c].Storage switch
            {
                StorageKind.Int64 => 70,
                StorageKind.Decimal or StorageKind.Double => 90,
                StorageKind.DateTime => 120,
                _ => 160,
            };
        }
        return widths;
    }

    private sealed record HeaderLayouts(
        Avalonia.Media.TextFormatting.TextLayout Number,
        Avalonia.Media.TextFormatting.TextLayout Name,
        double MaxWidth);
}

/// <summary>Click on a grid header: column (-1 outside the columns) and modifiers.</summary>
public sealed class GridHeaderEventArgs(int column, KeyModifiers modifiers) : EventArgs
{
    public int Column { get; } = column;

    public KeyModifiers Modifiers { get; } = modifiers;
}

/// <summary>Sorting of a column in the header: direction and position (1 = first) in the ORDER BY.</summary>
public readonly record struct GridSortIndicator(bool Descending, int Position);
