using System.Globalization;
using Avalonia.Threading;
using Eunomia.App.Commands;
using Eunomia.App.Query;
using Eunomia.App.Sessions;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Execution;
using Eunomia.Engine.Results;
using Eunomia.Engine.Sql;
using Eunomia.Engine.Sql.Completion;
using Eunomia.Engine.Sql.Splitting;
using Eunomia.Presentation.Logging;
using Eunomia.UI.Controls.Common.Dialogs;
using Eunomia.UI.Controls.Common.Input;
using Eunomia.UI.Controls.Common.Theming;
using Eunomia.UI.Controls.Grid;
using Eunomia.UI.Features.QueryTab;
using Eunomia.UI.Shell.Commands;
using Eunomia.UI.Shell.Sessions;
using Eunomia.UI.Shell.Views;

namespace Eunomia.UI.Shell;

/// <summary>
/// Execution in the query tabs (section 4.7; appendix C §4.4; appendix D B18; FAM-Q01–Q04, Q11): Run, Run selection and Run
/// current query with the <see cref="QueryRunner"/>, one result tab per result set, a warning for UPDATE/DELETE without WHERE,
/// an error with the line in pink and the cursor on it, summary and warnings in the SQL log, history. Cancel acts on the tab's
/// own run (D2). Result tabs sort locally by a column (header click), in the background and cancellable (P20).
/// </summary>
public sealed partial class MainWindow
{
    private readonly Dictionary<QueryTabView, QueryTabState> _queryStates = [];

    private enum RunScope
    {
        All,
        Selection,
        Current,

        /// <summary>Query › Explain current query: <c>EXPLAIN</c> of the statement under the cursor.</summary>
        Explain,
    }

    /// <summary>State of a query tab: the run in progress and the results of the last one.</summary>
    private sealed class QueryTabState
    {
        public CancellationTokenSource? Run { get; set; }

        /// <summary>Session of the run in progress.</summary>
        public OpenSession? Session { get; set; }

        /// <summary>"Pin tab to session" (D9): the tab always runs on this session, connected or not.</summary>
        public Guid? PinnedSession { get; set; }

        /// <summary>Name of the pinned session (for the indicator, if the session stops existing).</summary>
        public string? PinnedName { get; set; }

        /// <summary>Identity in the journal (section 21.4).</summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>The tab's file (Load / Save) and its encoding.</summary>
        public string? FilePath { get; set; }

        public string? FileEncoding { get; set; }

        /// <summary>Table whose columns the helpers panel shows (and whether the columns were already there).</summary>
        public (DbObjectRef? Table, bool WithColumns)? HelpersShown { get; set; }

        /// <summary>Last state saved in the journal.</summary>
        public TabState? Saved { get; set; }

        /// <summary>Date and size of the file after the last Load or Save (our own saves do not ask "Reload").</summary>
        public (DateTime, long)? FileStamp { get; set; }

        public List<ResultView> Results { get; } = [];
    }

    /// <summary>A result tab: the result, the local sort order and the view shown (before the local filter).</summary>
    private sealed class ResultView(QueryResult result, DataGridView grid)
    {
        public QueryResult Result { get; } = result;

        public DataGridView Grid { get; } = grid;

        public int SortColumn { get; set; } = -1;

        public bool Descending { get; set; }

        public IRowSource View { get; set; } = result.Source;

        public CancellationTokenSource? Sort { get; set; }

        /// <summary>Source table when the result is editable (appendix D B18).</summary>
        public TableDefinition? EditTable { get; set; }
    }

    private void InitQueryCommands()
    {
        var c = _context.Commands;
        c.SetHandler("query.run", _ => new ValueTask(RunQueryAsync(RunScope.All)), CanRunQuery);
        c.SetHandler("query.runSelection", _ => new ValueTask(RunQueryAsync(RunScope.Selection)), CanRunQuery);
        c.SetHandler("query.runCurrent", _ => new ValueTask(RunQueryAsync(RunScope.Current)), CanRunQuery);
        c.SetHandler("query.explainCurrent", _ => new ValueTask(RunQueryAsync(RunScope.Explain)), CanRunQuery);
        c.SetHandler("operation.cancel", CancelOperation, CanCancelOperation);
        c.SetHandler("query.previousResult", () => ActiveQueryView!.SelectResult(-1), () => ActiveQueryView is { Results.ItemCount: > 1 });
        c.SetHandler("query.nextResult", () => ActiveQueryView!.SelectResult(1), () => ActiveQueryView is { Results.ItemCount: > 1 });
        c.SetHandler("query.setDelimiter", _ => new ValueTask(SetDelimiterAsync()));
        c.SetHandler("query.pinToSession", TogglePin, () => PinTarget() is { } t && (t.State.PinnedSession is not null || ActiveSession is not null));
        c.SetChecked("query.pinToSession", () => PinTarget()?.State.PinnedSession is not null);
        if (Sessions is { } sessions)
        {
            sessions.Connector.SessionOpened += (_, _) => Dispatcher.UIThread.Post(UpdateQueryTargets);
            sessions.Connector.SessionClosed += (_, _) => Dispatcher.UIThread.Post(UpdateQueryTargets);
        }
        _tabs.CanCloseQueryTab = tab =>
        {
            if (tab.HasContent && tab.Content is QueryTabView running && StateOf(running).Run is not null)
            {
                _context.Log.Append(LogCategory.Info, $"Query tab \"{tab.Name}\" is busy. Cancel the running query before closing it.");
                return false;
            }
            return true;
        };
        _tabs.QueryTabClosing += (_, tab) =>
        {
            if (tab.HasContent && tab.Content is QueryTabView view && _queryStates.Remove(view, out var state))
            {
                state.Run?.Cancel();
                ClearQueryResults(view, state);
                ForgetJournal(state.Id);
                Unwatch(state);
            }
        };
    }

    /// <summary>Content of a new query tab: editor, helpers and the results tabset.</summary>
    private QueryTabView CreateQueryContent()
    {
        var panels = _context.Settings.Panels;
        var view = new QueryTabView(panels.QueryEditorHeight, panels.QueryHelpersWidth);
        var editor = view.Editor;
        editor.CompletionProvider = (text, caret, ct) => ProposeCompletionAsync(TargetOf(StateOf(view)).Session, text, caret, ct);
        editor.Editor.WordWrap = _context.Settings.Query.WordWrap;
        ApplyEditorOptions(editor);
        editor.DropText = TreeDropText;
        CommandScope.SetContext(editor, CommandContexts.QueryEditor);
        view.Helpers = CreateHelpersView(view);
        view.EditorHeightChanged += (_, h) => panels.QueryEditorHeight = (int)h;
        view.HelpersWidthChanged += (_, w) => panels.QueryHelpersWidth = (int)w;
        view.Results.SelectionChanged += (_, e) =>
        {
            // The event bubbles up to the main tabset, which has nothing to do with it.
            e.Handled = true;
            if (view.ActiveGrid is { } grid)
            {
                _context.Status.Position = grid.Selection.StatusText;
            }
            SyncLocalFilterBox();
        };
        var state = new QueryTabState();
        _queryStates[view] = state;
        editor.Editor.Document.Changed += (_, e) =>
        {
            _context.TabJournal?.Append(state.Id, new TextEdit(e.Offset, e.RemovalLength, e.InsertedText.Text));
            MarkModified(view);
        };
        var (target, name, color, _) = TargetOf(state);
        view.SetTarget(TargetLabel(target, name), color, pinned: false);
        view.Editor.SessionColor = color;
        RefreshHelpers(view);
        return view;
    }

    /// <summary>Preferences › SQL on the editor: font (points → pixels at 96 DPI), tab width, tabs to spaces, completion, uppercase.</summary>
    private void ApplyEditorOptions(Eunomia.UI.Controls.SqlEditor.SqlEditorControl editor)
    {
        var q = _context.Settings.Query;
        editor.Editor.FontFamily = UiFonts.MonospaceWith(q.EditorFont);
        editor.Editor.FontSize = Math.Clamp(q.EditorFontSize, 6, 40) * 96.0 / 72.0;
        editor.Editor.Options.IndentationSize = Math.Clamp(q.TabWidth, 1, 16);
        editor.Editor.Options.ConvertTabsToSpaces = q.TabsToSpaces;
        editor.CompletionEnabled = q.CompletionEnabled;
        editor.CompletionDelay = TimeSpan.FromMilliseconds(Math.Clamp(q.CompletionDelayMs, 0, 5000));
        editor.AutoUppercase = q.AutoUppercase;
        editor.CodeFolding = q.CodeFolding;
    }

    /// <summary>
    /// After Preferences: the editor options on every open query tab, the grid ones on every grid and the
    /// list bars.
    /// </summary>
    internal void ApplyEditorOptionsToTabs()
    {
        foreach (var tab in _tabs.QueryTabs.Where(t => t.HasContent))
        {
            if (tab.Content is QueryTabView view)
            {
                ApplyEditorOptions(view.Editor);
                foreach (var grid in view.ResultGrids)
                {
                    ApplyGridOptions(grid.Control);
                }
            }
        }
        ApplyGridOptions(DataGrid);
        ApplyListOptions();
    }

    /// <summary>Preferences › Grid formatting: font and maximum column width.</summary>
    private void ApplyGridOptions(Controls.Grid.DataGridControl grid)
    {
        var data = _context.Settings.Data;
        grid.FontFamilyName = data.FontName;
        grid.FontSize = Math.Clamp(data.FontSize, 6, 30) * 96.0 / 72.0;
        grid.MaxAutoColumnWidth = Math.Clamp(data.MaxColumnWidth, 20, 2000);
    }

    /// <summary>Preferences › General › Display bars: Host and Database tab lists.</summary>
    private void ApplyListOptions()
    {
        var bars = _context.Settings.Display.Bars;
        if (_hostView is { } host)
        {
            foreach (var list in new[] { host.DatabaseList, host.VariableList, host.StatusList, host.ProcessList, host.CommandList })
            {
                list.ShowBars = bars;
            }
        }
        if (_databaseView is { } database)
        {
            database.List.ShowBars = bars;
        }
    }

    /// <summary>Session the tab runs on (D9): the pinned one, if any (connected or not), otherwise the tree's.</summary>
    private (OpenSession? Session, string? Name, uint Color, bool Pinned) TargetOf(QueryTabState state)
    {
        if (state.PinnedSession is { } id)
        {
            var open = Sessions?.Connector.Find(id);
            var definition = open?.Definition ?? Sessions?.Catalog.Find(id);
            return (open, definition?.Path ?? state.PinnedName, ArgbOf(definition?.Color), true);
        }
        var active = ActiveSession;
        return (active, active?.Name, ArgbOf(active?.Color), false);
    }

    private static uint ArgbOf(uint? rgb) => rgb is { } value ? 0xFF000000 | value : 0;

    /// <summary>Name of the target session in the query tab bar, with "(read-only)" for read-only sessions.</summary>
    private static string? TargetLabel(OpenSession? session, string? name) =>
        name is null ? null : session?.Definition.ReadOnly == true ? name + " (read-only)" : name;

    /// <summary>Target session indicator on every query tab: header, editor bar, gutter and results.</summary>
    private void UpdateQueryTargets()
    {
        var active = ActiveSession;
        foreach (var tab in _tabs.QueryTabs)
        {
            var view = tab.HasContent ? tab.Content as QueryTabView : null;
            var (target, name, color, pinned) = view is null ? (active, active?.Name, ArgbOf(active?.Color), false) : TargetOf(StateOf(view));
            tab.SetSession(name, color, pinned);
            if (view is not null)
            {
                view.SetTarget(TargetLabel(target, name), color, pinned);
                view.Editor.SessionColor = color;
            }
        }
    }

    /// <summary>Query tab of the "Pin tab to session" command (the one from the tab bar menu, or the active one).</summary>
    private (QueryTabView View, QueryTabState State)? PinTarget() =>
        _tabs.TargetQueryTab is { } tab && QueryViewOf(tab) is var view ? (view, StateOf(view)) : null;

    private void TogglePin()
    {
        if (PinTarget() is not { State: var state })
        {
            return;
        }
        if (state.PinnedSession is not null)
        {
            state.PinnedSession = null;
            state.PinnedName = null;
        }
        else if (ActiveSession is { } session)
        {
            state.PinnedSession = session.Id;
            state.PinnedName = session.Name;
        }
        UpdateQueryTargets();
    }

    /// <summary>
    /// Autocomplete A on the active session, with the table selected in the tree; the UI state is read here and the analysis
    /// runs in the background.
    /// </summary>
    private Task<CompletionProposal?> ProposeCompletionAsync(OpenSession? target, string text, int caret, CancellationToken cancellationToken)
    {
        var session = target is { Host.State: SessionState.Ready } ready ? ready : null;
        var selected = session is not null && _tree.Tree.FocusedNode?.Tag is TreeObjectTag { Object: { } obj } tag && tag.Session == session.Id ? obj : null;
        var delimiter = _context.Settings.Query.Delimiter;
        var completion = new QueryCompletion(session);
        return Task.Run(() => completion.ProposeAsync(text, caret, delimiter, selected, cancellationToken), cancellationToken);
    }

    /// <summary>Data tab filter: the text is seen as <c>SELECT * FROM tbl WHERE …</c> (appendix C §4.2).</summary>
    private async Task<CompletionProposal?> ProposeFilterCompletionAsync(string text, int caret, CancellationToken cancellationToken)
    {
        if (_browser is not { } browser)
        {
            return await ProposeCompletionAsync(ActiveSession, text, caret, cancellationToken);
        }
        var language = ActiveSession?.Language ?? SqlLanguages.Default;
        var prefix = "SELECT * FROM " + language.EditorQualifiedName(browser.Table.Ref.Catalog, browser.Table.Ref.Name) + " WHERE ";
        var proposal = await ProposeCompletionAsync(ActiveSession, prefix + text, caret + prefix.Length, cancellationToken);
        return proposal is null ? null : proposal with { ReplaceStart = proposal.ReplaceStart - prefix.Length };
    }

    private static QueryTabView QueryViewOf(QueryTab tab) => (QueryTabView)tab.Content;

    /// <summary>Active query tab, if it already has content.</summary>
    private QueryTabView? ActiveQueryView => _tabs.ActiveQueryTab is { } tab ? QueryViewOf(tab) : null;

    private QueryTabState StateOf(QueryTabView view)
    {
        if (!_queryStates.TryGetValue(view, out var state))
        {
            state = new QueryTabState();
            _queryStates[view] = state;
        }
        return state;
    }

    /// <summary>Selected result tab of the active query tab.</summary>
    private ResultView? ActiveResultView =>
        ActiveQueryView is { } view && _queryStates.TryGetValue(view, out var state) && view.Results.SelectedIndex is var i && i >= 0 && i < state.Results.Count
            ? state.Results[i]
            : null;

    /// <summary>
    /// Run: the target session ready (or busy only with the Data tab load, which yields the connection to the query in
    /// <see cref="RunQueryAsync"/>), or pinned and disconnected (Run asks to connect).
    /// </summary>
    private bool CanRunQuery() =>
        ActiveQueryView is { } view && StateOf(view) is { Run: null } state && view.Editor.Text.Trim().Length > 0
        && TargetOf(state) is var target
        && (target.Session is { Host.State: SessionState.Ready }
            || (target.Session is { Host.State: SessionState.Busy } busy && IsLoadingData(busy) && !QueryRunningOn(busy))
            || (target.Pinned && target.Session is null && Sessions is not null));

    private bool CanCancelOperation() =>
        ActiveQueryView is { } view
            ? StateOf(view).Run is not null || ActiveResultView?.Sort is not null
            : ActiveSession is { Host.IsPrimaryBusy: true } session && !QueryRunningOn(session);

    /// <summary>
    /// Esc / Cancel (D2): in a query tab, its run or the result tab's sort; in the other tabs, the work on the active session's
    /// primary connection (Data tab), but never a query tab's run.
    /// </summary>
    private void CancelOperation()
    {
        if (ActiveQueryView is { } view)
        {
            if (StateOf(view).Run is { } run)
            {
                run.Cancel();
            }
            else
            {
                ActiveResultView?.Sort?.Cancel();
            }
            return;
        }
        if (ActiveSession is { } session && !QueryRunningOn(session))
        {
            session.Host.CancelPrimary();
        }
    }

    private bool QueryRunningOn(OpenSession session) =>
        _queryStates.Values.Any(s => s.Run is not null && ReferenceEquals(s.Session, session));

    private void ClearQueryResults(QueryTabView view, QueryTabState state)
    {
        view.ClearResults();
        foreach (var result in state.Results)
        {
            result.Sort?.Cancel();
            _localFilterTexts.Remove(result);
            result.Result.Source.Dispose();
        }
        state.Results.Clear();
    }

    private QueryRunOptions ReadRunOptions()
    {
        var query = _context.Settings.Query;
        var data = _context.Settings.Data;
        return new QueryRunOptions
        {
            BatchInOneGo = query.BatchInOneGo,
            StopOnErrors = query.StopOnErrors,
            MaxResultSets = Math.Max(1, query.MaxResults),
            Storage = new ResultStorage(
                (long)Math.Max(16, data.ResultMemoryMb) * 1024 * 1024,
                _context.SpillDirectory,
                (long)Math.Max(0, data.SpillQuotaMb) * 1024 * 1024)
            {
                Pool = GlobalResultPool(),
            },
        };
    }

    private async Task RunQueryAsync(RunScope scope)
    {
        var view = ActiveQueryView!;
        if (await QuerySessionAsync(StateOf(view)) is not { } session)
        {
            return;
        }
        var editor = view.Editor.Editor;
        var selected = scope == RunScope.Selection && editor.SelectionLength > 0;
        var text = selected ? editor.SelectedText : editor.Text;
        var lineOffset = selected ? editor.Document.GetLineByOffset(editor.SelectionStart).LineNumber - 1 : 0;
        var statements = SqlSplitter.Split(text, _context.Settings.Query.Delimiter);
        if (scope is RunScope.Current or RunScope.Explain)
        {
            statements = SqlSplitter.StatementAt(statements, editor.CaretOffset) is { } current ? [current] : [];
        }
        if (scope == RunScope.Explain && statements.Count == 1)
        {
            statements = [statements[0] with { Text = "EXPLAIN " + statements[0].Text }];
        }
        if (statements.Count == 0)
        {
            return;
        }
        if (session.Definition.ConfirmChanges && !session.Definition.ReadOnly
            && statements.Where(s => !Engine.Execution.ReadOnlyPolicy.IsAllowed(s.Text)).ToList() is { Count: > 0 } changes)
        {
            var preview = changes[0].Text.Length > 300 ? changes[0].Text[..300] + " ..." : changes[0].Text;
            var question = string.Create(CultureInfo.InvariantCulture, $"Run {changes.Count} statement(s) that change data or structure?\n\n{preview}") + SessionLine(session);
            if (!(await ConfirmAsync("Confirm changes", question)).Ok)
            {
                return;
            }
        }
        if (scope != RunScope.Explain && _context.Settings.Query.WarnUnsafeUpdates && statements.Any(s => QueryText.IsUnsafe(s.Text)))
        {
            var (ok, verified) = await ConfirmAsync("Confirm", "Run unsafe queries without a WHERE clause?", "Do not ask again");
            if (!ok)
            {
                return;
            }
            if (verified)
            {
                _context.Settings.Query.WarnUnsafeUpdates = false;
            }
        }

        // The Data tab load on the same session is cancelled: the query does not wait for it on the primary connection.
        if (IsLoadingData(session))
        {
            AbandonDataLoad();
        }
        var state = StateOf(view);
        ClearQueryResults(view, state);
        view.Editor.ErrorLine = null;
        using var cts = new CancellationTokenSource();
        state.Run = cts;
        state.Session = session;
        var color = session.Color is { } rgb ? 0xFF000000 | rgb : 0;
        var runner = new QueryRunner(session.Host);
        runner.Progress += (_, p) => Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(state.Run, cts))
            {
                _context.Status.Busy = string.Create(CultureInfo.InvariantCulture, $"Executing query #{p.Number} of {p.Total} ...");
                _context.Status.Progress = p.Total > 1 ? (double)(p.Number - 1) / p.Total : null;
            }
        });
        runner.ResultStarted += (_, r) => Dispatcher.UIThread.Post(() => AddQueryResult(view, state, r, color));
        runner.ResultCompleted += (_, r) => Dispatcher.UIThread.Post(() =>
        {
            if (state.Results.FirstOrDefault(v => ReferenceEquals(v.Result, r)) is { } done)
            {
                view.SetResultCaption(r.Index, r.Caption, r.Sql);
                _ = EnableResultEditingAsync(done, session);
            }
        });
        runner.Message += (_, m) => _context.Log.Append(LogCategory.Info, m);
        var batchText = scope is RunScope.Current or RunScope.Explain ? statements[0].Text : text;
        var started = DateTimeOffset.Now;
        _context.Status.Busy = string.Create(CultureInfo.InvariantCulture, $"Executing query #1 of {statements.Count} ...");
        QueryRunSummary? summary = null;
        try
        {
            try
            {
                summary = await runner.RunAsync(batchText, statements, ReadRunOptions(), cts.Token);
            }
            catch (OperationCanceledException)
            {
                _context.Log.Append(LogCategory.Info, "Query execution was cancelled.");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ShowError(ex);
            }
            // The grids and captions arrive through Post: flushes them before the summary.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            // Results cut by an error or a cancel keep the rows that arrived.
            foreach (var result in state.Results.Where(r => !r.Result.Source.IsComplete))
            {
                result.Result.Source.Complete();
                view.SetResultCaption(result.Result.Index, result.Result.Caption, result.Result.Sql);
            }
            if (summary is not null)
            {
                _context.Log.Append(LogCategory.Info, summary.LogLine);
                if (summary.Errors.Count > 0)
                {
                    ShowQueryErrors(view, summary.Errors, lineOffset);
                }
                else
                {
                    AddHistory(session, batchText, statements.Count, started, summary.Duration);
                    RefreshHelpers(view);
                }
            }
        }
        finally
        {
            if (ReferenceEquals(state.Run, cts))
            {
                state.Run = null;
            }
            _context.Status.SetIdle();
            session.Definition.LastUsedDatabase = session.Host.CurrentDatabase ?? session.Definition.LastUsedDatabase;
        }
    }

    /// <summary>
    /// Session to run on (D9): with the tab pinned and the session disconnected, asks whether to connect and never runs on another session.
    /// </summary>
    private async Task<OpenSession?> QuerySessionAsync(QueryTabState state)
    {
        var target = TargetOf(state);
        if (target.Session is not null || !target.Pinned)
        {
            return target.Session;
        }
        if (Sessions is not { } sessions || sessions.Catalog.Find(state.PinnedSession!.Value) is not { } definition)
        {
            ShowError(new InvalidOperationException($"Session \"{target.Name}\" not found. Unpin the tab to run it on another session."));
            return null;
        }
        var (ok, _) = await ConfirmAsync("Confirm", $"This query tab is pinned to session \"{definition.Path}\", which is not connected. Connect now?");
        // The run carries on in the query tab: connecting does not switch to the Host tab.
        if (!ok || !await ConnectAsync(this, definition, null, showHostTab: false))
        {
            return null;
        }
        return sessions.Connector.Find(definition.Id) is { Host.State: SessionState.Ready } session ? session : null;
    }

    private void AddQueryResult(QueryTabView view, QueryTabState state, QueryResult result, uint color)
    {
        if (!_queryStates.ContainsKey(view))
        {
            result.Source.Dispose();
            return;
        }
        var grid = view.AddResult(result.Caption, result.Source, result.Sql, color, g =>
        {
            CommandScope.SetContext(g, CommandContexts.Grid);
            ApplyGridOptions(g.Control);
            g.Control.IsReadOnly = true;
            WatchForPreview(g.Control);
            g.Control.StatusChanged += (_, _) =>
            {
                if (ReferenceEquals(view.ActiveGrid, g.Control))
                {
                    _context.Status.Position = g.Control.Selection.StatusText;
                }
            };
        });
        var entry = new ResultView(result, grid);
        grid.Control.HeaderClicked += (_, e) => _ = SortResultAsync(entry, e.Column);
        state.Results.Add(entry);
    }

    /// <summary>Dialog for the first error, line in pink and the cursor on it; with Stop on errors off, all of them go to the log.</summary>
    private void ShowQueryErrors(QueryTabView view, IReadOnlyList<QueryRunError> errors, int lineOffset)
    {
        foreach (var error in errors)
        {
            _context.Log.Append(LogCategory.Error, ErrorText(error));
        }
        var first = errors[0];
        view.Editor.ErrorLine = first.Line + lineOffset;
        view.Editor.Editor.TextArea.Focus();
        if (QueryErrorOverride is { } report)
        {
            report(ErrorText(first));
        }
        else
        {
            _ = MessageDialog.ShowAsync(this, "Error", ErrorText(first), MessageDialogButtons.Ok);
        }
    }

    private static string ErrorText(QueryRunError error) =>
        string.Create(CultureInfo.InvariantCulture, $"SQL Error ({error.Error.Code}): {error.Error.Message}");

    /// <summary>Replaces the query tabs' error dialog (tests).</summary>
    internal Action<string>? QueryErrorOverride { get; set; }

    /// <summary>
    /// Per-session history (appendix C §4.5): only batches without errors, up to 5 statements and 1 MB. Batches with passwords
    /// (<c>IDENTIFIED BY</c>, <c>SET PASSWORD</c>…) are not kept: passwords only live in the system vault (23.2).
    /// </summary>
    private void AddHistory(OpenSession session, string sql, int statements, DateTimeOffset executedAt, TimeSpan duration)
    {
        if (!_context.Settings.Query.HistoryEnabled || _context.AppDatabase is not { } db || statements > 5 || sql.Length > 1024 * 1024
            || Engine.Sql.SecretMasking.ContainsSecret(sql))
        {
            return;
        }
        try
        {
            db.AddHistory(session.Name, session.Host.CurrentDatabase, sql.Trim(), executedAt, duration);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _context.Log.Append(LogCategory.Error, ex.Message);
        }
    }

    /// <summary>
    /// Click on a result tab's header: sorts locally by that column, ascending and then alternating (appendix D
    /// B18). Runs in the background and Esc cancels; the local filter is applied again to the sorted view.
    /// </summary>
    private async Task SortResultAsync(ResultView entry, int column)
    {
        if (column < 0 || !entry.Result.Source.IsComplete)
        {
            return;
        }
        var descending = entry.SortColumn == column && !entry.Descending;
        entry.Sort?.Cancel();
        using var cts = new CancellationTokenSource();
        entry.Sort = cts;
        _context.Status.Busy = "Sorting rows ...";
        try
        {
            var sorted = await FilteredRowSource.SortAsync(entry.Result.Source, column, descending, cts.Token);
            if (!ReferenceEquals(entry.Sort, cts))
            {
                return;
            }
            entry.SortColumn = column;
            entry.Descending = descending;
            entry.View = sorted;
            entry.Grid.Control.SortIndicators = new Dictionary<int, GridSortIndicator> { [column] = new(descending, 1) };
            entry.Grid.Control.SetSource(sorted);
            if (_localFilterTexts.ContainsKey(entry) && ReferenceEquals(LocalFilterTarget(), entry))
            {
                await ApplyLocalFilterAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(entry.Sort, cts))
            {
                entry.Sort = null;
            }
            _context.Status.SetIdle();
        }
    }

    private async Task SetDelimiterAsync()
    {
        var value = await InputDialog.ShowAsync(this, "Set delimiter", "SQL statement delimiter (default: ;)", _context.Settings.Query.Delimiter);
        if (value is null)
        {
            return;
        }
        value = value.Trim();
        if (!SqlSplitter.IsValidDelimiter(value))
        {
            _ = MessageDialog.ShowAsync(this, "Error", $"Invalid delimiter: {value}", MessageDialogButtons.Ok);
            return;
        }
        _context.Settings.Query.Delimiter = value;
        _context.Log.Append(LogCategory.Info, $"Delimiter changed to {value}");
    }
}
