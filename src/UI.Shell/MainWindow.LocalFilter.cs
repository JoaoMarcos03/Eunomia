using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using Eunomia.Engine.Results;
using Eunomia.Presentation.Logging;
using Eunomia.UI.Controls.Common.Input;
using Eunomia.UI.Controls.Common.TreeList;
using Eunomia.UI.Features.HostTab;

namespace Eunomia.UI.Shell;

/// <summary>
/// Bottom filter panel (Ctrl+Alt+F; appendix C §1.9; appendix D B11; FAM-G14): local case-insensitive regex over the
/// loaded rows of the active list (Data grid, query tab result tab, the five Host lists, the Database tab list and the
/// table editor columns), 500 ms after the last key; the clear button applies at once. The text is kept per list;
/// "x out of y matching. z hidden."; an invalid regex goes to the log and shows everything; the Data grid clears it on
/// every reload (Next and Show all included).
/// </summary>
public sealed partial class MainWindow
{
    private const string DataGridFilterKey = "data";

    private readonly Dictionary<object, string> _localFilterTexts = [];
    private DispatcherTimer? _localFilterTimer;
    private CancellationTokenSource? _localFilterScan;
    private object? _localFilterTarget;

    /// <summary>The Data grid shows a view filtered by the local filter (clearing it restores the whole source).</summary>
    private bool _dataGridFiltered;

    /// <summary>List last typed into in the box (the one the 500 ms timer filters).</summary>
    private object? _localFilterTyped;

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
        _filterBox.WithClearButton(cleared: ClearLocalFilter);
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

    /// <summary>
    /// The Table tab's text belongs to the tab (appendix D B11: per tab), not to the editor: the editor is rebuilt when
    /// coming back to the table or opening another one, and the filter keeps applying to the new editor's columns.
    /// </summary>
    private void ReapplyTableEditorFilter()
    {
        if (TableEditor is not { } editor || _localFilterTexts.GetValueOrDefault(_tabs.TableTab) is not { Length: > 0 } text || LocalFilterRegex(text) is not { } regex)
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

    /// <summary>The active list changed: the box shows that list's stored text.</summary>
    private void SyncLocalFilterBox()
    {
        var target = LocalFilterTarget();
        if (ReferenceEquals(target, _localFilterTarget))
        {
            return;
        }
        _localFilterTarget = target;
        _filterBox.Text = target is not null ? _localFilterTexts.GetValueOrDefault(target) ?? string.Empty : string.Empty;
        _filterInfo.Text = string.Empty;
    }

    /// <summary>The Data grid reloaded: the local filter is cleared (appendix D B11).</summary>
    private void ResetDataGridFilter()
    {
        _localFilterTexts.Remove(DataGridFilterKey);
        _localFilterScan?.Cancel();
        _dataGridFiltered = false;
        if (ReferenceEquals(LocalFilterTarget(), DataGridFilterKey))
        {
            _filterBox.Text = string.Empty;
            _filterInfo.Text = string.Empty;
        }
    }

    private Regex? LocalFilterRegex(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        try
        {
            return new Regex(text, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
        }
        catch (ArgumentException)
        {
            _context.Log.Append(LogCategory.Info, "Filter text is not a valid regular expression");
            return null;
        }
    }

    private static string MatchText(long matching, long total) =>
        string.Create(CultureInfo.CurrentCulture, $"{matching:N0} out of {total:N0} matching. {total - matching:N0} hidden.");

    internal async Task ApplyLocalFilterAsync()
    {
        var target = LocalFilterTarget();
        var text = _filterBox.Text ?? string.Empty;
        var regex = LocalFilterRegex(text);
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
            case DataGridFilterKey when _browser is { } browser && _browserRows is { } source:
                _localFilterScan?.Cancel();
                var scan = new CancellationTokenSource();
                _localFilterScan = scan;
                if (regex is null)
                {
                    // With no filter applied, nothing to restore: SetSource took the focus and the scroll to the first row.
                    if (_dataGridFiltered)
                    {
                        DataGrid.SetSource(new ProjectedRowSource(source, _dataVisibleColumns));
                        _dataGridFiltered = false;
                    }
                    _filterInfo.Text = string.Empty;
                    return;
                }
                _context.Status.Busy = "Filtering rows ...";
                try
                {
                    var filtered = await FilteredRowSource.CreateAsync(source, regex, scan.Token);
                    if (scan.IsCancellationRequested || !ReferenceEquals(_browser, browser))
                    {
                        return;
                    }
                    DataGrid.SetSource(new ProjectedRowSource(filtered, _dataVisibleColumns));
                    _dataGridFiltered = true;
                    _filterInfo.Text = MatchText(filtered.MatchCount, filtered.TotalRows);
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
}
