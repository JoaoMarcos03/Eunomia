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
/// Execução nas query tabs (secção 4.7; anexo C §4.4; anexo D B18; FAM-Q01–Q04, Q11): Run, Run selection e Run current
/// query com o <see cref="QueryRunner"/>, um result tab por result set, aviso de UPDATE/DELETE sem WHERE, erro com a
/// linha a rosa e o cursor nela, resumo e warnings no SQL log, histórico. O cancel atua sobre a execução da própria tab
/// (D2). Os result tabs ordenam localmente por uma coluna (clique no header), em background e canceláveis (P20).
/// </summary>
public sealed partial class MainWindow
{
    private readonly Dictionary<QueryTabView, QueryTabState> _queryStates = [];

    private enum RunScope
    {
        All,
        Selection,
        Current,

        /// <summary>Query › Explain current query: <c>EXPLAIN</c> da instrução onde está o cursor.</summary>
        Explain,
    }

    /// <summary>Estado de uma query tab: execução em curso e resultados da última.</summary>
    private sealed class QueryTabState
    {
        public CancellationTokenSource? Run { get; set; }

        /// <summary>Sessão da execução em curso.</summary>
        public OpenSession? Session { get; set; }

        /// <summary>"Pin tab to session" (D9): a tab corre sempre nesta sessão, ligada ou não.</summary>
        public Guid? PinnedSession { get; set; }

        /// <summary>Nome da sessão fixada (para o indicador, se a sessão deixar de existir).</summary>
        public string? PinnedName { get; set; }

        /// <summary>Identidade no journal (secção 21.4).</summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>Ficheiro da tab (Load / Save) e a codificação.</summary>
        public string? FilePath { get; set; }

        public string? FileEncoding { get; set; }

        /// <summary>Tabela das colunas mostradas no painel de helpers (e se as colunas já lá estavam).</summary>
        public (DbObjectRef? Table, bool WithColumns)? HelpersShown { get; set; }

        /// <summary>Último estado gravado no journal.</summary>
        public TabState? Saved { get; set; }

        /// <summary>Data e tamanho do ficheiro depois do último Load ou Save (as nossas gravações não perguntam "Reload").</summary>
        public (DateTime, long)? FileStamp { get; set; }

        public List<ResultView> Results { get; } = [];
    }

    /// <summary>Um result tab: o resultado, a ordenação local e a vista mostrada (antes do filtro local).</summary>
    private sealed class ResultView(QueryResult result, DataGridView grid)
    {
        public QueryResult Result { get; } = result;

        public DataGridView Grid { get; } = grid;

        public int SortColumn { get; set; } = -1;

        public bool Descending { get; set; }

        public IRowSource View { get; set; } = result.Source;

        public CancellationTokenSource? Sort { get; set; }

        /// <summary>Tabela de origem quando o resultado é editável (anexo D B18).</summary>
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

    /// <summary>Conteúdo de uma query tab nova: editor, helpers e o tabset de resultados.</summary>
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
            // O evento sobe até ao tabset principal, que não tem nada a ver com ele.
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

    /// <summary>Preferences › SQL no editor: fonte (pontos → pixels a 96 DPI), tab width, tabs to spaces, completion, uppercase.</summary>
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
    /// Depois do Preferences: as opções do editor em todas as query tabs abertas, as do grid em todos os grids e as
    /// barras das listas.
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

    /// <summary>Preferences › Grid formatting: fonte e largura máxima das colunas.</summary>
    private void ApplyGridOptions(Controls.Grid.DataGridControl grid)
    {
        var data = _context.Settings.Data;
        grid.FontFamilyName = data.FontName;
        grid.FontSize = Math.Clamp(data.FontSize, 6, 30) * 96.0 / 72.0;
        grid.MaxAutoColumnWidth = Math.Clamp(data.MaxColumnWidth, 20, 2000);
    }

    /// <summary>Preferences › General › Display bars: listas do Host e do Database tab.</summary>
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

    /// <summary>Sessão onde a tab corre (D9): a fixada, se houver (ligada ou não), senão a da árvore.</summary>
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

    /// <summary>Nome da sessão alvo na barra da query tab, com "(read-only)" nas sessões só de leitura.</summary>
    private static string? TargetLabel(OpenSession? session, string? name) =>
        name is null ? null : session?.Definition.ReadOnly == true ? name + " (read-only)" : name;

    /// <summary>Indicador da sessão alvo em todas as query tabs: cabeçalho, barra do editor, gutter e resultados.</summary>
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

    /// <summary>Query tab do comando "Pin tab to session" (a do menu da barra de tabs, ou a ativa).</summary>
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
    /// Autocomplete A na sessão ativa, com a tabela selecionada na árvore; o estado da UI é lido aqui e a análise
    /// corre em background.
    /// </summary>
    private Task<CompletionProposal?> ProposeCompletionAsync(OpenSession? target, string text, int caret, CancellationToken cancellationToken)
    {
        var session = target is { Host.State: SessionState.Ready } ready ? ready : null;
        var selected = session is not null && _tree.Tree.FocusedNode?.Tag is TreeObjectTag { Object: { } obj } tag && tag.Session == session.Id ? obj : null;
        var delimiter = _context.Settings.Query.Delimiter;
        var completion = new QueryCompletion(session);
        return Task.Run(() => completion.ProposeAsync(text, caret, delimiter, selected, cancellationToken), cancellationToken);
    }

    /// <summary>Filtro do Data tab: o texto é visto como <c>SELECT * FROM tbl WHERE …</c> (anexo C §4.2).</summary>
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

    /// <summary>Query tab ativa, se já tiver conteúdo.</summary>
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

    /// <summary>Result tab selecionado da query tab ativa.</summary>
    private ResultView? ActiveResultView =>
        ActiveQueryView is { } view && _queryStates.TryGetValue(view, out var state) && view.Results.SelectedIndex is var i && i >= 0 && i < state.Results.Count
            ? state.Results[i]
            : null;

    /// <summary>
    /// Run: a sessão alvo pronta (ou ocupada só pela carga do Data tab, que cede a ligação à query em
    /// <see cref="RunQueryAsync"/>), ou fixada e desligada (o Run pede para ligar).
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
    /// Esc / Cancel (D2): numa query tab, a sua execução ou a ordenação do result tab; nas outras tabs, o trabalho da
    /// ligação primária da sessão ativa (Data tab), mas nunca a execução de uma query tab.
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

        // A carga do Data tab na mesma sessão é cancelada: a query não fica à espera dela na ligação primária.
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
            // Os grids e as captions chegam por Post: esvazia-os antes do resumo.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            // Resultados cortados por erro ou cancel ficam com as linhas que chegaram.
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
    /// Sessão onde correr (D9): com a tab fixada e a sessão desligada, pergunta se liga e nunca corre noutra sessão.
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
        // A execução continua na query tab: ligar não muda para o Host tab.
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

    /// <summary>Diálogo do primeiro erro, linha a rosa e cursor nela; com Stop on errors desligado, todos vão para o log.</summary>
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

    /// <summary>Substitui o diálogo de erro das query tabs (testes).</summary>
    internal Action<string>? QueryErrorOverride { get; set; }

    /// <summary>
    /// Histórico por sessão (anexo C §4.5): só batches sem erros, até 5 instruções e 1 MB. Batches com passwords
    /// (<c>IDENTIFIED BY</c>, <c>SET PASSWORD</c>…) não ficam: as passwords só vivem no cofre do sistema (23.2).
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
    /// Clique no header de um result tab: ordena localmente por essa coluna, ascendente e depois alternando (anexo D
    /// B18). Corre em background e o Esc cancela; o filtro local é reaplicado à vista ordenada.
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
