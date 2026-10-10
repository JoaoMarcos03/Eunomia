using System.Globalization;
using Eunomia.App.Data;
using Eunomia.App.Sessions;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Execution.Editing;
using Eunomia.Engine.Results;
using Eunomia.Engine.Sql;
using Eunomia.Presentation.Logging;
using Eunomia.UI.Controls.Common.Dialogs;
using Eunomia.UI.Controls.Grid;

namespace Eunomia.UI.Shell;

/// <summary>
/// Edição no Data tab (anexo D B15–B17; FAM-G08, G09; D3): a linha alterada grava-se ao mudar de linha, com Ctrl+Enter
/// ou ao clicar fora (UPDATE/INSERT pela chave, verificação das linhas afetadas e releitura da linha); um erro mantém a
/// linha alterada. Insert row, Duplicate row (sem e com chaves), Delete selected row(s) com "Delete N row(s)?", NULL
/// (Ctrl+Shift+N). Sem chave suficiente no resultado, o grid fica só de leitura e diz porquê.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Mensagem do HeidiSQL (<c>MSG_NOGRIDEDITING</c>).</summary>
    public const string NoKeyMessage = "Selected columns don't contain a sufficient set of key columns to allow editing. Please select primary or unique key columns, or all columns.";

    /// <summary>Sessão read-only (secção 23.2): o grid não edita e diz porquê.</summary>
    private static string ReadOnlyMessage(OpenSession session) =>
        $"Session \"{session.Name}\" is read-only. Turn off \"Read-only session\" in the Session Manager (Advanced) to edit data.";

    private OpenSession? _browserSession;
    private readonly Dictionary<Guid, TimeSpan> _serverClockOffsets = [];
    private IReadOnlyDictionary<string, string> _insertValues = new Dictionary<string, string>();

    /// <summary>Substitui o diálogo de erro da edição (testes).</summary>
    internal Action<string>? EditErrorOverride { get; set; }

    /// <summary>Substitui o editor em janela (testes): o pedido → o valor novo ou <c>null</c>.</summary>
    internal Func<GridPopupEditEventArgs, string?>? PopupEditorOverride { get; set; }

    private void InitEditing()
    {
        DataGrid.CommitHandler = CommitDataRowAsync;
        DataGrid.EditRefused += (_, _) => ShowEditError(_browserSession is { Definition.ReadOnly: true } ro ? ReadOnlyMessage(ro) : NoKeyMessage);
        DataGrid.EditorProvider = DataEditorAsync;
        DataGrid.PopupEditRequested += (_, e) => _ = PopupEditAsync(DataGrid, e);
        var c = _context.Commands;
        c.SetHandler("grid.insertRow", _ => new ValueTask(InsertDataRowAsync(duplicate: false, withKeys: false)), CanEditData);
        c.SetHandler("grid.duplicateRow", _ => new ValueTask(InsertDataRowAsync(duplicate: true, withKeys: false)), () => CanEditData() && DataGrid.Selection.FocusedRow >= 0);
        c.SetHandler("grid.duplicateRowWithKeys", _ => new ValueTask(InsertDataRowAsync(duplicate: true, withKeys: true)), () => CanEditData() && DataGrid.Selection.FocusedRow >= 0);
        c.SetHandler("grid.deleteRows", _ => new ValueTask(DeleteDataRowsAsync()), () => CanEditData() && DataGrid.Selection.FocusedRow >= 0);
        c.SetHandler("grid.post", _ => new ValueTask(PostFocusedGridAsync()), () => FocusedGrid() is { } g && (g.IsEditing || g.Edits.HasPending));
        c.SetHandler("grid.cancelEditing", () => FocusedGrid()!.CancelEditing(), () => FocusedGrid() is { } g && (g.IsEditing || g.Edits.HasPending));
        c.SetHandler("grid.setNull", _ => new ValueTask(SetFocusedCellAsync(CellValue.Null)), () => FocusedGrid() is { IsReadOnly: false, Selection.FocusedRow: >= 0 });
        c.SetHandler("grid.sqlFunction", _ => new ValueTask(FocusedGrid()!.BeginEditAsync(FunctionEditor())), () => FocusedGrid() is { IsReadOnly: false, Selection.FocusedRow: >= 0 });
        c.SetHandler("grid.editWithoutLookup", _ => new ValueTask(FocusedGrid()!.BeginEditAsync(new TextCellEditor())), () => FocusedGrid() is { IsReadOnly: false, Selection.FocusedRow: >= 0 });

        // Insert value ▸: a caption mostra o valor; os valores refazem-se quando o menu do grid abre.
        RefreshInsertValues();
        if (DataGrid.ContextMenu is { } menu)
        {
            menu.Opening += (_, _) => RefreshInsertValues();
        }
        foreach (var (id, caption) in App.Commands.HeidiSqlCatalog.InsertValues)
        {
            if (id == "insertValue.default")
            {
                c.SetCaption(id, () => FocusedDefault() is { } d ? "Default value: " + d : null);
                c.SetHandler(id, _ => new ValueTask(SetFocusedCellAsync(new CellValue(FocusedDefault()))), () => CanEditData() && FocusedDefault() is not null);
                continue;
            }
            c.SetCaption(id, () => _insertValues.TryGetValue(id, out var v) ? caption + ": " + v : null);
            c.SetHandler(id, _ => new ValueTask(SetFocusedCellAsync(new CellValue(_insertValues.GetValueOrDefault(id)))), () => FocusedGrid() is { IsReadOnly: false, Selection.FocusedRow: >= 0 });
        }
    }

    /// <summary>Valores do Insert value ▸ com o relógio do servidor da sessão do Data tab (desvio medido pela status bar).</summary>
    internal void RefreshInsertValues()
    {
        var offset = _browserSession is { } s && _serverClockOffsets.TryGetValue(s.Id, out var o) ? o : TimeSpan.Zero;
        _insertValues = App.Data.InsertValueItems.Compute(DateTime.Now + offset, DateTime.Now, DateTime.UtcNow, Guid.NewGuid());
    }

    /// <summary>Default literal da coluna focada no Data tab ("Default value: x").</summary>
    private string? FocusedDefault()
    {
        if (_browser is not { } browser || DataGrid.Source is not { } source || DataGrid.Selection.FocusedColumn is var c && (c < 0 || c >= source.Schema.Count))
        {
            return null;
        }
        var name = source.Schema[DataGrid.Selection.FocusedColumn].Name;
        return browser.Table.Columns.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)) is { } column
            ? App.Data.InsertValueItems.DefaultLiteral(column)
            : null;
    }

    /// <summary>Insert value ▸ SQL function (Ctrl+F2): lista das funções com texto livre; o valor vai como expressão.</summary>
    private ChoiceCellEditor FunctionEditor() =>
        new([.. (ActiveSession?.Language ?? SqlLanguages.Default).Functions.Order(StringComparer.OrdinalIgnoreCase).Select(f => (f + "()", f + "()"))], allowText: true, isFunction: true);

    /// <summary>
    /// Editor por coluna no Data tab (anexo D B15): lista da chave estrangeira, ENUM e booleanos em lista, SET em
    /// caixas, datas e horas com setas, binário em hex na janela; o resto em texto.
    /// </summary>
    private async Task<GridCellEditor?> DataEditorAsync(long row, int column, string? value)
    {
        if (_browser is not { } browser || _browserSession is not { } session || DataGrid.Source is not { } source || column >= source.Schema.Count)
        {
            return null;
        }
        var name = source.Schema[column].Name;
        if (browser.Table.Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } definition)
        {
            return null;
        }
        if (_context.Settings.Data.ForeignDropDown && browser.Table.ForeignKeys.Any(f => f.Columns.Contains(name, StringComparer.OrdinalIgnoreCase)))
        {
            try
            {
                if (await App.Data.ForeignKeyLookup.LoadAsync(session, browser.Table, name, CancellationToken.None) is { } items)
                {
                    return new ChoiceCellEditor(items);
                }
                _context.Log.Append(LogCategory.Info, $"Foreign key lookup for \"{name}\" has {App.Data.ForeignKeyLookup.MaxRows:N0} or more rows; using the normal editor.");
            }
            catch (Exception ex) when (ex is DbOperationException or Engine.Execution.SessionOperationException)
            {
                _context.Log.Append(LogCategory.Error, ex.Message);
            }
        }
        return definition.Type.Kind switch
        {
            LogicalTypeKind.Enum when definition.Type.Values is { Count: > 0 } values => new ChoiceCellEditor([.. values.Select(v => (v, v))]),
            LogicalTypeKind.Boolean => new ChoiceCellEditor([("0", "0"), ("1", "1")]),
            LogicalTypeKind.Set when definition.Type.Values is { Count: > 0 } values => new SetCellEditor(values),
            LogicalTypeKind.Date or LogicalTypeKind.Time or LogicalTypeKind.DateTime => new DateTimeCellEditor(),
            LogicalTypeKind.Binary or LogicalTypeKind.Spatial when !_context.Settings.Data.BlobAsText => new PopupCellEditor(binary: true),
            _ => null,
        };
    }

    /// <summary>Editor em janela (texto longo ou com quebras de linha, "…", binário): o valor novo vai para a célula.</summary>
    private async Task PopupEditAsync(DataGridControl grid, GridPopupEditEventArgs request)
    {
        var name = grid.Source is { } source && request.Column < source.Schema.Count ? source.Schema[request.Column].Name : string.Empty;
        long? max = _browser?.Table.Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))?.Type.Length;
        var value = PopupEditorOverride is { } fake
            ? fake(request)
            : await Views.TextEditorWindow.ShowAsync(this, $"Edit {name}", request.Value, max, request.IsBinary);
        if (value is not null && value != request.Value)
        {
            await grid.SetCellValueAsync(request.Row, request.Column, new CellValue(value));
        }
    }

    private bool CanEditData() => _browser is not null && !DataGrid.IsReadOnly && DataGrid.Source is not null;

    private async Task PostFocusedGridAsync()
    {
        if (FocusedGrid() is { } grid)
        {
            await grid.PostAsync();
        }
    }

    private async Task SetFocusedCellAsync(CellValue value)
    {
        if (FocusedGrid() is { } grid)
        {
            await grid.SetCellValueAsync(grid.Selection.FocusedRow, grid.Selection.FocusedColumn, value);
        }
    }

    private void ShowEditError(string message)
    {
        _context.Log.Append(LogCategory.Error, message);
        if (EditErrorOverride is { } report)
        {
            report(message);
        }
        else
        {
            _ = MessageDialog.ShowAsync(this, "Grid editing error", message, MessageDialogButtons.Ok);
        }
    }

    /// <summary>O Data tab é editável quando o SELECT tem as colunas que identificam a linha (anexo D B15, <c>CheckEditable</c>).</summary>
    private static bool IsEditable(TableBrowser browser) =>
        RowKey.Of(browser.Table).Columns.All(k => browser.Select.Columns.Contains(k.Name, StringComparer.OrdinalIgnoreCase));

    /// <summary>Linha do browser por trás de uma linha do grid (o filtro local muda os índices); <c>null</c> numa linha nova.</summary>
    private long? SourceRowOf(long gridRow)
    {
        if (gridRow >= DataGrid.SourceRows)
        {
            return null;
        }
        return DataGrid.Source is ProjectedRowSource { Inner: FilteredRowSource filtered } ? filtered.InnerRow(gridRow) : gridRow;
    }

    private int GridColumnOf(string name) =>
        DataGrid.Source is { } source ? source.Schema.Columns.ToList().FindIndex(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) : -1;

    /// <summary>Valor atual de uma coluna numa linha: o gravado por cima do source, senão o do browser (completo).</summary>
    private async Task<CellLiteral> CurrentValueAsync(TableBrowser browser, long gridRow, string column)
    {
        var gridColumn = GridColumnOf(column);
        if (gridColumn >= 0 && DataGrid.Edits.TryGetCommitted(gridRow, gridColumn, out var committed))
        {
            return new CellLiteral(committed.Text, committed.IsFunction);
        }
        var index = browser.Select.Columns.ToList().FindIndex(c => string.Equals(c, column, StringComparison.OrdinalIgnoreCase));
        if (SourceRowOf(gridRow) is not { } sourceRow || index < 0)
        {
            return CellLiteral.Null;
        }
        return new CellLiteral(await browser.GetFullTextAsync(sourceRow, index));
    }

    /// <summary>Valores originais das colunas que identificam a linha (para o WHERE).</summary>
    private async Task<Dictionary<string, CellLiteral>> OriginalKeyAsync(TableBrowser browser, long gridRow)
    {
        var original = new Dictionary<string, CellLiteral>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in RowKey.Of(browser.Table).Columns)
        {
            original[column.Name] = await CurrentValueAsync(browser, gridRow, column.Name);
        }
        return original;
    }

    /// <summary>
    /// Grava; se a linha mudou no servidor desde que foi lida (<see cref="RowConflictException"/>), pergunta se sobrescreve.
    /// Devolve <c>null</c> se o utilizador não quiser (a linha fica por gravar no grid).
    /// </summary>
    private async Task<RowWriteResult?> WriteWithConflictCheckAsync(RowWriter writer, TableDefinition table, RowChange change)
    {
        try
        {
            return await writer.WriteAsync(table, change, CancellationToken.None);
        }
        catch (RowConflictException conflict)
        {
            var overwrite = (await ConfirmAsync("Row changed on the server",
                conflict.Message + "\n\nOK overwrites it with your values. Cancel keeps your edit unsaved: press Esc to discard it and F5 to see the current row.")).Ok;
            return overwrite ? await writer.WriteAsync(table, change with { Expected = null }, CancellationToken.None) : null;
        }
    }

    /// <summary>Grava a linha pendente do Data tab no servidor e devolve os valores relidos para o grid.</summary>
    private async Task<GridCommitResult> CommitDataRowAsync(GridRowCommit commit)
    {
        if (_browser is not { } browser || _browserSession is not { } session || DataGrid.Source is not { } gridSource)
        {
            return GridCommitResult.Failed;
        }
        var table = browser.Table;
        var names = gridSource.Schema.Columns.Select(c => c.Name).ToList();
        foreach (var column in commit.Values.Keys)
        {
            if (table.Columns.FirstOrDefault(c => string.Equals(c.Name, names[column], StringComparison.OrdinalIgnoreCase)) is { IsGenerated: true } generated)
            {
                ShowEditError($"Column \"{generated.Name}\" is defined as generated, so it cannot be edited.");
                return GridCommitResult.Failed;
            }
        }
        var changes = commit.Values.ToDictionary(v => names[v.Key], v => new CellLiteral(v.Value.Text, v.Value.IsFunction), StringComparer.OrdinalIgnoreCase);
        _context.Status.Busy = "Saving row ...";
        try
        {
            var original = commit.IsInsert ? new Dictionary<string, CellLiteral>() : await OriginalKeyAsync(browser, commit.Row);
            // Valores lidos das colunas alteradas: se o servidor já tiver outros, outra sessão gravou entretanto.
            var expected = new Dictionary<string, CellLiteral>(StringComparer.OrdinalIgnoreCase);
            if (!commit.IsInsert)
            {
                foreach (var name in changes.Keys)
                {
                    expected[name] = await CurrentValueAsync(browser, commit.Row, name);
                }
            }
            var writer = new RowWriter(session.Host);
            var change = new RowChange(commit.IsInsert ? RowChangeKind.Insert : RowChangeKind.Update, original, changes) { Expected = expected };
            var written = await WriteWithConflictCheckAsync(writer, table, change);
            if (written is null)
            {
                return GridCommitResult.Failed;
            }
            var reread = await writer.ReadRowAsync(table, written.KeyAfter, CancellationToken.None);
            if (reread is null)
            {
                return new GridCommitResult(true);
            }
            var row = new Dictionary<int, CellValue>();
            for (var i = 0; i < names.Count; i++)
            {
                if (reread.TryGetValue(names[i], out var value))
                {
                    row[i] = value is null ? CellValue.Null : new CellValue(value);
                }
            }
            return new GridCommitResult(true, row);
        }
        catch (Exception ex) when (ex is RowWriteException or DbOperationException or InvalidOperationException or Engine.Execution.SessionOperationException)
        {
            ShowEditError(ex.Message);
            return GridCommitResult.Failed;
        }
        finally
        {
            _context.Status.SetIdle();
        }
    }

    /// <summary>
    /// Insert row (Ins) ou Duplicate row (Ctrl+Ins, sem as colunas da chave primária; "with keys" copia-as também): uma
    /// linha nova no fim do grid, que só é gravada quando tiver alterações.
    /// </summary>
    private async Task InsertDataRowAsync(bool duplicate, bool withKeys)
    {
        if (_browser is not { } browser || DataGrid.Source is not { } source)
        {
            return;
        }
        Dictionary<int, CellValue>? values = null;
        if (duplicate && DataGrid.Selection.FocusedRow is var from and >= 0)
        {
            var primary = browser.Table.Indexes.FirstOrDefault(i => i.Kind == IndexKind.Primary)?.Columns.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
            values = [];
            for (var i = 0; i < source.Schema.Count; i++)
            {
                var name = source.Schema[i].Name;
                if (!withKeys && primary.Contains(name))
                {
                    continue;
                }
                var value = await CurrentValueAsync(browser, from, name);
                values[i] = new CellValue(value.Text, value.IsFunction);
            }
        }
        await DataGrid.InsertRowAsync(values);
    }

    /// <summary>Delete selected row(s) (Ctrl+Del): "Delete N row(s)?", um DELETE por linha e recarregar o Data tab.</summary>
    private async Task DeleteDataRowsAsync()
    {
        if (_browser is not { } browser || _browserSession is not { } session)
        {
            return;
        }
        var grid = DataGrid;
        var rows = grid.Selection.Count > 0
            ? grid.Selection.Ranges.SelectMany(r => LongRange(r.Start, r.End)).Where(r => r < grid.RowCount).ToList()
            : [grid.Selection.FocusedRow];
        if (rows.Count == 0 || !(await ConfirmAsync("Confirm", string.Create(CultureInfo.InvariantCulture, $"Delete {rows.Count} row(s)?") + SessionLine(session))).Ok)
        {
            return;
        }
        // Uma linha nova por gravar só se descarta.
        if (grid.Edits.HasPending && grid.Edits.PendingIsInsert && rows.Remove(grid.Edits.PendingRow))
        {
            grid.CancelEditing();
        }
        var writer = new RowWriter(session.Host);
        var deleted = 0;
        try
        {
            // Primeiro as chaves de todas as linhas (podem precisar do servidor), depois os DELETE numa transação própria.
            var originals = new List<IReadOnlyDictionary<string, CellLiteral>>();
            foreach (var row in rows.Where(r => r < grid.SourceRows || grid.Edits.TryGetCommitted(r, 0, out _)))
            {
                originals.Add(await OriginalKeyAsync(browser, row));
            }
            _context.Status.Busy = string.Create(CultureInfo.InvariantCulture, $"Deleting {originals.Count} row(s) ...");
            var progress = new Progress<int>(done => _context.Status.Progress = (double)done / Math.Max(1, originals.Count));
            // Uma transação do utilizador aberta pode ficar com parte das linhas apagadas: recarrega-se na mesma.
            deleted = originals.Count;
            await writer.DeleteRowsAsync(browser.Table, originals, progress, CancellationToken.None);
        }
        catch (Exception ex) when (ex is RowWriteException or DbOperationException or Engine.Execution.SessionOperationException)
        {
            ShowEditError(ex.Message);
        }
        finally
        {
            _context.Status.SetIdle();
        }
        if (deleted > 0)
        {
            grid.Edits.Reset();
            LoadDataForFocusedNode(force: true);
        }
    }

    private static IEnumerable<long> LongRange(long start, long end)
    {
        for (var r = Math.Min(start, end); r <= Math.Max(start, end); r++)
        {
            yield return r;
        }
    }
}
