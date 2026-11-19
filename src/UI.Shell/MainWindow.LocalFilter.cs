using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Input;
using Avalonia.Threading;
using Eunomia.App.Data;
using Eunomia.Engine.Results;
using Eunomia.Presentation.Logging;
using Eunomia.UI.Controls.Common.Input;
using Eunomia.UI.Controls.Common.TreeList;
using Eunomia.UI.Features.HostTab;

namespace Eunomia.UI.Shell;

/// <summary>
/// Bottom filter panel (Ctrl+Alt+F; appendix C §1.9; appendix D B11; FAM-G14): terms separated by <c>;</c>, any of them
/// (<c>\;</c> is a <c>;</c>), plain text without case or, with the <c>.*</c> button, regular expressions
/// (<see cref="SearchTerms"/>). The Data grid searches the whole table on the server, together with the WHERE filter, and
/// keeps the search while it shows the same table (F5, sorting, Next, Show all); the other lists (query tab result tab, the
/// five Host lists, the Database tab list and the table editor columns) filter the rows shown, "x out of y matching. z
/// hidden.". It applies 500 ms after the last key, and at once with Enter, the clear button and the <c>.*</c> button. The
/// text is kept per list; an invalid regex goes to the log; closing the panel clears every list.
/// </summary>
public sealed partial class MainWindow
{
    private const string DataGridFilterKey = "data";

    private const string FilterTip =
        "Shows the rows with a value that contains the text, in upper or lower case.\n" +
        "a;b finds a or b (spaces around the ; are ignored); \\; is a ; (a\\;b finds \"a;b\").\n" +
        ".* (button on the right): each term is a regular expression.\n" +
        "Data tab: searches the whole table on the server, together with the WHERE filter. Other lists: the rows shown.\n" +
        "Enter applies at once; closing the panel clears the filter.";

    private readonly Dictionary<object, string> _localFilterTexts = [];

    /// <summary>
    /// Terms last applied to each list (the Data grid's are <see cref="_dataSearch"/>). A list shown again whose text or mode
    /// changed meanwhile (panel closed, <c>.*</c> toggled on another list) is filtered again.
    /// </summary>
    private readonly Dictionary<object, SearchTerms?> _localFilterApplied = [];

    private DispatcherTimer? _localFilterTimer;
    private CancellationTokenSource? _localFilterScan;
    private object? _localFilterTarget;

    /// <summary>List last typed into in the box (the one the 500 ms timer filters).</summary>
    private object? _localFilterTyped;

    /// <summary>Search of the Data grid (whole table, on the server), or <c>null</c>.</summary>
    private SearchTerms? _dataSearch;

    /// <summary>Panel label for the Data grid's search, shown again when coming back to the Data tab.</summary>
    private string _dataSearchInfo = string.Empty;

    private bool FilterRegex => _context.Settings.Display.FilterRegex;

    private void InitLocalFilter()
    {
        _localFilterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _localFilterTimer.Tick += (_, _) =>
        {
            _localFilterTimer.Stop();
            // Only on the list that was typed into: switching tab within the 500 ms does not apply the filter to the next list.
            if (ReferenceEquals(LocalFilterTarget(), _localFilterTyped))
            {
                _ = ApplyLocalFilterAsync();
            }
        };
        _filterBox.TextChanged += (_, _) =>
        {
            // Avalonia's TextChanged arrives after the change (asynchronously): text set by code (list switch, Data grid
            // reload) is already stored and does not apply the filter again. Before, 500 ms after opening the Data tab
            // or reloading, the empty filter reset the grid's source and the focus and scroll went back to the first row.
            var text = _filterBox.Text ?? string.Empty;
            var target = LocalFilterTarget();
            if (target is null || string.Equals(_localFilterTexts.GetValueOrDefault(target) ?? string.Empty, text, StringComparison.Ordinal))
            {
                return;
            }
            _localFilterTexts[target] = text;
            _localFilterTyped = target;
            _localFilterTimer.Stop();
            _localFilterTimer.Start();
        };
        _filterBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
            {
                e.Handled = true;
                ApplyLocalFilterNow();
            }
        };
        _filterBox.WithClearButton(cleared: ClearLocalFilter);
        _filterRegexButton.IsCheckedChanged += (_, _) =>
        {
            _context.Settings.Display.FilterRegex = _filterRegexButton.IsChecked == true;
            UpdateFilterPlaceholder();
            ApplyLocalFilterNow();
        };
        UpdateFilterPlaceholder();
        _tabs.SelectionChanged += (_, _) => SyncLocalFilterBox();
        // Another table in the Table tab (or the Host and Database tab views being created) changes the list without a tab change.
        foreach (var tab in new[] { _tabs.TableTab, _tabs.HostTab, _tabs.DatabaseTab })
        {
            tab.PropertyChanged += (_, e) =>
            {
                if (e.Property == Avalonia.Controls.ContentControl.ContentProperty)
                {
                    SyncLocalFilterBox();
                    ReapplyTableEditorFilter();
                }
            };
        }
    }

    /// <summary>The syntax in the box itself, for the current mode.</summary>
    private void UpdateFilterPlaceholder() =>
        _filterBox.PlaceholderText = (FilterRegex ? "Regular expression" : "Text to find") + "  (a;b → a or b,  \\; → ;)";

    /// <summary>Enter and the <c>.*</c> button: the active list's filter at once, without waiting the 500 ms.</summary>
    private void ApplyLocalFilterNow()
    {
        _localFilterTimer?.Stop();
        if (LocalFilterTarget() is { } target)
        {
            // Stored before TextChanged (which arrives later), so it does not start the 500 ms again.
            _localFilterTexts[target] = _filterBox.Text ?? string.Empty;
            _ = ApplyLocalFilterAsync();
        }
    }

    /// <summary>
    /// The Table tab's text belongs to the tab (appendix D B11: per tab), not to the editor: the editor is rebuilt when
    /// coming back to the table or opening another one, and the filter keeps applying to the new editor's columns.
    /// </summary>
    private void ReapplyTableEditorFilter()
    {
        if (TableEditor is not { } editor || _localFilterTexts.GetValueOrDefault(_tabs.TableTab) is not { Length: > 0 } text)
        {
            return;
        }
        var terms = SearchTerms.Parse(text, FilterRegex);
        _localFilterApplied[_tabs.TableTab] = terms;
        if (LocalFilterRegex(terms) is not { } regex)
        {
            return;
        }
        try
        {
            var (matching, total) = editor.FilterColumns(regex);
            if (ReferenceEquals(LocalFilterTarget(), _tabs.TableTab))
            {
                _filterInfo.Text = MatchText(matching, total);
            }
        }
        catch (RegexMatchTimeoutException)
        {
            _context.Log.Append(LogCategory.Info, "Filter text is not a valid regular expression");
        }
    }

    /// <summary>Clear button: shows everything at once, without waiting the 500 ms.</summary>
    private void ClearLocalFilter()
    {
        _localFilterTimer?.Stop();
        if (LocalFilterTarget() is { } target)
        {
            // Stored before TextChanged (which arrives later), so the empty filter is not applied again 500 ms later.
            _localFilterTexts[target] = string.Empty;
            _ = ApplyLocalFilterAsync();
        }
    }

    /// <summary>
    /// Panel closed: every list loses its text. The active one shows everything at once; the others (the Data grid
    /// included) when they are shown again (<see cref="SyncLocalFilterBox"/>).
    /// </summary>
    private void ClearLocalFilters()
    {
        _localFilterTimer?.Stop();
        _localFilterTexts.Clear();
        _filterBox.Text = string.Empty;
        _filterInfo.Text = string.Empty;
        _ = ApplyLocalFilterAsync();
    }

    /// <summary>A list that no longer exists (result tab of a run replaced or closed).</summary>
    private void ForgetLocalFilter(object target)
    {
        _localFilterTexts.Remove(target);
        _localFilterApplied.Remove(target);
    }

    /// <summary>List the filter panel filters now (key of the stored text), or <c>null</c>.</summary>
    private object? LocalFilterTarget()
    {
        if (ReferenceEquals(_tabs.SelectedItem, _tabs.DataTab))
        {
            return DataGridFilterKey;
        }
        if (ReferenceEquals(_tabs.SelectedItem, _tabs.HostTab) && _hostView is { } host)
        {
            return HostListOf(host);
        }
        if (ReferenceEquals(_tabs.SelectedItem, _tabs.DatabaseTab) && _databaseView is { } database)
        {
            return database.List;
        }
        if (ReferenceEquals(_tabs.SelectedItem, _tabs.TableTab))
        {
            // Only with the table editor showing (appendix C §1.9); the code editor of views and routines has no list.
            return TableEditor is not null ? _tabs.TableTab : null;
        }
        return ActiveResultView;
    }

    /// <summary>List of the visible Host sub-tab (before, every tab other than Processes filtered the Databases list).</summary>
    private static TreeListControl HostListOf(HostTabView host)
    {
        if (ReferenceEquals(host.SelectedItem, host.ProcessesTab))
        {
            return host.ProcessList;
        }
        if (ReferenceEquals(host.SelectedItem, host.VariablesTab))
        {
            return host.VariableList;
        }
        if (ReferenceEquals(host.SelectedItem, host.StatusTab))
        {
            return host.StatusList;
        }
        return ReferenceEquals(host.SelectedItem, host.CommandStatisticsTab) ? host.CommandList : host.DatabaseList;
    }

    /// <summary>
    /// The active list changed: the box shows that list's stored text, and the list is filtered again if its text or the
    /// mode changed while it was not shown.
    /// </summary>
    private void SyncLocalFilterBox()
    {
        var target = LocalFilterTarget();
        if (ReferenceEquals(target, _localFilterTarget))
        {
            return;
        }
        _localFilterTarget = target;
        var text = target is not null ? _localFilterTexts.GetValueOrDefault(target) ?? string.Empty : string.Empty;
        _filterBox.Text = text;
        var isData = ReferenceEquals(target, DataGridFilterKey);
        _filterInfo.Text = isData ? _dataSearchInfo : string.Empty;
        var applied = isData ? _dataSearch : target is not null ? _localFilterApplied.GetValueOrDefault(target) : null;
        if (target is not null && !Equals(SearchTerms.Parse(text, FilterRegex), applied))
        {
            _ = ApplyLocalFilterAsync();
        }
    }

    /// <summary>Another table in the Data tab: its search does not carry over (appendix D B11).</summary>
    private void ClearDataSearch()
    {
        _dataSearch = null;
        _dataSearchInfo = string.Empty;
        _localFilterTexts.Remove(DataGridFilterKey);
        if (ReferenceEquals(LocalFilterTarget(), DataGridFilterKey))
        {
            _filterBox.Text = string.Empty;
            _filterInfo.Text = string.Empty;
        }
    }

    /// <summary>Regex of the lists filtered in memory; <c>null</c> without terms or when not valid (goes to the log).</summary>
    private Regex? LocalFilterRegex(SearchTerms? terms)
    {
        if (terms is null)
        {
            return null;
        }
        try
        {
            return terms.LocalRegex();
        }
        catch (ArgumentException)
        {
            _context.Log.Append(LogCategory.Info, "Filter text is not a valid regular expression");
            return null;
        }
    }

    private static string MatchText(long matching, long total) =>
        string.Create(CultureInfo.CurrentCulture, $"{matching:N0} out of {total:N0} matching. {total - matching:N0} hidden.");

    /// <summary>Panel label for the Data grid's search, from the rows loaded and the exact count, if there is one.</summary>
    private static string DataSearchInfo(TableBrowser browser, long? total, bool exact)
    {
        var loaded = browser.Loaded;
        var more = browser.CanLoadMore ? ". Next or Show all for more." : ".";
        if (exact && total is { } all)
        {
            return all <= loaded
                ? $"{MatchingRows(all)}."
                : string.Create(CultureInfo.CurrentCulture, $"First {loaded:N0} of {MatchingRows(all)}{more}");
        }
        return loaded < browser.Wanted ? $"{MatchingRows(loaded)}." : $"First {MatchingRows(loaded)}{more}";
    }

    private static string MatchingRows(long count) =>
        string.Create(CultureInfo.CurrentCulture, $"{count:N0} matching {(count == 1 ? "row" : "rows")}");

    internal async Task ApplyLocalFilterAsync()
    {
        if (LocalFilterTarget() is not { } target)
        {
            return;
        }
        var terms = SearchTerms.Parse(_filterBox.Text, FilterRegex);
        if (ReferenceEquals(target, DataGridFilterKey))
        {
            ApplyDataSearch(terms);
            return;
        }
        _localFilterApplied[target] = terms;
        var regex = LocalFilterRegex(terms);
        switch (target)
        {
            case TreeListControl list:
                list.SetFilter(regex is null ? null : node => node.Cells.Any(c => regex.IsMatch(c)));
                if (regex is null)
                {
                    _filterInfo.Text = string.Empty;
                }
                else
                {
                    var nodes = list.AllNodes().ToList();
                    _filterInfo.Text = MatchText(nodes.Count(n => n.Cells.Any(c => regex.IsMatch(c))), nodes.Count);
                }
                break;
            case Avalonia.Controls.TabItem when TableEditor is { } editor:
                try
                {
                    var (matchingColumns, totalColumns) = editor.FilterColumns(regex);
                    _filterInfo.Text = regex is null ? string.Empty : MatchText(matchingColumns, totalColumns);
                }
                catch (RegexMatchTimeoutException)
                {
                    _context.Log.Append(LogCategory.Info, "Filter text is not a valid regular expression");
                    _filterInfo.Text = string.Empty;
                }
                break;
            case ResultView entry:
                _localFilterScan?.Cancel();
                var resultScan = new CancellationTokenSource();
                _localFilterScan = resultScan;
                var view = entry.View;
                if (regex is null)
                {
                    entry.Grid.Control.SetSource(view);
                    _filterInfo.Text = string.Empty;
                    return;
                }
                _context.Status.Busy = "Filtering rows ...";
                try
                {
                    var filteredResult = await FilteredRowSource.CreateAsync(view, regex, resultScan.Token);
                    if (resultScan.IsCancellationRequested || !ReferenceEquals(entry.View, view))
                    {
                        return;
                    }
                    entry.Grid.Control.SetSource(filteredResult);
                    _filterInfo.Text = MatchText(filteredResult.MatchCount, filteredResult.TotalRows);
                }
                catch (OperationCanceledException)
                {
                }
                catch (RegexMatchTimeoutException)
                {
                    _context.Log.Append(LogCategory.Info, "Filter text is not a valid regular expression");
                }
                finally
                {
                    _context.Status.SetIdle();
                }
                break;
        }
    }

    /// <summary>
    /// Data grid: the search runs on the server, on the whole table and together with the WHERE filter; a different one
    /// reloads the table (the load in progress is cancelled). An invalid regex goes to the log and the grid stays as it is.
    /// </summary>
    private void ApplyDataSearch(SearchTerms? search)
    {
        if (search is { IsRegex: true } && LocalFilterRegex(search) is null)
        {
            return;
        }
        if (Equals(search, _dataSearch))
        {
            return;
        }
        _dataSearch = search;
        if (search is null)
        {
            _dataSearchInfo = string.Empty;
            _filterInfo.Text = string.Empty;
        }
        LoadDataForFocusedNode();
    }
}
