using Avalonia.Threading;
using Eunomia.App.Query;
using Eunomia.Presentation.Logging;
using Eunomia.UI.Features.QueryTab;
using Eunomia.UI.Shell.Views;

namespace Eunomia.UI.Shell;

/// <summary>
/// Restauro das query tabs (anexo C §4.3; secção 21.4; FAM-Q07; PR-21): o texto e o estado de cada tab (caption, *,
/// ficheiro, sessão fixada, cursor, tab ativa) vão para o journal de 2 em 2 s e ao fechar; no arranque, as tabs
/// gravadas que não são de outra instância voltam pela ordem. Fechar uma tab apaga-a do journal.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Intervalo do flush (PR-21: no máximo 2 s de texto perdido num crash).</summary>
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

    /// <summary>Tabs do journal: a primeira vai para a tab "Query", as outras são criadas pela ordem.</summary>
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
                // Ficheiro restaurado: volta a ser vigiado; se mudou enquanto a aplicação estava fechada, pergunta já.
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
    /// Estados que mudaram e texto pendente para o journal, em background; <paramref name="wait"/> espera (ao fechar a
    /// janela e nos testes).
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
                // O flush anterior ainda corre: os estados voltam a ser comparados no próximo tick.
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

    /// <summary>A tab fechou: sai do journal.</summary>
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
