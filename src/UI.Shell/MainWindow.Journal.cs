using Avalonia.Threading;
using Eunomia.App.Query;
using Eunomia.Presentation.Logging;
using Eunomia.UI.Features.QueryTab;
using Eunomia.UI.Shell.Views;

namespace Eunomia.UI.Shell;

/// <summary>
/// Query tab restore (appendix C §4.3; section 21.4; FAM-Q07; PR-21): each tab's text and state (caption, *,
/// file, pinned session, cursor, active tab) go to the journal every 2 s and on closing; on startup, the saved
/// tabs that do not belong to another instance come back in order. Closing a tab removes it from the journal.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Flush interval (PR-21: at most 2 s of text lost in a crash).</summary>
    public static readonly TimeSpan JournalInterval = TimeSpan.FromSeconds(2);

    private DispatcherTimer? _journalTimer;
    private Task _journalFlush = Task.CompletedTask;
    private bool _journalFailed;
    private QueryTab? _restoredFocus;

    private void InitJournal()
    {
        if (_context.TabJournal is null)
        {
            return;
        }
        RestoreTabs();
        _journalTimer = new DispatcherTimer(JournalInterval, DispatcherPriority.Background, (_, _) => SaveJournal());
        _journalTimer.Start();
        Closing += (_, _) => SaveJournal(wait: true);
    }

    /// <summary>Journal tabs: the first goes to the "Query" tab, the others are created in order.</summary>
    private void RestoreTabs()
    {
        var journal = _context.TabJournal!;
        IReadOnlyList<RestoredTab> restored;
        try
        {
            restored = journal.Restore();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _context.Log.Append(LogCategory.Error, "Could not restore query tabs: " + ex.Message);
            return;
        }
        QueryTab? focused = null;
        for (var i = 0; i < restored.Count; i++)
        {
            var (saved, text) = (restored[i].State, restored[i].Text);
            var tab = i == 0 ? _tabs.QueryTabs[0] : _tabs.AddQueryTab(select: false, name: saved.Caption);
            var view = QueryViewOf(tab);
            var state = StateOf(view);
            journal.Remove(state.Id);
            state.Id = saved.Id;
            state.PinnedSession = saved.PinnedSession;
            state.PinnedName = saved.PinnedName;
            state.FilePath = saved.FilePath;
            state.FileEncoding = saved.FileEncoding;
            view.Editor.Text = text;
            journal.Reset(state.Id, text);
            view.Editor.Editor.CaretOffset = Math.Clamp(saved.CaretOffset, 0, text.Length);
            tab.SetCaption(saved.Caption, saved.Modified);
            if (state.FilePath is { } file && File.Exists(file))
            {
                // Restored file: watched again; if it changed while the application was closed, it asks right away.
                state.FileStamp = saved.FileWriteTicks is { } ticks && saved.FileLength is { } length
                    ? (new DateTime(ticks, DateTimeKind.Utc), length)
                    : null;
                Watch(state);
                if (FileStamp(file) != state.FileStamp)
                {
                    Dispatcher.UIThread.Post(() => ScheduleFileCheck(state), DispatcherPriority.Background);
                }
            }
            if (saved.Focused)
            {
                focused = tab;
            }
        }
        UpdateQueryTargets();
        _restoredFocus = focused;
    }

    private TabState TabStateOf(QueryTab tab, QueryTabView view, QueryTabState state, int order) => new()
    {
        Id = state.Id,
        Order = order,
        Caption = tab.Name,
        Modified = tab.IsModified,
        FilePath = state.FilePath,
        FileEncoding = state.FileEncoding,
        FileWriteTicks = state.FileStamp?.Item1.Ticks,
        FileLength = state.FileStamp?.Item2,
        PinnedSession = state.PinnedSession,
        PinnedName = state.PinnedName,
        CaretOffset = view.Editor.Editor.CaretOffset,
        Focused = ReferenceEquals(_tabs.ActiveQueryTab, tab),
    };

    /// <summary>
    /// States that changed and pending text for the journal, in the background; <paramref name="wait"/> waits (when closing
    /// the window and in tests).
    /// </summary>
    internal void SaveJournal(bool wait = false)
    {
        if (_context.TabJournal is not { } journal)
        {
            return;
        }
        var changed = new List<TabState>();
        var order = 0;
        foreach (var tab in _tabs.QueryTabs)
        {
            var current = order++;
            if (!tab.HasContent || tab.Content is not QueryTabView view)
            {
                continue;
            }
            var state = StateOf(view);
            var now = TabStateOf(tab, view, state, current);
            if (now != state.Saved)
            {
                state.Saved = now;
                changed.Add(now);
            }
        }
        if (!_journalFlush.IsCompleted)
        {
            if (!wait)
            {
                // The previous flush is still running: the states are compared again on the next tick.
                foreach (var s in changed)
                {
                    ForgetSaved(s.Id);
                }
                return;
            }
            _journalFlush.Wait();
        }
        _journalFlush = Task.Run(() =>
        {
            try
            {
                foreach (var s in changed)
                {
                    journal.SaveState(s);
                }
                journal.Flush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (!_journalFailed)
                {
                    _journalFailed = true;
                    _context.Log.Append(LogCategory.Error, "Could not save query tabs: " + ex.Message);
                }
            }
        });
        if (wait)
        {
            _journalFlush.Wait();
        }
    }

    private void ForgetSaved(Guid id)
    {
        foreach (var state in _queryStates.Values.Where(s => s.Id == id))
        {
            state.Saved = null;
        }
    }

    /// <summary>The tab closed: it leaves the journal.</summary>
    private void ForgetJournal(Guid id)
    {
        if (_context.TabJournal is not { } journal)
        {
            return;
        }
        try
        {
            journal.Remove(id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _context.Log.Append(LogCategory.Error, "Could not remove query tab backup: " + ex.Message);
        }
    }
}
