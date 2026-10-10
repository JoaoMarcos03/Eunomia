using System.Diagnostics;
using Eunomia.App.Data;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Results;

namespace Eunomia.UI.Shell;

/// <summary>
/// Links from a Data grid cell (appendix C §3.5): Follow Foreign Key (the referenced table opens in the Data
/// tab filtered by the focused row's key) and Open URL (opens an http/https value in the browser).
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Replaces opening the browser (tests).</summary>
    internal Action<string>? OpenUrlOverride { get; set; }

    private void InitGridLinks()
    {
        var c = _context.Commands;
        c.SetHandler("grid.followForeignKey", _ => new ValueTask(FollowForeignKeyAsync()), () => FocusedForeignKey() is not null);
        c.SetHandler("grid.openUrl", OpenFocusedUrl, () => FocusedUrl() is not null);
    }

    /// <summary>Foreign key of the Data tab's table that contains the focused column.</summary>
    private ForeignKeyDefinition? FocusedForeignKey()
    {
        if (QuickColumn() is not ({ } column, _) || _browser is not { } browser || DataGrid.Selection.FocusedRow < 0)
        {
            return null;
        }
        return browser.Table.ForeignKeys.FirstOrDefault(fk => fk.Columns.Contains(column.Name, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Follow Foreign Key: saves the filter <c>col_ref = value</c> (every column of the key) on the referenced table,
    /// focuses it in the tree and stays on the Data tab, which opens it already filtered.
    /// </summary>
    private async Task FollowForeignKeyAsync()
    {
        if (FocusedForeignKey() is not { } fk || _browser is not { } browser || _dataTarget is not { } target
            || Sessions?.Connector.Find(target.Session) is not { } open || DialectOf() is not { } dialect)
        {
            return;
        }
        var row = DataGrid.Selection.FocusedRow;
        var parts = new List<string>();
        for (var i = 0; i < fk.Columns.Count && i < fk.ReferencedColumns.Count; i++)
        {
            var inner = browser.Select.Columns.ToList().FindIndex(n => string.Equals(n, fk.Columns[i], StringComparison.OrdinalIgnoreCase));
            var column = browser.Table.Columns.FirstOrDefault(c => string.Equals(c.Name, fk.Columns[i], StringComparison.OrdinalIgnoreCase));
            if (inner < 0 || column is null || browser.RawText(row, inner, out var truncated) is not { } value || truncated)
            {
                LogInfo($"Cannot follow foreign key \"{fk.Name}\": the value of column \"{fk.Columns[i]}\" is NULL or not loaded.");
                return;
            }
            parts.Add(DataFilters.Single(dialect, column with { Name = fk.ReferencedColumns[i] }, QuickFilterOperator.Equal, value));
        }
        var filter = string.Join(" AND ", parts);
        var key = KeyOf(open, fk.ReferencedTable);
        SetTableState(key, "Filter", filter);
        AddRecentFilter(key, filter);
        var referenced = fk.ReferencedTable with { Kind = DbObjectKind.Table };
        if (!await FocusTreeObjectAsync(open.Id, referenced))
        {
            LogInfo($"Table \"{fk.ReferencedTable.Schema}.{fk.ReferencedTable.Name}\" not found in the database tree.");
            return;
        }
        _tabs.SelectedItem = _tabs.DataTab;
    }

    /// <summary>Value of the focused cell if it is an http or https address.</summary>
    private string? FocusedUrl()
    {
        if (FocusedGrid() is not { } grid || grid.Selection.FocusedRow < 0 || grid.Selection.FocusedColumn < 0)
        {
            return null;
        }
        var text = grid.GetDisplayText(grid.Selection.FocusedRow, grid.Selection.FocusedColumn)?.Trim();
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.AbsoluteUri
            : null;
    }

    private void OpenFocusedUrl()
    {
        if (FocusedUrl() is not { } url)
        {
            return;
        }
        if (OpenUrlOverride is { } open)
        {
            open(url);
            return;
        }
        using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
