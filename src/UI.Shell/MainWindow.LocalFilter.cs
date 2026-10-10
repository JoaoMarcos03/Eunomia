using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using Eunomia.Engine.Results;
using Eunomia.Presentation.Logging;
using Eunomia.UI.Controls.Common.TreeList;

namespace Eunomia.UI.Shell;

/// <summary>
/// Bottom filter panel (Ctrl+Alt+F; appendix D B11; FAM-G14): local case-insensitive regex over the loaded rows of the
/// active list (Data grid, query tab result tab, Host and Database tab lists), 500 ms after the last key. The text is kept per
/// list; "x out of y matching. z hidden."; an invalid regex goes to the log and shows everything; the Data grid clears it on
/// every reload.
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
        _tabs.SelectionChanged += (_, _) => SyncLocalFilterBox();
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
            return ReferenceEquals(host.SelectedItem, host.ProcessesTab) ? host.ProcessList : host.DatabaseList;
        }
        if (ReferenceEquals(_tabs.SelectedItem, _tabs.DatabaseTab) && _databaseView is { } database)
        {
            return database.List;
        }
        return ActiveResultView;
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
