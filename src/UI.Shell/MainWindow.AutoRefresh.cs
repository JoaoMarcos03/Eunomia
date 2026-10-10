using System.Globalization;
using Avalonia.Threading;
using Eunomia.Drivers.Abstractions;
using Eunomia.UI.Controls.Common.Dialogs;

namespace Eunomia.UI.Shell;

/// <summary>
/// Auto refresh (Ctrl+F5; Refresh drop-down, appendix C §3.12): reloads the active tab's list at every interval (Host tab:
/// the sub-tab's list, such as the processes; Database tab: the database's objects; Data tab: the rows, if there is no
/// unsaved edit). "Set interval ..." asks for the seconds. Plus the processes' "EXPLAIN query" (appendix C §1.8): a new
/// query tab running <c>USE database; EXPLAIN sql;</c>.
/// </summary>
public sealed partial class MainWindow
{
    private DispatcherTimer? _autoRefresh;
    private int _autoRefreshSeconds = 10;
    private bool _autoRefreshBusy;

    /// <summary>Auto refresh interval in seconds (tests).</summary>
    internal int AutoRefreshSeconds => _autoRefreshSeconds;

    /// <summary>Replaces the interval prompt (tests).</summary>
    internal Func<int, Task<string?>>? AutoRefreshIntervalOverride { get; set; }

    private void InitAutoRefresh()
    {
        var c = _context.Commands;
        c.SetHandler("view.autoRefresh", ToggleAutoRefresh, () => _autoRefresh is not null || ActiveSession is not null);
        c.SetChecked("view.autoRefresh", () => _autoRefresh is not null);
        c.SetHandler("view.autoRefreshInterval", _ => new ValueTask(SetAutoRefreshIntervalAsync()));
        c.SetHandler("host.explainProcess", _ => new ValueTask(ExplainProcessAsync()),
            () => _hostView is { } v && ReferenceEquals(v.SelectedItem, v.ProcessesTab) && v.ProcessList.FocusedNode?.Tag is ServerProcess { Info.Length: > 0 });
        Closed += (_, _) => _autoRefresh?.Stop();
    }

    private void ToggleAutoRefresh()
    {
        if (_autoRefresh is { } timer)
        {
            timer.Stop();
            _autoRefresh = null;
            return;
        }
        _autoRefresh = new DispatcherTimer(TimeSpan.FromSeconds(_autoRefreshSeconds), DispatcherPriority.Background, (_, _) => _ = AutoRefreshTickAsync());
        _autoRefresh.Start();
    }

    private async Task SetAutoRefreshIntervalAsync()
    {
        var text = AutoRefreshIntervalOverride is { } ask
            ? await ask(_autoRefreshSeconds)
            : await InputDialog.ShowAsync(this, "Auto refresh", "Refresh interval in seconds:", _autoRefreshSeconds.ToString(CultureInfo.CurrentCulture));
        if (text is null)
        {
            return;
        }
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var seconds) || seconds < 1 || seconds > 86_400)
        {
            ShowError(new FormatException("Please enter a number of seconds between 1 and 86400."));
            return;
        }
        _autoRefreshSeconds = seconds;
        if (_autoRefresh is { } timer)
        {
            timer.Interval = TimeSpan.FromSeconds(seconds);
        }
    }

    /// <summary>One auto refresh round: only the active tab, and never two at the same time.</summary>
    internal async Task AutoRefreshTickAsync()
    {
        if (_autoRefreshBusy || ActiveSession is not { } open)
        {
            return;
        }
        _autoRefreshBusy = true;
        try
        {
            if (ReferenceEquals(_tabs.SelectedItem, _tabs.HostTab))
            {
                await RefreshHostAsync();
            }
            else if (ReferenceEquals(_tabs.SelectedItem, _tabs.DatabaseTab) && _tree.Tree.FocusedNode?.Database is { } db)
            {
                open.Metadata.Invalidate(db.Name);
                await RefreshDatabaseTabAsync();
            }
            else if (ReferenceEquals(_tabs.SelectedItem, _tabs.DataTab) && !DataGrid.IsEditing && !DataGrid.Edits.HasPending)
            {
                LoadDataForFocusedNode(force: true);
            }
        }
        finally
        {
            _autoRefreshBusy = false;
        }
    }

    /// <summary>EXPLAIN query (processes): a new query tab with <c>USE database; EXPLAIN sql;</c>, and runs it.</summary>
    private async Task ExplainProcessAsync()
    {
        if (_hostView?.ProcessList.FocusedNode?.Tag is not ServerProcess { Info: { Length: > 0 } info } process
            || ActiveSession is not { Host.ServerServices: { } services })
        {
            return;
        }
        // The text belongs to another session: only one explainable statement goes into the EXPLAIN (a "SELECT 1; DROP …" in
        // INFO must not run with the privileges of whoever is watching the processes).
        var statements = Eunomia.Engine.Sql.Splitting.SqlSplitter.Split(info);
        var first = statements.Count == 1 ? statements[0].Text.TrimStart('(', ' ', '\t', '\r', '\n') : string.Empty;
        var word = new string([.. first.TakeWhile(char.IsLetter)]).ToUpperInvariant();
        if (word is not ("SELECT" or "INSERT" or "UPDATE" or "DELETE" or "REPLACE" or "TABLE" or "WITH"))
        {
            ShowEditError("EXPLAIN works only on a single SELECT, INSERT, UPDATE, DELETE, REPLACE or TABLE statement.");
            return;
        }
        var sql = (process.Database is { Length: > 0 } db ? $"USE {services.Dialect.QuoteIdentifier(db)};\n" : string.Empty) + "EXPLAIN " + statements[0].Text + ";";
        var tab = _tabs.AddQueryTab();
        QueryEditorOf(tab).Text = sql;
        await RunQueryAsync(RunScope.All);
    }
}
