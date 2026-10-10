using System.Globalization;
using Avalonia.Input;
using Avalonia.Threading;
using Eunomia.App.Data;
using Eunomia.App.Sessions;
using Eunomia.App.Settings;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Results;
using Eunomia.Presentation.Logging;
using Eunomia.UI.Controls.Grid;
using Eunomia.UI.Controls.Tree;
using Eunomia.UI.Shell.Sessions;

namespace Eunomia.UI.Shell;

/// <summary>
/// Data tab on real tables (phase 5; section 4.4; appendix D B2–B12): SELECT with truncation, Next and Show all, a label with
/// the count variants, server-side sorting (header click, Sorting, Alt+S) and hidden columns, with the state
/// saved per table in <c>app.db</c>.
/// </summary>
public sealed partial class MainWindow
{
    private TableBrowser? _browser;

    /// <summary>The browser's rows with the path to the full values (Ctrl+C and editing of cut cells).</summary>
    private FullValueRowSource? _browserRows;
    private long? _restoreTopRow;
    private IReadOnlyList<int> _dataVisibleColumns = [];
    private (Guid Session, DbObjectRef Table)? _dataTarget;

    /// <summary>
    /// Data tab load in progress (first page, Next, Show all and the label's count), or <c>null</c>. Choosing something else
    /// in the tree or running a query on the same session cancels it (the driver sends <c>KILL QUERY</c>), instead of the
    /// connection staying busy until the end of a large table on a slow server.
    /// </summary>
    private CancellationTokenSource? _dataLoad;

    /// <summary>Data tab browser (tests).</summary>
    internal TableBrowser? DataBrowser => _browser;

    private void InitDataTab()
    {
        var c = _context.Commands;
        _dataView.NextButton.Click += (_, _) => _ = LoadMoreAsync(all: false);
        _dataView.ShowAllButton.Click += (_, _) => _ = LoadMoreAsync(all: true);
        _dataView.SortingButton.Click += (_, _) => ShowSortingPopup();
        ApplyGridOptions(DataGrid);
        _dataView.ColumnsButton.Click += (_, _) => ShowColumnsPopup();
        _dataView.ExactCountChanged += (_, _) => _ = RecountAsync();
        DataGrid.HeaderClicked += OnDataHeaderClicked;
        DataGrid.HeaderContextRequested += (_, _) => ShowColumnsPopup();
        DataGrid.NextPageRequested += (_, _) => _ = LoadMoreAsync(all: false);
        DataGrid.LastRowRequested += (_, _) => _ = ShowAllAndGoLastAsync();
        // Toolbar First and Last buttons: Last is Show all and the last row (appendix D B3).
        c.SetHandler("grid.first", () =>
        {
            DataGrid.Selection.Focus(0, Math.Max(0, DataGrid.Selection.FocusedColumn));
            DataGrid.TopRow = 0;
        }, () => _browser is not null && DataGrid.RowCount > 0);
        c.SetHandler("grid.last", _ => new ValueTask(ShowAllAndGoLastAsync()), () => _browser is not null && DataGrid.RowCount > 0);
        c.SetHandler("data.showNext", _ => new ValueTask(LoadMoreAsync(all: false)), () => _browser?.CanLoadMore == true);
        c.SetHandler("data.showAll", _ => new ValueTask(LoadMoreAsync(all: true)), () => _browser?.CanLoadMore == true);
        c.SetHandler("grid.resetSorting", _ => new ValueTask(ChangeSortAsync([])), () => _browser is { Options.Sort.Count: > 0 });

        // WHERE filter (appendix D B9): F9 and Ctrl+W in the panel; F7 focuses the multi-column filter.
        Controls.Common.Input.CommandScope.SetContext(_dataView.FilterPanel, App.Commands.CommandContexts.FilterPanel);
        _dataView.ApplyFilterRequested += (_, _) => ApplyFilter(_dataView.FilterEditor.Text);
        _dataView.FilterEditor.CompletionProvider = (text, caret, ct) => ProposeFilterCompletionAsync(text, caret, ct);
        _dataView.ClearFilterRequested += (_, _) => ClearFilter();
        _dataView.MultiColumnTextChanged += (_, text) =>
        {
            if (_browser is { } browser && Sessions?.Connector.Find(_dataTarget!.Value.Session)?.Host.ServerServices is { } services)
            {
                _dataView.FilterEditor.Text = DataFilters.MultiColumn(services.Dialect, browser.Table.Columns, text);
            }
        };
        c.SetHandler("filter.apply", () => ApplyFilter(_dataView.FilterEditor.Text), () => _browser is not null);
        c.SetHandler("filter.clear", ClearFilter, () => _browser is not null);
        c.SetHandler("filter.remove", ClearFilter, () => _browser is not null && DataTabQuery.NormalizeFilter(_browser.Options.Filter) is not null);
        c.SetHandler("goto.multiColumnFilter", () =>
        {
            _tabs.SelectedItem = _tabs.DataTab;
            _dataView.FilterPanel.IsVisible = true;
            _dataView.MultiColumnBox.Focus();
        }, () => _browser is not null);
    }

    private readonly Dictionary<TableKey, List<string>> _memoryRecentFilters = [];

    private IReadOnlyList<string> RecentFiltersOf(TableKey key) =>
        _context.AppDatabase is { } db ? db.GetRecentFilters(key) : _memoryRecentFilters.GetValueOrDefault(key) ?? [];

    private void AddRecentFilter(TableKey key, string filter)
    {
        if (_context.AppDatabase is { } db)
        {
            db.AddRecentFilter(key, filter);
            return;
        }
        var list = _memoryRecentFilters.TryGetValue(key, out var existing) ? existing : _memoryRecentFilters[key] = [];
        list.Remove(filter);
        list.Insert(0, filter);
        if (list.Count > 20)
        {
            list.RemoveRange(20, list.Count - 20);
        }
    }

    /// <summary>Apply filter: the text goes to the table's recent filters (20, no duplicates) and reloads (appendix D B9).</summary>
    private void ApplyFilter(string text)
    {
        if (_dataTarget is not { } target || Sessions?.Connector.Find(target.Session) is not { } session)
        {
            return;
        }
        if (DataTabQuery.FilterError(text) is { } error)
        {
            ShowEditError(error);
            return;
        }
        var key = KeyOf(session, target.Table);
        var filter = DataTabQuery.NormalizeFilter(text) is null ? null : text.Trim();
        SetTableState(key, "Filter", filter);
        if (filter is not null)
        {
            AddRecentFilter(key, filter);
        }
        LoadDataForFocusedNode(force: true);
    }

    /// <summary>Clear (Ctrl+W) and Remove filter: clears the filter and the multi-column filter and reloads.</summary>
    private void ClearFilter()
    {
        _dataView.MultiColumnBox.Text = string.Empty;
        _dataView.FilterEditor.Text = string.Empty;
        ApplyFilter(string.Empty);
    }

    private readonly Dictionary<(TableKey Table, string Name), string?> _memoryTableState = [];

    private static TableKey KeyOf(OpenSession session, DbObjectRef table) => new(session.Id.ToString("D"), table.Schema, table.Name);

    /// <summary>Per-table state in <c>app.db</c>; without it (measurements, tests), only in memory.</summary>
    private string? GetTableState(TableKey table, string name) =>
        _context.AppDatabase is { } db ? db.GetTableState(table, name) : _memoryTableState.GetValueOrDefault((table, name));

    private void SetTableState(TableKey table, string name, string? value)
    {
        if (_context.AppDatabase is { } db)
        {
            db.SetTableState(table, name, value);
        }
        else
        {
            _memoryTableState[(table, name)] = value;
        }
    }

    /// <summary>
    /// Data tab of the focused object (table or view). Only reloads if the table changed or with <paramref name="force"/> (F5,
    /// sorting, columns, filter); reloading the same table keeps the rows already requested (appendix D B3).
    /// </summary>
    private void LoadDataForFocusedNode(bool force = false)
    {
        if (_tree.Tree.FocusedNode?.Tag is not TreeObjectTag { Object: { Kind: DbObjectKind.Table or DbObjectKind.View } obj } tag
            || Sessions?.Connector.Find(tag.Session) is not { } session)
        {
            return;
        }
        var same = _dataTarget is { } target && target.Session == session.Id && target.Table == obj;
        if (same && !force && _browser is not null)
        {
            return;
        }
        var step = Math.Max(1, _context.Settings.Data.RowsPerStep);
        var wanted = same && _browser is not null ? Math.Max(_browser.Wanted, step) : step;
        _dataTarget = (session.Id, obj);
        _ = LoadTableAsync(session, obj, wanted, BeginDataLoad());
    }

    /// <summary>Starts a Data tab load; the one in progress is cancelled.</summary>
    private CancellationTokenSource BeginDataLoad()
    {
        _dataLoad?.Cancel();
        var cts = new CancellationTokenSource();
        _dataLoad = cts;
        return cts;
    }

    /// <summary>End of a load; returns whether no other one is in progress (the status bar goes back to idle).</summary>
    private bool EndDataLoad(CancellationTokenSource cts)
    {
        if (ReferenceEquals(_dataLoad, cts))
        {
            _dataLoad = null;
        }
        return _dataLoad is null;
    }

    /// <summary>The Data tab is loading a table of this session.</summary>
    private bool IsLoadingData(OpenSession session) => _dataLoad is not null && _dataTarget?.Session == session.Id;

    /// <summary>The node is the table (or one of its columns) that the Data tab shows or is loading.</summary>
    private bool IsDataTarget(TreeNode node) =>
        node.Tag is TreeObjectTag { Object: { } obj } tag && _dataTarget is { } target && target.Session == tag.Session && target.Table == obj;

    /// <summary>
    /// Abandons the load in progress (another choice in the tree, a query on the same session): the server stops the query and the
    /// connection is free right away; going back to the table loads it again.
    /// </summary>
    private void AbandonDataLoad()
    {
        if (_dataLoad is { } cts)
        {
            _dataLoad = null;
            _dataTarget = null;
            cts.Cancel();
        }
    }

    private DataBrowseOptions ReadBrowseOptions(OpenSession session, DbObjectRef table)
    {
        var data = _context.Settings.Data;
        var key = KeyOf(session, table);
        return new DataBrowseOptions
        {
            Hidden = DataTabQuery.ParseHidden(GetTableState(key, "HiddenColumns")),
            Filter = GetTableState(key, "Filter"),
            Sort = DataTabQuery.ParseSort(GetTableState(key, "Sort")),
            RowsPerStep = Math.Max(1, data.RowsPerStep),
            MaximumRows = Math.Max(data.RowsPerStep, data.MaximumRows),
            Storage = new ResultStorage(
                (long)Math.Max(16, data.ResultMemoryMb) * 1024 * 1024,
                _context.SpillDirectory,
                (long)Math.Max(0, data.SpillQuotaMb) * 1024 * 1024)
            {
                Pool = GlobalResultPool(),
            },
        };
    }

    private async Task LoadTableAsync(OpenSession session, DbObjectRef obj, long wanted, CancellationTokenSource cts)
    {
        var cancellationToken = cts.Token;
        // Switching table or reloading saves the changed row first (appendix D B15).
        if (DataGrid.IsEditing || DataGrid.Edits.HasPending)
        {
            await DataGrid.PostAsync();
        }
        _dataLabel.Text = $"{obj.Schema}.{obj.Name}: loading ...";
        _context.Status.Busy = "Fetching rows ...";
        TableBrowser? browser = null;
        try
        {
            var table = await session.Metadata.GetTableAsync(obj, cancellationToken);
            browser = new TableBrowser(session.Host, table, ReadBrowseOptions(session, obj));
            var loading = browser;
            browser.SourceReady += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    ShowBrowser(loading, session);
                }
            });
            await browser.LoadAsync(wanted, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                browser.Dispose();
                return;
            }
            if (!ReferenceEquals(_browser, browser))
            {
                // No result set (should not happen with SELECT): shows the label at least.
                ShowBrowser(browser, session);
            }
            // With fewer rows than before (filter), the focus moves to the last one; the scroll goes back to where it was.
            var rows = browser.Loaded;
            if (DataGrid.Selection.FocusedRow >= rows)
            {
                DataGrid.Selection.Focus(Math.Max(0, rows - 1), Math.Max(0, DataGrid.Selection.FocusedColumn));
            }
            if (_restoreTopRow is { } top)
            {
                DataGrid.TopRow = top;
                _restoreTopRow = null;
            }
            await UpdateDataLabelAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException
                                   || (cancellationToken.IsCancellationRequested && ex is DbOperationException or Engine.Execution.SessionOperationException))
        {
            // Cancelled (Esc, another choice in the tree, Drop of the table): the error of a request already in flight counts as a cancellation.
            browser?.Dispose();
            if (ReferenceEquals(_dataLoad, cts))
            {
                // Esc: the table was left unloaded (the grid may still show the previous one); going back to it loads it again.
                _dataTarget = null;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _context.Log.Append(LogCategory.Error, ex.Message);
            _dataLabel.Text = $"{obj.Schema}.{obj.Name}: {ex.Message}";
            if (ex is DbOperationException or Engine.Execution.SessionOperationException)
            {
                ShowError(ex);
            }
        }
        finally
        {
            if (EndDataLoad(cts))
            {
                _context.Status.SetIdle();
            }
            _ui.Refresh();
        }
    }

    /// <summary>Puts the browser's source in the grid (hidden columns out, sort indicators) and releases the previous browser.</summary>
    private void ShowBrowser(TableBrowser browser, OpenSession session)
    {
        if (ReferenceEquals(_browser, browser) || browser.Source is not { } source)
        {
            return;
        }
        var previous = _browser;
        _browser = browser;
        var hidden = browser.Options.Hidden.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var columns = browser.Select.Columns;
        var visible = Enumerable.Range(0, columns.Count).Where(i => !hidden.Contains(columns[i])).ToList();
        DataGrid.SessionColor = session.Color is { } rgb ? 0xFF000000 | rgb : 0;
        _browserSession = session;
        DataGrid.IsReadOnly = !IsEditable(browser) || _browserSession?.Definition.ReadOnly == true;
        // Reloading the same table (F5, filter, sorting) keeps the focused cell and the scroll (appendix D A9).
        var same = previous is not null && previous.Table.Ref == browser.Table.Ref;
        var (focusRow, focusColumn, topRow) = (DataGrid.Selection.FocusedRow, DataGrid.Selection.FocusedColumn, DataGrid.TopRow);
        ResetDataGridFilter();
        _dataVisibleColumns = visible;
        _browserRows = new FullValueRowSource(source, browser);
        DataGrid.SetSource(new ProjectedRowSource(_browserRows, visible));
        if (same && focusRow >= 0)
        {
            DataGrid.Selection.Focus(focusRow, Math.Clamp(focusColumn, 0, Math.Max(0, visible.Count - 1)));
            _restoreTopRow = topRow;
        }
        DataGrid.SortIndicators = SortIndicatorsFor(browser, visible);
        var total = browser.Table.Columns.Count;
        _dataView.SetColumns(total - browser.Table.Columns.Count(c => hidden.Contains(c.Name)), total);
        _dataView.SetSortCount(browser.Options.Sort.Count);
        _dataView.SetPaging(browser.CanLoadMore);
        var filtered = DataTabQuery.NormalizeFilter(browser.Options.Filter) is not null;
        if (previous is null || previous.Table.Ref != browser.Table.Ref)
        {
            // Another table: the panel shows its saved filter (and opens if there is one).
            _dataView.MultiColumnBox.Text = string.Empty;
            _dataView.FilterEditor.Text = browser.Options.Filter ?? string.Empty;
            _dataView.FilterPanel.IsVisible = filtered;
        }
        _dataView.SetRecentFilters(RecentFiltersOf(KeyOf(session, browser.Table.Ref)));
        _dataView.SetFilterActive(filtered);
        previous?.Dispose();
    }

    private static Dictionary<int, GridSortIndicator> SortIndicatorsFor(TableBrowser browser, IReadOnlyList<int> visible)
    {
        var result = new Dictionary<int, GridSortIndicator>();
        for (var i = 0; i < visible.Count; i++)
        {
            var name = browser.Select.Columns[visible[i]];
            var position = browser.Options.Sort.ToList().FindIndex(s => string.Equals(s.Column, name, StringComparison.OrdinalIgnoreCase));
            if (position >= 0)
            {
                result[i] = new GridSortIndicator(browser.Options.Sort[position].Descending, position + 1);
            }
        }
        return result;
    }

    /// <summary>"Query exact row count" changed: counts again (in a load in progress, the final count already uses the new option).</summary>
    private async Task RecountAsync()
    {
        if (_dataLoad is not null || _browser is null)
        {
            return;
        }
        var cts = BeginDataLoad();
        try
        {
            await UpdateDataLabelAsync(cts.Token);
        }
        finally
        {
            EndDataLoad(cts);
        }
    }

    /// <summary>Label (appendix D B6): approximate (or exact) count only when there is a limit or a filter.</summary>
    private async Task UpdateDataLabelAsync(CancellationToken cancellationToken)
    {
        if (_browser is not { } browser)
        {
            return;
        }
        var table = browser.Table.Ref;
        var limited = browser.Loaded >= browser.Wanted;
        var filtered = DataTabQuery.NormalizeFilter(browser.Options.Filter) is not null;
        var isView = table.Kind is DbObjectKind.View or DbObjectKind.MaterializedView;
        var innoDb = string.Equals(browser.Table.Options.GetValueOrDefault("Engine"), "InnoDB", StringComparison.OrdinalIgnoreCase);
        long? total = null;
        var exact = false;
        if (!isView && (limited || filtered))
        {
            try
            {
                (total, exact) = await browser.CountAsync(_dataView.QueryExactCount, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // A COUNT(*) of a large table does not hold the metadata connection after something else is chosen.
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _context.Log.Append(LogCategory.Error, ex.Message);
            }
        }
        if (!ReferenceEquals(_browser, browser))
        {
            return;
        }
        _dataLabel.Text = DataTabQuery.CountLabel(table.Schema, table.Name, new DataCount(browser.Loaded, limited, filtered, total, exact, innoDb, isView));
        _dataView.SetPaging(browser.CanLoadMore);
        if (browser.ReachedMaximum)
        {
            _context.Log.Append(LogCategory.Info,
                $"Browsing is currently limited to a maximum of {browser.Options.MaximumRows.ToString("N0", CultureInfo.CurrentCulture)} rows. To see more rows, increase this maximum in Tools > Preferences > Data.");
        }
        _ui.Refresh();
    }

    /// <summary>Next (one more step) or Show all (up to the maximum); focuses the first new row (appendix D B3).</summary>
    private async Task LoadMoreAsync(bool all)
    {
        if (_browser is not { CanLoadMore: true } browser)
        {
            return;
        }
        var firstNew = browser.Loaded;
        var cts = BeginDataLoad();
        _context.Status.Busy = "Fetching rows ...";
        try
        {
            await (all ? browser.ShowAllAsync(cts.Token) : browser.NextAsync(cts.Token));
            if (ReferenceEquals(_browser, browser) && browser.Loaded > firstNew)
            {
                DataGrid.Selection.Focus(firstNew, Math.Max(0, DataGrid.Selection.FocusedColumn));
                DataGrid.TopRow = firstNew;
            }
            await UpdateDataLabelAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Esc or another choice: the rows that already arrived stay and Next and Show all continue from there.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowError(ex);
        }
        finally
        {
            if (EndDataLoad(cts))
            {
                _context.Status.SetIdle();
            }
            _ui.Refresh();
        }
    }

    /// <summary>Ctrl+End and Last: Show all (if there is more) and focus on the last row.</summary>
    private async Task ShowAllAndGoLastAsync()
    {
        if (_browser is { CanLoadMore: true })
        {
            await LoadMoreAsync(all: true);
        }
        var last = DataGrid.RowCount - 1;
        if (last >= 0)
        {
            DataGrid.Selection.Focus(last, Math.Max(0, DataGrid.Selection.FocusedColumn));
            DataGrid.TopRow = Math.Max(0, last - DataGrid.ViewportRows + 1);
        }
    }

    /// <summary>Header click: ASC → DESC → removed; a new column is appended, DESC with Shift (appendix D B7).</summary>
    private void OnDataHeaderClicked(object? sender, GridHeaderEventArgs e)
    {
        if (_browser is not { } browser || DataGrid.Source is not ProjectedRowSource projected || e.Column < 0)
        {
            return;
        }
        var name = browser.Select.Columns[projected.InnerColumn(e.Column)];
        _ = ChangeSortAsync(DataTabQuery.ToggleSort(browser.Options.Sort, name, e.Modifiers.HasFlag(KeyModifiers.Shift)));
    }

    private Task ChangeSortAsync(IReadOnlyList<DataSort> sort)
    {
        if (_browser is not { } browser || _dataTarget is not { } target || Sessions?.Connector.Find(target.Session) is not { } session)
        {
            return Task.CompletedTask;
        }
        SetTableState(KeyOf(session, browser.Table.Ref), "Sort", sort.Count > 0 ? DataTabQuery.FormatSort(sort) : null);
        LoadDataForFocusedNode(force: true);
        return Task.CompletedTask;
    }

    private void ShowSortingPopup()
    {
        if (_browser is { } browser)
        {
            _dataView.ShowSortingPopup(browser.Table.Columns.Select(c => c.Name).ToList(), browser.Options.Sort, sort => _ = ChangeSortAsync(sort));
        }
    }

    /// <summary>Columns (v/t): saves the table's hidden columns and reloads (appendix D B8).</summary>
    private void ShowColumnsPopup()
    {
        if (_browser is not { } browser || _dataTarget is not { } target || Sessions?.Connector.Find(target.Session) is not { } session)
        {
            return;
        }
        _dataView.ShowColumnsPopup(browser.Table.Columns.Select(c => c.Name).ToList(), browser.Options.Hidden, hidden =>
        {
            SetTableState(KeyOf(session, browser.Table.Ref), "HiddenColumns", hidden.Count > 0 ? DataTabQuery.FormatHidden(hidden) : null);
            LoadDataForFocusedNode(force: true);
        });
    }
}
