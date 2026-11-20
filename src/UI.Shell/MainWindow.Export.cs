using System.Text;
using Avalonia.Input.Platform;
using Eunomia.App.Commands;
using Eunomia.App.Export;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Execution.Editing;
using Eunomia.Engine.Results;
using Eunomia.Presentation.Logging;
using Eunomia.UI.Controls.Common.TreeList;
using Eunomia.UI.Controls.Grid;

namespace Eunomia.UI.Shell;

/// <summary>
/// Copy as (16 formats), Copy column names and "Copy all lines … in CSV format" (phase 5; appendix B §1.5; FAM-C02, C06).
/// Copy as always uses the selected rows and the clipboard options (<c>GridExportClp*</c>); cut values make it
/// read the rows again without <c>LEFT()</c> before exporting. Copy as and Export grid rows serve the focused grid: a
/// query tab result tab (HeidiSQL's <c>popupDataGrid</c> is the same for both) or, otherwise, the Data grid.
/// </summary>
public sealed partial class MainWindow
{
    private void InitExport()
    {
        var c = _context.Commands;
        for (var i = 0; i < HeidiSqlCatalog.CopyAsFormats.Count; i++)
        {
            var format = (GridExportFormat)i;
            c.SetHandler(HeidiSqlCatalog.CopyAsFormats[i].Id, _ => new ValueTask(CopyAsAsync(format)), CanExportGrid);
        }
        c.SetHandler("export.gridRows", _ => new ValueTask(ExportGridRowsAsync()), CanExportGrid);
        c.SetHandler("edit.copyColumnNames", _ => new ValueTask(CopyColumnNamesAsync()), () => ColumnNames() is { Count: > 0 });
        c.SetHandler("edit.copyGridNodes", _ => new ValueTask(CopyAllLinesAsync()), () => FocusedGrid() is not null || FocusedTreeList() is not null);
    }

    private TreeListControl? FocusedTreeList()
    {
        for (var v = FocusedElement as Avalonia.Visual; v is not null; v = Avalonia.VisualTree.VisualExtensions.GetVisualParent(v))
        {
            if (v is TreeListControl list)
            {
                return list;
            }
        }
        return null;
    }

    /// <summary>Copy as and Export grid rows: the focused result tab (with the session that produced it), otherwise the Data grid.</summary>
    private bool CanExportGrid() =>
        FocusedResultView() is { } result
            ? result.Grid.Control.Source is not null && DialectOf(result) is not null
            : _browser is not null && DataGrid.Source is ProjectedRowSource;

    /// <summary>The result tab whose grid has the focus (a right click focuses the cell before opening the menu).</summary>
    private ResultView? FocusedResultView()
    {
        if (FocusedGrid() is not { } grid)
        {
            return null;
        }
        foreach (var state in _queryStates.Values)
        {
            if (state.Results.FirstOrDefault(r => ReferenceEquals(r.Grid.Control, grid)) is { } result)
            {
                return result;
            }
        }
        return null;
    }

    private static IDialect? DialectOf(ResultView result) => result.Session?.Host.ServerServices?.Dialect;

    /// <summary>
    /// Data grid rows and columns in the browser's source, and the column definitions; <c>GridRows</c> and <c>GridColumns</c>
    /// are the same rows and columns in the grid.
    /// </summary>
    private (IReadOnlyList<long> Rows, IReadOnlyList<(int Column, GridExportColumn Info)> Columns, IReadOnlyList<long> GridRows, IReadOnlyList<int> GridColumns)? DataGridExportShape(bool selectedOnly, bool focusedColumnOnly = false)
    {
        if (_browser is not { Source: { } source } browser || DataGrid.Source is not ProjectedRowSource projected)
        {
            return null;
        }
        var gridRows = (selectedOnly ? SelectedRows() : AllRows(DataGrid)).Where(r => r < projected.RowCount.Count).ToList();
        var rows = gridRows;
        var visible = Enumerable.Range(0, projected.Schema.Count).ToList();
        if (focusedColumnOnly && DataGrid.Selection.FocusedColumn >= 0)
        {
            visible = [DataGrid.Selection.FocusedColumn];
        }
        var columns = visible.Select(c =>
        {
            var inner = projected.InnerColumn(c);
            var info = source.Schema[inner];
            var definition = browser.Table.Columns.FirstOrDefault(d => string.Equals(d.Name, info.Name, StringComparison.OrdinalIgnoreCase));
            return (inner, new GridExportColumn(info.Name, info, definition));
        }).ToList();
        return (rows, columns, gridRows, visible);
    }

    /// <summary>Reads the rows to export; if any cell came cut, reads the rows again without <c>LEFT()</c> (appendix D B4).</summary>
    internal async Task<GridExportData?> ReadDataGridExportAsync(bool selectedOnly, bool focusedColumnOnly = false)
    {
        if (DataGridExportShape(selectedOnly, focusedColumnOnly) is not ({ } rows, { } columns, { } gridRows, { } gridColumns) || _browser is not { Source: { } source } browser)
        {
            return null;
        }
        IRowSource from = source;
        MaterializedRowSource? full = null;
        try
        {
            if (await GridExportSource.HasTruncatedAsync(source, rows, columns.Select(c => c.Column).ToList(), CancellationToken.None))
            {
                _context.Status.Busy = "Fetching full rows ...";
                full = await browser.LoadFullRowsAsync(CancellationToken.None);
                // Without ORDER BY (or with ties in the sort) the server may return the rows in another order: each row is
                // found again by its key. A row no longer there: the cut values are exported, not another row's.
                if (await RowsByKeyAsync(source, full, rows, browser.Select.KeyColumns) is { } matched)
                {
                    from = full;
                    rows = matched;
                }
            }
            var data = await GridExportSource.ReadAsync(from, rows, columns, browser.Table.Ref.Name, browser.Select.KeyColumns, _context.Settings.Data.BlobAsText, CancellationToken.None);
            return WithCommittedValues(DataGrid, data, gridRows, gridColumns);
        }
        finally
        {
            full?.Dispose();
            _context.Status.SetIdle();
        }
    }

    /// <summary>
    /// Rows of <paramref name="full"/> with the same key as <paramref name="rows"/> of <paramref name="source"/> (the key
    /// columns are never cut); <c>null</c> if there is no key or some row is not in <paramref name="full"/>.
    /// </summary>
    internal static async Task<List<long>?> RowsByKeyAsync(IRowSource source, IRowSource full, IReadOnlyList<long> rows, IReadOnlyList<string> keys)
    {
        static List<int> Positions(ResultSchema schema, IReadOnlyList<string> keys) =>
            keys.Select(k => schema.Columns.ToList().FindIndex(c => string.Equals(c.Name, k, StringComparison.OrdinalIgnoreCase))).ToList();
        static string KeyText(RowBlock block, int row, List<int> columns) =>
            string.Join('\u0001', columns.Select(c => DisplayFormatter.FormatRaw(block, row, c) ?? "\u0002"));
        var sourceKeys = Positions(source.Schema, keys);
        var fullKeys = Positions(full.Schema, keys);
        if (keys.Count == 0 || sourceKeys.Contains(-1) || fullKeys.Contains(-1))
        {
            return null;
        }
        var index = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var b = 0; (long)b * full.BlockSize < full.RowCount.Count; b++)
        {
            var block = await full.GetBlockAsync(b, CancellationToken.None);
            for (var r = 0; r < block.RowCount; r++)
            {
                index.TryAdd(KeyText(block, r, fullKeys), block.FirstRow + r);
            }
        }
        var result = new List<long>(rows.Count);
        foreach (var row in rows)
        {
            var block = await source.GetBlockAsync((int)(row / source.BlockSize), CancellationToken.None);
            if (!index.TryGetValue(KeyText(block, (int)(row - block.FirstRow), sourceKeys), out var at))
            {
                return null;
            }
            result.Add(at);
        }
        return result;
    }

    private static List<long> AllRows(DataGridControl grid)
    {
        var rows = new List<long>((int)Math.Min(grid.RowCount, int.MaxValue));
        for (long r = 0; r < grid.RowCount; r++)
        {
            rows.Add(r);
        }
        return rows;
    }

    /// <summary>
    /// Rows of a result tab, in the grid's order (local sort and filter included), with the values already saved on top.
    /// The table name is the source table's when every column comes from the same one (HeidiSQL's <c>org_table</c>),
    /// otherwise <c>UnknownTable</c> (appendix B §1.5); the key only when the result is editable and includes all of it.
    /// Query results are never cut, so there is no re-read.
    /// </summary>
    private async Task<GridExportData?> ReadResultExportAsync(ResultView entry, bool selectedOnly, bool focusedColumnOnly)
    {
        var grid = entry.Grid.Control;
        if (grid.Source is not { } source)
        {
            return null;
        }
        var count = source.RowCount.Count;
        var rows = (selectedOnly ? SelectedRows(grid) : AllRows(grid)).Where(r => r < count).ToList();
        IEnumerable<int> visible = Enumerable.Range(0, source.Schema.Count);
        if (focusedColumnOnly && grid.Selection.FocusedColumn is var focused && focused >= 0 && focused < source.Schema.Count)
        {
            visible = [focused];
        }
        var table = entry.EditTable;
        var columns = visible.Select(c =>
        {
            var info = source.Schema[c];
            var definition = table?.Columns.FirstOrDefault(d => string.Equals(d.Name, info.SourceColumn, StringComparison.OrdinalIgnoreCase));
            return (Column: c, Info: new GridExportColumn(info.Name, info, definition));
        }).ToList();
        var tables = source.Schema.Columns.Select(c => c.SourceTable).Distinct(StringComparer.Ordinal).ToList();
        var tableName = tables is [{ Length: > 0 } single] ? single : "UnknownTable";
        var keys = new List<string>();
        if (table is not null && RowKey.Of(table) is { IsUnique: true } key)
        {
            keys = key.Columns
                .Select(k => columns.Select(c => c.Info).FirstOrDefault(c => string.Equals(c.Info.SourceColumn, k.Name, StringComparison.OrdinalIgnoreCase))?.Name)
                .OfType<string>()
                .ToList();
            if (keys.Count != key.Columns.Count)
            {
                keys.Clear();
            }
        }
        var data = await GridExportSource.ReadAsync(source, rows, columns, tableName, keys, _context.Settings.Data.BlobAsText, CancellationToken.None);
        return WithCommittedValues(grid, data, rows, columns.Select(c => c.Column).ToList());
    }

    /// <summary>
    /// Values saved and read back in the grid (Data tab or editable result tab) go over the source, as in the grid, until it
    /// reloads. <paramref name="gridRows"/> and <paramref name="gridColumns"/> are the exported rows and columns in the grid.
    /// </summary>
    private static GridExportData WithCommittedValues(DataGridControl grid, GridExportData data, IReadOnlyList<long> gridRows, IReadOnlyList<int> gridColumns)
    {
        List<IReadOnlyList<string?>>? patched = null;
        for (var i = 0; i < gridRows.Count; i++)
        {
            for (var j = 0; j < gridColumns.Count; j++)
            {
                if (grid.Edits.TryGetCommitted(gridRows[i], gridColumns[j], out var value) && !value.IsFunction)
                {
                    patched ??= [.. data.Rows];
                    var row = patched[i].ToArray();
                    row[j] = value.Text;
                    patched[i] = row;
                }
            }
        }
        return patched is null ? data : data with { Rows = patched };
    }

    /// <summary>Rows to export from the given result tab, otherwise from the Data grid, and the dialect of their session.</summary>
    private async Task<(GridExportData Data, IDialect Dialect)?> ReadGridExportAsync(ResultView? result, bool selectedOnly, bool focusedColumnOnly = false)
    {
        if (result is not null)
        {
            return DialectOf(result) is { } resultDialect && await ReadResultExportAsync(result, selectedOnly, focusedColumnOnly) is { } resultData
                ? (resultData, resultDialect)
                : null;
        }
        return DialectOf() is { } dialect && await ReadDataGridExportAsync(selectedOnly, focusedColumnOnly) is { } data ? (data, dialect) : null;
    }

    private GridExportOptions ClipboardExportOptions()
    {
        var e = _context.Settings.Export;
        return new GridExportOptions
        {
            IncludeColumnNames = e.ClipboardColumnNames,
            IncludeAutoIncrement = e.ClipboardAutoIncrement,
            RemoveLinebreaks = e.ClipboardRemoveLinebreaks,
            Separator = e.ClipboardSeparator,
            Encloser = e.ClipboardEncloser,
            Terminator = e.ClipboardTerminator,
            NullValue = e.ClipboardNullValue,
            ToClipboard = true,
            Generator = "Eunomia " + ShellContext.Version,
        };
    }

    /// <summary>Copy as: the selected rows, in the chosen format, to the clipboard as text (appendix B §1.5).</summary>
    private async Task CopyAsAsync(GridExportFormat format)
    {
        if (await ReadGridExportAsync(FocusedResultView(), selectedOnly: true) is not ({ } data, { } dialect))
        {
            return;
        }
        var text = GridExporter.Write(format, data, ClipboardExportOptions(), dialect);
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
        _context.Log.Append(LogCategory.Info, $"{data.Rows.Count} row(s) copied to clipboard as {HeidiSqlCatalog.CopyAsFormats[(int)format].Caption}.");
    }

    /// <summary>Replaces the Export grid rows dialog (tests).</summary>
    internal Func<UI.Features.Transfer.GridExportRequest?>? ExportDialogOverride { get; set; }

    /// <summary>File name placeholders with the session and database of the exported grid.</summary>
    private string ExpandExportFileName(string template, ResultView? result)
    {
        var session = result is not null ? result.Session : _dataTarget is { } target ? Sessions?.Connector.Find(target.Session) : null;
        var definition = session?.Definition;
        var database = result is not null
            ? result.Result.Source.Schema.Columns.Select(c => c.SourceDatabase).FirstOrDefault(d => !string.IsNullOrEmpty(d))
            : _browser?.Table.Ref.Schema;
        return ExportFile.ExpandPlaceholders(template, definition?.Path ?? "nosession", definition?.Host ?? string.Empty, definition?.User ?? string.Empty,
            database ?? "nodb", DateTime.Now);
    }

    /// <summary>Export grid rows (appendix B §1.4): dialog, reading the (full) rows and writing to the clipboard or a file.</summary>
    private async Task ExportGridRowsAsync()
    {
        // The grid is picked before the dialog, which takes the focus.
        var result = FocusedResultView();
        if (result is null && _browser is null)
        {
            return;
        }
        var grid = result?.Grid.Control ?? DataGrid;
        var focusedColumn = grid.Selection.FocusedColumn >= 0 && grid.Source is { } source && grid.Selection.FocusedColumn < source.Schema.Count
            ? source.Schema[grid.Selection.FocusedColumn].Name
            : null;
        var request = ExportDialogOverride is { } fake
            ? fake()
            : await UI.Features.Transfer.GridExportDialog.ShowAsync(this, _context.Settings.Export, SelectedRows(grid).LongCount(), grid.RowCount, focusedColumn, t => ExpandExportFileName(t, result));
        _context.SaveSettings();
        if (request is null || await ReadGridExportAsync(result, request.SelectedOnly, request.FocusedColumnOnly) is not ({ } data, { } dialect))
        {
            return;
        }
        var options = request.Options with
        {
            Query = request.IncludeQuery ? result?.Result.Sql ?? _browser?.Select.Sql : null,
            Generator = "Eunomia " + ShellContext.Version,
            Culture = System.Globalization.CultureInfo.CurrentCulture,
        };
        var text = GridExporter.Write(request.Format, data, options, dialect);
        try
        {
            if (request.ToClipboard)
            {
                if (Clipboard is { } clipboard)
                {
                    await clipboard.SetTextAsync(text);
                }
                _context.Log.Append(LogCategory.Info, $"{data.Rows.Count} row(s) exported to clipboard.");
                return;
            }
            await File.WriteAllBytesAsync(request.FileName, ExportFile.Encode(text, request.Encoding));
            _context.Log.Append(LogCategory.Info, $"{data.Rows.Count} row(s) exported to {request.FileName}.");
            if (request.OpenFile)
            {
                // Opens the file with the name already expanded (HeidiSQL opened the name with the placeholders).
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(request.FileName) { UseShellExecute = true })?.Dispose();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ShowError(ex);
        }
    }

    /// <summary>Visible captions of the focused grid or list.</summary>
    private IReadOnlyList<string>? ColumnNames()
    {
        if (FocusedGrid() is { Source: { } source })
        {
            return source.Schema.Columns.Select(c => c.Name).ToList();
        }
        return FocusedTreeList()?.Columns.Where(c => c.Visible).Select(c => c.Header).ToList();
    }

    /// <summary>Copy column names: one per line.</summary>
    private async Task CopyColumnNamesAsync()
    {
        if (ColumnNames() is { } names && Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(string.Join(Environment.NewLine, names));
        }
    }

    /// <summary>"Copy all lines from listing or tree in CSV format": TSV of the visible text, unquoted.</summary>
    private async Task CopyAllLinesAsync()
    {
        var sb = new StringBuilder();
        if (FocusedGrid() is { Source: { } source } grid)
        {
            // Every row, also the ones not in the grid's cache (the display text of those is not available without I/O).
            var result = FocusedResultView();
            var data = result is not null ? await ReadResultExportAsync(result, selectedOnly: false, focusedColumnOnly: false)
                : ReferenceEquals(grid, DataGrid) ? await ReadDataGridExportAsync(selectedOnly: false)
                : null;
            if (data is null)
            {
                var count = source.RowCount.Count;
                var columns = Enumerable.Range(0, source.Schema.Count).Select(c => (c, new GridExportColumn(source.Schema[c].Name, source.Schema[c]))).ToList();
                data = await GridExportSource.ReadAsync(source, AllRows(grid).Where(r => r < count).ToList(), columns, string.Empty, [], _context.Settings.Data.BlobAsText, CancellationToken.None);
            }
            foreach (var row in data.Rows)
            {
                sb.Append(string.Join('\t', row.Select(v => v ?? DisplayFormatter.NullText))).Append(Environment.NewLine);
            }
        }
        else if (FocusedTreeList() is { } list)
        {
            var visible = list.Columns.Select((c, i) => (c, i)).Where(x => x.c.Visible).Select(x => x.i).ToList();
            foreach (var node in list.VisibleNodes)
            {
                sb.Append(string.Join('\t', visible.Select(i => node[i]))).Append(Environment.NewLine);
            }
        }
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(sb.ToString());
        }
    }
}
