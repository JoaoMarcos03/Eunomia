using System.Diagnostics;
using Eunomia.App.Data;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Results;

namespace Eunomia.UI.Shell;

/// <summary>
/// Ligações a partir de uma célula do Data grid (anexo C §3.5): Follow Foreign Key (a tabela referenciada abre no Data
/// tab filtrada pela chave da linha focada) e Open URL (abre no browser um valor http/https).
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Substitui a abertura do browser (testes).</summary>
    internal Action<string>? OpenUrlOverride { get; set; }

    private void InitGridLinks()
    {
        var c = _context.Commands;
        c.SetHandler("grid.followForeignKey", _ => new ValueTask(FollowForeignKeyAsync()), () => FocusedForeignKey() is not null);
        c.SetHandler("grid.openUrl", OpenFocusedUrl, () => FocusedUrl() is not null);
    }

    /// <summary>Chave estrangeira da tabela do Data tab que contém a coluna focada.</summary>
    private ForeignKeyDefinition? FocusedForeignKey()
    {
        if (QuickColumn() is not ({ } column, _) || _browser is not { } browser || DataGrid.Selection.FocusedRow < 0)
        {
            return null;
        }
        return browser.Table.ForeignKeys.FirstOrDefault(fk => fk.Columns.Contains(column.Name, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Follow Foreign Key: grava o filtro <c>col_ref = valor</c> (todas as colunas da chave) na tabela referenciada,
    /// foca-a na árvore e fica no Data tab, que a abre já filtrada.
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

    /// <summary>Valor da célula focada se for um endereço http ou https.</summary>
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
