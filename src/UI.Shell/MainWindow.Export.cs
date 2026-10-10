using System.Text;
using Avalonia.Input.Platform;
using Eunomia.App.Commands;
using Eunomia.App.Export;
using Eunomia.Engine.Results;
using Eunomia.Presentation.Logging;
using Eunomia.UI.Controls.Common.TreeList;

namespace Eunomia.UI.Shell;

/// <summary>
/// Copy as (16 formats), Copy column names and "Copy all lines … in CSV format" (phase 5; appendix B §1.5; FAM-C02, C06).
/// Copy as always uses the selected rows and the clipboard options (<c>GridExportClp*</c>); cut values make it
/// read the rows again without <c>LEFT()</c> before exporting.
/// </summary>
public sealed partial class MainWindow
{
    private void InitExport()
    {
        var c = _context.Commands;
        for (var i = 0; i < HeidiSqlCatalog.CopyAsFormats.Count; i++)
        {
            var format = (GridExportFormat)i;
            c.SetHandler(HeidiSqlCatalog.CopyAsFormats[i].Id, _ => new ValueTask(CopyAsAsync(format)), () => _browser is not null && DataGrid.Source is ProjectedRowSource);
        }
        c.SetHandler("export.gridRows", _ => new ValueTask(ExportGridRowsAsync()), () => _browser is not null && DataGrid.Source is ProjectedRowSource);
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

    /// <summary>Data grid rows and columns in the browser's source, and the column definitions.</summary>
    private (IReadOnlyList<long> Rows, IReadOnlyList<(int Column, GridExportColumn Info)> Columns)? DataGridExportShape(bool selectedOnly, bool focusedColumnOnly = false)
    {
        if (_browser is not { Source: { } source } browser || DataGrid.Source is not ProjectedRowSource projected)
        {
            return null;
        }
        var gridRows = selectedOnly ? SelectedRows().ToList() : Enumerable.Range(0, (int)DataGrid.RowCount).Select(r => (long)r).ToList();
        var filtered = projected.Inner as FilteredRowSource;
        var rows = gridRows.Where(r => r < projected.RowCount.Count).Select(r => filtered?.InnerRow(r) ?? r).ToList();
        var visible = Enumerable.Range(0, projected.Schema.Count);
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
        return (rows, columns);
    }

    /// <summary>Reads the rows to export; if any cell came cut, reads the rows again without <c>LEFT()</c> (appendix D B4).</summary>
    internal async Task<GridExportData?> ReadDataGridExportAsync(bool selectedOnly, bool focusedColumnOnly = false)
    {
        if (DataGridExportShape(selectedOnly, focusedColumnOnly) is not ({ } rows, { } columns) || _browser is not { Source: { } source } browser)
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
                from = full;
            }
            return await GridExportSource.ReadAsync(from, rows, columns, browser.Table.Ref.Name, browser.Select.KeyColumns, _context.Settings.Data.BlobAsText, CancellationToken.None);
        }
        finally
        {
            full?.Dispose();
            _context.Status.SetIdle();
        }
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
        if (DialectOf() is not { } dialect || await ReadDataGridExportAsync(selectedOnly: true) is not { } data)
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

    /// <summary>File name placeholders with the current session and database.</summary>
    private string ExpandExportFileName(string template)
    {
        var session = _dataTarget is { } target ? Sessions?.Connector.Find(target.Session) : null;
        var definition = session?.Definition;
        return ExportFile.ExpandPlaceholders(template, definition?.Path ?? "nosession", definition?.Host ?? string.Empty, definition?.User ?? string.Empty,
            _browser?.Table.Ref.Schema ?? "nodb", DateTime.Now);
    }

    /// <summary>Export grid rows (appendix B §1.4): dialog, reading the (full) rows and writing to the clipboard or a file.</summary>
    private async Task ExportGridRowsAsync()
    {
        if (_browser is not { } browser || DialectOf() is not { } dialect)
        {
            return;
        }
        var focusedColumn = DataGrid.Selection.FocusedColumn >= 0 && DataGrid.Source is { } grid && DataGrid.Selection.FocusedColumn < grid.Schema.Count
            ? grid.Schema[DataGrid.Selection.FocusedColumn].Name
            : null;
        var request = ExportDialogOverride is { } fake
            ? fake()
            : await UI.Features.Transfer.GridExportDialog.ShowAsync(this, _context.Settings.Export, SelectedRows().LongCount(), DataGrid.RowCount, focusedColumn, ExpandExportFileName);
        _context.SaveSettings();
        if (request is null || await ReadDataGridExportAsync(request.SelectedOnly, request.FocusedColumnOnly) is not { } data)
        {
            return;
        }
        var options = request.Options with
        {
            Query = request.IncludeQuery ? browser.Select.Sql : null,
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
            for (long r = 0; r < grid.RowCount; r++)
            {
                sb.Append(string.Join('\t', Enumerable.Range(0, source.Schema.Count).Select(c => grid.GetDisplayText(r, c) ?? string.Empty))).Append(Environment.NewLine);
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
