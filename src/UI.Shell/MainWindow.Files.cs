using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Eunomia.App.Export;
using Eunomia.App.Query;
using Eunomia.Presentation.Logging;
using Eunomia.UI.Controls.Common.Dialogs;
using Eunomia.UI.Controls.Common.Text;
using Eunomia.UI.Features.QueryTab;
using Eunomia.UI.Shell.Views;

namespace Eunomia.UI.Shell;

/// <summary>
/// Ficheiros nas query tabs (secção 4.7; anexo C §3.2, §4.3): Load SQL file (vários, com a codificação; um ficheiro já
/// aberto só ativa a tab; reutiliza a tab vazia; &gt; 5 MiB pergunta), Save, Save as e Save selection, os 20 recentes no
/// dropdown do Load (Remove absent files, Clear file list), o <c>*</c> das tabs alteradas e a pergunta ao fechar, e a
/// pasta do ficheiro vigiada (alteração externa pergunta "Reload file?"; apagado pergunta se fecha a tab).
/// </summary>
public sealed partial class MainWindow
{
    private readonly Dictionary<QueryTabState, FileSystemWatcher> _fileWatchers = [];
    private readonly Dictionary<QueryTabState, DispatcherTimer> _fileChecks = [];
    private readonly HashSet<QueryTabState> _fileRecheck = [];
    private bool _fileQuestionOpen;

    /// <summary>Espera depois do último evento do ficheiro (uma gravação externa dá vários, a meio da escrita).</summary>
    internal static readonly TimeSpan FileSettleDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>Substitui o diálogo Open (testes): os caminhos escolhidos.</summary>
    internal Func<IReadOnlyList<string>>? OpenSqlFilesOverride { get; set; }

    /// <summary>Substitui o diálogo Save as (testes): nome sugerido → caminho.</summary>
    internal Func<string, string?>? SaveSqlFileOverride { get; set; }

    /// <summary>Substitui a escolha da codificação do Load (testes).</summary>
    internal Func<string?>? LoadEncodingOverride { get; set; }

    /// <summary>Substitui as perguntas Yes/No(/Cancel) dos ficheiros (testes).</summary>
    internal Func<string, MessageDialogResult>? FileQuestionOverride { get; set; }

    private void InitFiles()
    {
        var c = _context.Commands;
        c.SetHandler("query.load", _ => new ValueTask(LoadSqlFilesAsync()));
        c.SetHandler("query.save", _ => new ValueTask(SaveTabAsync(_tabs.ActiveQueryTab!, saveAs: false)), () => _tabs.ActiveQueryTab is not null);
        c.SetHandler("query.saveAs", _ => new ValueTask(SaveTabAsync(_tabs.ActiveQueryTab!, saveAs: true)), () => _tabs.ActiveQueryTab is not null);
        c.SetHandler("query.saveSelection", _ => new ValueTask(SaveSelectionAsync()), () => ActiveQueryView is { Editor.Editor.SelectionLength: > 0 });
        _tabs.ConfirmClose = ConfirmCloseAsync;
    }

    private async Task<MessageDialogResult> AskAsync(string question, MessageDialogButtons buttons) =>
        FileQuestionOverride is { } fake ? fake(question) : await MessageDialog.ShowAsync(this, "Confirm", question, buttons);

    private QueryTab? TabOf(QueryTabView view) => _tabs.QueryTabs.FirstOrDefault(t => t.HasContent && ReferenceEquals(t.Content, view));

    /// <summary>Editar marca a tab com <c>*</c>.</summary>
    private void MarkModified(QueryTabView view)
    {
        if (TabOf(view) is { IsModified: false } tab)
        {
            tab.SetCaption(tab.Name, modified: true);
        }
    }

    /// <summary>"Save changes to file X ?" / "Save content of tab "X"?" (Yes/No/Cancel) quando a tab está alterada.</summary>
    private async Task<bool> ConfirmCloseAsync(QueryTab tab)
    {
        if (!_context.Settings.Query.PromptSaveOnClose || !tab.IsModified || !tab.HasContent || tab.Content is not QueryTabView view)
        {
            return true;
        }
        var state = StateOf(view);
        var question = state.FilePath is { } path ? $"Save changes to file {path} ?" : $"Save content of tab \"{tab.Name}\"?";
        return await AskAsync(question, MessageDialogButtons.YesNo | MessageDialogButtons.Cancel) switch
        {
            MessageDialogResult.Yes => await SaveTabAsync(tab, saveAs: false),
            MessageDialogResult.No => true,
            _ => false,
        };
    }

    private async Task LoadSqlFilesAsync()
    {
        IReadOnlyList<string> paths;
        if (OpenSqlFilesOverride is { } fake)
        {
            paths = fake();
        }
        else
        {
            paths = [.. await PathPicker.OpenFilesAsync(this, new FilePickerOpenOptions
            {
                Title = "Load SQL file",
                AllowMultiple = true,
                FileTypeFilter = [new FilePickerFileType("SQL files") { Patterns = ["*.sql"] }, FilePickerFileTypes.All],
            })];
        }
        if (paths.Count == 0)
        {
            return;
        }
        var encoding = LoadEncodingOverride is { } choose
            ? choose()
            : await ChoiceDialog.ShowAsync(this, "Load SQL file", "Encoding:", SqlFile.LoadEncodings, SqlFile.AutoDetect);
        if (encoding is null)
        {
            return;
        }
        await OpenSqlFilesAsync(paths, encoding, forceRun: false);
    }

    /// <summary>
    /// Um ficheiro numa query tab: a que já o tem, a ativa se estiver vazia, ou uma nova. Com mais de 5 MiB (e
    /// <paramref name="askLarge"/>), o "Opening large files" pode corrê-lo sem o abrir.
    /// </summary>
    internal async Task<bool> LoadFileAsync(string path, string encoding, bool askLarge = true)
    {
        var full = Path.GetFullPath(path);
        foreach (var open in _tabs.QueryTabs.Where(t => t.HasContent && t.Content is QueryTabView))
        {
            if (string.Equals(StateOf((QueryTabView)open.Content).FilePath, full, StringComparison.OrdinalIgnoreCase))
            {
                _tabs.SelectedItem = open.Item;
                return true;
            }
        }
        try
        {
            var info = new FileInfo(full);
            if (askLarge && info.Length > SqlFile.LargeFileBytes)
            {
                switch (await AskLargeFilesAsync([(full, info.Length)]))
                {
                    case LargeFilesChoice.Cancel:
                        return false;
                    case LargeFilesChoice.Run:
                        await RunFilesNowAsync([full], encoding);
                        return false;
                }
            }
            var bytes = await File.ReadAllBytesAsync(full);
            var (text, used) = SqlFile.Decode(bytes, encoding);
            var tab = _tabs.ActiveQueryTab is { } active && QueryViewOf(active) is var activeView && activeView.Editor.Text.Length == 0 && StateOf(activeView).FilePath is null
                ? active
                : _tabs.AddQueryTab();
            var view = QueryViewOf(tab);
            var state = StateOf(view);
            view.Editor.Text = text;
            view.Editor.Editor.CaretOffset = 0;
            _context.TabJournal?.Reset(state.Id, text);
            BindFile(tab, state, full, used);
            _tabs.SelectedItem = tab.Item;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowError(ex);
            return false;
        }
    }

    /// <summary>A tab passa a ser o ficheiro: caption, sem <c>*</c>, recentes e vigilância.</summary>
    private void BindFile(QueryTab tab, QueryTabState state, string path, string encoding)
    {
        state.FilePath = path;
        state.FileEncoding = encoding;
        state.FileStamp = FileStamp(path);
        tab.SetCaption(Path.GetFileName(path), modified: false);
        _context.Settings.Query.RecentFiles = SqlFile.AddRecent(_context.Settings.Query.RecentFiles, path);
        Watch(state);
    }

    /// <summary>Save: no ficheiro da tab, com a codificação dele; sem ficheiro (ou Save as), pergunta onde.</summary>
    internal async Task<bool> SaveTabAsync(QueryTab tab, bool saveAs)
    {
        var view = QueryViewOf(tab);
        var state = StateOf(view);
        var path = saveAs ? null : state.FilePath;
        if (path is null)
        {
            path = await AskSavePathAsync(state.FilePath is { } current ? Path.GetFileName(current) : tab.Name + ".sql");
            if (path is null)
            {
                return false;
            }
        }
        var encoding = state.FileEncoding ?? "UTF-8";
        if (!await WriteFileAsync(path, view.Editor.Text, encoding))
        {
            return false;
        }
        BindFile(tab, state, Path.GetFullPath(path), encoding);
        return true;
    }

    private async Task SaveSelectionAsync()
    {
        var view = ActiveQueryView!;
        var text = view.Editor.Editor.SelectedText;
        if (await AskSavePathAsync("selection.sql") is { } path)
        {
            await WriteFileAsync(path, text, "UTF-8");
            _context.Settings.Query.RecentFiles = SqlFile.AddRecent(_context.Settings.Query.RecentFiles, Path.GetFullPath(path));
        }
    }

    private async Task<string?> AskSavePathAsync(string suggested)
    {
        if (SaveSqlFileOverride is { } fake)
        {
            return fake(suggested);
        }
        return await PathPicker.SaveFileAsync(this, new FilePickerSaveOptions
        {
            Title = "Save SQL file",
            SuggestedFileName = suggested,
            DefaultExtension = "sql",
            FileTypeChoices = [new FilePickerFileType("SQL files") { Patterns = ["*.sql"] }, FilePickerFileTypes.All],
        });
    }

    private async Task<bool> WriteFileAsync(string path, string text, string encoding)
    {
        try
        {
            await File.WriteAllBytesAsync(path, ExportFile.Encode(text, encoding));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError(ex);
            return false;
        }
    }

    private static (DateTime, long)? FileStamp(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? (info.LastWriteTimeUtc, info.Length) : null;
    }

    /// <summary>Vigia a pasta do ficheiro da tab (alterado, apagado ou renomeado fora do editor).</summary>
    private void Watch(QueryTabState state)
    {
        Unwatch(state);
        if (state.FilePath is not { } path || Path.GetDirectoryName(path) is not { } directory || !Directory.Exists(directory))
        {
            return;
        }
        var watcher = new FileSystemWatcher(directory, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };
        watcher.Changed += (_, _) => Dispatcher.UIThread.Post(() => ScheduleFileCheck(state));
        watcher.Deleted += (_, _) => Dispatcher.UIThread.Post(() => _ = OnFileDeletedAsync(state));
        watcher.Renamed += (_, e) => Dispatcher.UIThread.Post(() => OnFileRenamed(state, e.FullPath));
        watcher.EnableRaisingEvents = true;
        _fileWatchers[state] = watcher;
    }

    private void Unwatch(QueryTabState state)
    {
        if (_fileWatchers.Remove(state, out var watcher))
        {
            watcher.Dispose();
        }
        if (_fileChecks.Remove(state, out var timer))
        {
            timer.Stop();
        }
        _fileRecheck.Remove(state);
    }

    /// <summary>Verifica o ficheiro quando os eventos acalmam (debounce de <see cref="FileSettleDelay"/>).</summary>
    private void ScheduleFileCheck(QueryTabState state)
    {
        if (!_fileChecks.TryGetValue(state, out var timer))
        {
            timer = new DispatcherTimer { Interval = FileSettleDelay };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _ = OnFileChangedAsync(state);
            };
            _fileChecks[state] = timer;
        }
        timer.Stop();
        timer.Start();
    }

    private (QueryTab Tab, QueryTabView View)? TabOfState(QueryTabState state) =>
        _queryStates.FirstOrDefault(p => ReferenceEquals(p.Value, state)).Key is { } view && TabOf(view) is { } tab ? (tab, view) : null;

    /// <summary>Alterado fora do editor: "Reload file?" (só uma pergunta de cada vez; as nossas gravações não contam).</summary>
    private async Task OnFileChangedAsync(QueryTabState state)
    {
        if (_fileQuestionOpen)
        {
            // Outra pergunta aberta: volta a ver no fim.
            _fileRecheck.Add(state);
            return;
        }
        if (state.FilePath is not { } path || TabOfState(state) is not var (tab, view))
        {
            return;
        }
        var stamp = FileStamp(path);
        if (stamp is null || stamp == state.FileStamp)
        {
            return;
        }
        state.FileStamp = stamp;
        _fileQuestionOpen = true;
        try
        {
            if (await AskAsync($"File was modified from outside:\n{path}\n\nReload file?", MessageDialogButtons.YesNo) == MessageDialogResult.Yes)
            {
                var (text, used) = SqlFile.Decode(await File.ReadAllBytesAsync(path), state.FileEncoding ?? SqlFile.AutoDetect);
                view.Editor.Text = text;
                _context.TabJournal?.Reset(state.Id, text);
                state.FileEncoding = used;
                state.FileStamp = FileStamp(path);
                tab.SetCaption(tab.Name, modified: false);
            }
        }
        catch (IOException ex)
        {
            _context.Log.Append(LogCategory.Error, ex.Message);
        }
        finally
        {
            _fileQuestionOpen = false;
            foreach (var pending in _fileRecheck.ToList())
            {
                _fileRecheck.Remove(pending);
                ScheduleFileCheck(pending);
            }
        }
    }

    /// <summary>Apagado fora do editor: "Close file and query tab?".</summary>
    private async Task OnFileDeletedAsync(QueryTabState state)
    {
        if (_fileQuestionOpen || state.FilePath is not { } path || File.Exists(path) || TabOfState(state) is not var (tab, _))
        {
            return;
        }
        _fileQuestionOpen = true;
        try
        {
            if (await AskAsync($"File was deleted from outside:\n{path}\n\nClose file and query tab?", MessageDialogButtons.YesNo) == MessageDialogResult.Yes)
            {
                Unwatch(state);
                if (tab.IsClosable)
                {
                    _tabs.CloseQueryTab(tab);
                }
                else
                {
                    state.FilePath = null;
                    tab.SetCaption(tab.Name, modified: true);
                }
            }
            else
            {
                tab.SetCaption(tab.Name, modified: true);
            }
        }
        finally
        {
            _fileQuestionOpen = false;
        }
    }

    /// <summary>Renomeado fora do editor: a tab segue o nome novo.</summary>
    private void OnFileRenamed(QueryTabState state, string newPath)
    {
        if (TabOfState(state) is not var (tab, _))
        {
            return;
        }
        state.FilePath = newPath;
        tab.SetCaption(Path.GetFileName(newPath), tab.IsModified);
        Watch(state);
    }

    /// <summary>Dropdown do Load SQL file: os recentes, Remove absent files e Clear file list.</summary>
    internal IReadOnlyList<object> LoadSqlItems()
    {
        var query = _context.Settings.Query;
        var items = new List<object> { SnippetsMenu(), new Separator() };
        var number = 1;
        foreach (var path in query.RecentFiles)
        {
            var item = new MenuItem { Header = $"{number++} {LiteralText.Escaped(path)}" };
            item.Click += (_, _) => _ = LoadFileAsync(path, SqlFile.AutoDetect);
            items.Add(item);
        }
        if (query.RecentFiles.Count > 0)
        {
            items.Add(new Separator());
        }
        var remove = new MenuItem { Header = "Remove absent files", IsEnabled = query.RecentFiles.Count > 0 };
        remove.Click += (_, _) => query.RecentFiles = [.. query.RecentFiles.Where(File.Exists)];
        var clear = new MenuItem { Header = "Clear file list", IsEnabled = query.RecentFiles.Count > 0 };
        clear.Click += (_, _) => query.RecentFiles = [];
        items.Add(remove);
        items.Add(clear);
        return items;
    }
}
