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
/// Data tab sobre tabelas reais (Fase 5; secção 4.4; anexo D B2–B12): SELECT com truncagem, Next e Show all, label com
/// as variantes de contagem, ordenação no servidor (clique no header, Sorting, Alt+S) e colunas ocultas, com o estado
/// gravado por tabela no <c>app.db</c>.
/// </summary>
public sealed partial class MainWindow
{
    private TableBrowser? _browser;

    /// <summary>Linhas do browser com o caminho para os valores completos (Ctrl+C e edição de células cortadas).</summary>
    private FullValueRowSource? _browserRows;
    private long? _restoreTopRow;
    private IReadOnlyList<int> _dataVisibleColumns = [];
    private (Guid Session, DbObjectRef Table)? _dataTarget;

    /// <summary>
    /// Carga em curso do Data tab (primeira página, Next, Show all e a contagem da label), ou <c>null</c>. Escolher outra
    /// coisa na árvore ou correr uma query na mesma sessão cancela-a (o driver manda <c>KILL QUERY</c>), em vez de a
    /// ligação ficar ocupada até ao fim de uma tabela grande num servidor lento.
    /// </summary>
    private CancellationTokenSource? _dataLoad;

    /// <summary>Browser do Data tab (testes).</summary>
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
        // Botões First e Last da toolbar: Last é Show all e a última linha (anexo D B3).
        c.SetHandler("grid.first", () =>
        {
            DataGrid.Selection.Focus(0, Math.Max(0, DataGrid.Selection.FocusedColumn));
            DataGrid.TopRow = 0;
        }, () => _browser is not null && DataGrid.RowCount > 0);
        c.SetHandler("grid.last", _ => new ValueTask(ShowAllAndGoLastAsync()), () => _browser is not null && DataGrid.RowCount > 0);
        c.SetHandler("data.showNext", _ => new ValueTask(LoadMoreAsync(all: false)), () => _browser?.CanLoadMore == true);
        c.SetHandler("data.showAll", _ => new ValueTask(LoadMoreAsync(all: true)), () => _browser?.CanLoadMore == true);
        c.SetHandler("grid.resetSorting", _ => new ValueTask(ChangeSortAsync([])), () => _browser is { Options.Sort.Count: > 0 });

        // Filtro WHERE (anexo D B9): F9 e Ctrl+W no painel; F7 foca o multi column filter.
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

    /// <summary>Apply filter: o texto vai para os recentes da tabela (20, sem repetidos) e recarrega (anexo D B9).</summary>
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

    /// <summary>Clear (Ctrl+W) e Remove filter: limpa o filtro e o multi column filter e recarrega.</summary>
    private void ClearFilter()
    {
        _dataView.MultiColumnBox.Text = string.Empty;
        _dataView.FilterEditor.Text = string.Empty;
        ApplyFilter(string.Empty);
    }

    private readonly Dictionary<(TableKey Table, string Name), string?> _memoryTableState = [];

    private static TableKey KeyOf(OpenSession session, DbObjectRef table) => new(session.Id.ToString("D"), table.Schema, table.Name);

    /// <summary>Estado por tabela no <c>app.db</c>; sem ele (medições, testes), só em memória.</summary>
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
    /// Data tab do objeto focado (tabela ou view). Só recarrega se a tabela mudou ou com <paramref name="force"/> (F5,
    /// ordenação, colunas, filtro); ao recarregar a mesma tabela mantém as linhas já pedidas (anexo D B3).
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

    /// <summary>Começa uma carga do Data tab; a que estava em curso é cancelada.</summary>
    private CancellationTokenSource BeginDataLoad()
    {
        _dataLoad?.Cancel();
        var cts = new CancellationTokenSource();
        _dataLoad = cts;
        return cts;
    }

    /// <summary>Fim de uma carga; devolve se não ficou outra em curso (a status bar volta a idle).</summary>
    private bool EndDataLoad(CancellationTokenSource cts)
    {
        if (ReferenceEquals(_dataLoad, cts))
        {
            _dataLoad = null;
        }
        return _dataLoad is null;
    }

    /// <summary>O Data tab está a carregar uma tabela desta sessão.</summary>
    private bool IsLoadingData(OpenSession session) => _dataLoad is not null && _dataTarget?.Session == session.Id;

    /// <summary>O nó é a tabela (ou uma coluna dela) que o Data tab mostra ou está a carregar.</summary>
    private bool IsDataTarget(TreeNode node) =>
        node.Tag is TreeObjectTag { Object: { } obj } tag && _dataTarget is { } target && target.Session == tag.Session && target.Table == obj;

    /// <summary>
    /// Abandona a carga em curso (outra escolha na árvore, query na mesma sessão): o servidor para a query e a ligação
    /// fica livre logo; voltar à tabela carrega-a de novo.
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
        // Mudar de tabela ou recarregar grava antes a linha alterada (anexo D B15).
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
                // Sem result set (não devia acontecer com SELECT): mostra pelo menos a label.
                ShowBrowser(browser, session);
            }
            // Com menos linhas do que antes (filtro), o foco passa para a última; o scroll volta onde estava.
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
            // Cancelada (Esc, outra escolha na árvore, Drop da tabela): o erro de um pedido que já ia a caminho conta como cancelamento.
            browser?.Dispose();
            if (ReferenceEquals(_dataLoad, cts))
            {
                // Esc: a tabela ficou por carregar (o grid pode ter ainda a anterior); voltar a ela carrega de novo.
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

    /// <summary>Põe a fonte do browser no grid (colunas ocultas fora, indicadores de ordenação) e larga o browser anterior.</summary>
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
        // Recarregar a mesma tabela (F5, filtro, ordenação) mantém a célula focada e o scroll (anexo D A9).
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
            // Outra tabela: o painel mostra o filtro gravado dela (e abre se houver).
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

    /// <summary>"Query exact row count" mudou: conta de novo (numa carga em curso, a contagem do fim já usa a opção nova).</summary>
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

    /// <summary>Label (anexo D B6): contagem aproximada (ou exata) só quando há limite ou filtro.</summary>
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
                // Um COUNT(*) de uma tabela grande não prende a ligação de metadata depois de se escolher outra coisa.
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

    /// <summary>Next (mais um passo) ou Show all (até ao máximo); foca a primeira linha nova (anexo D B3).</summary>
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
            // Esc ou outra escolha: as linhas que já chegaram ficam e Next e Show all continuam daí.
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

    /// <summary>Ctrl+End e Last: Show all (se houver mais) e foco na última linha.</summary>
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

    /// <summary>Clique no header: ASC → DESC → removida; coluna nova acrescentada, DESC com Shift (anexo D B7).</summary>
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

    /// <summary>Columns (v/t): grava as colunas ocultas da tabela e recarrega (anexo D B8).</summary>
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
