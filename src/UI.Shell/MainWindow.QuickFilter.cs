using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Eunomia.App.Commands;
using Eunomia.App.Data;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Results;
using Eunomia.Presentation.Logging;
using Eunomia.UI.Controls.Common.Dialogs;
using Eunomia.UI.Controls.Common.Text;
using ColumnDefinition = Eunomia.Drivers.Abstractions.ColumnDefinition;

namespace Eunomia.UI.Shell;

/// <summary>
/// Data grid Quick Filter (phase 5; appendix D B10; FAM-G13): captions with the real SQL computed when the menu opens, values
/// of the focused column in the selected rows, a typed value, clipboard (&lt; 1 KB), "More values" and Remove filter. Choosing
/// with Shift joins it to the current filter with AND. The filter panel opens and the filter applies right away.
/// </summary>
public sealed partial class MainWindow
{
    private static readonly (string Id, QuickFilterOperator Op)[] FocusedFilters =
    [
        ("quickFilter.focused1", QuickFilterOperator.Equal), ("quickFilter.focused2", QuickFilterOperator.NotEqual),
        ("quickFilter.focused3", QuickFilterOperator.StartsWith), ("quickFilter.focused4", QuickFilterOperator.EndsWith),
        ("quickFilter.focused5", QuickFilterOperator.Contains), ("quickFilter.focused6", QuickFilterOperator.Greater),
        ("quickFilter.focused7", QuickFilterOperator.Less),
    ];

    private static readonly (string Id, QuickFilterOperator Op)[] PromptFilters =
    [
        ("quickFilter.prompt1", QuickFilterOperator.Equal), ("quickFilter.prompt2", QuickFilterOperator.NotEqual),
        ("quickFilter.prompt3", QuickFilterOperator.Greater), ("quickFilter.prompt4", QuickFilterOperator.Less),
        ("quickFilter.prompt5", QuickFilterOperator.Contains),
    ];

    private static readonly (string Id, QuickFilterOperator Op)[] ClipboardFilters =
    [
        ("quickFilter.clipboard1", QuickFilterOperator.Equal), ("quickFilter.clipboard2", QuickFilterOperator.NotEqual),
        ("quickFilter.clipboard3", QuickFilterOperator.Greater), ("quickFilter.clipboard4", QuickFilterOperator.Less),
        ("quickFilter.clipboard5", QuickFilterOperator.Contains), ("quickFilter.clipboard6", QuickFilterOperator.InVerbatim),
    ];

    private readonly Dictionary<string, string?> _quickFilterSql = new(StringComparer.Ordinal);
    private string? _clipboardText;
    private IReadOnlyList<object> _moreValues = [];

    private void InitQuickFilters()
    {
        var c = _context.Commands;
        var menu = _ui.BuildContextMenu(MainMenuLayout.DataGridMenu);
        menu.Opening += (_, _) => PrepareQuickFilters(loadMoreValues: true);
        DataGrid.ContextMenu = menu;

        foreach (var (id, _) in FocusedFilters)
        {
            c.SetHandler(id, () =>
            {
                var and = _ui.InvokeModifiers.HasFlag(KeyModifiers.Shift);
                PrepareQuickFilters(loadMoreValues: false);
                if (_quickFilterSql.GetValueOrDefault(id) is { } sql)
                {
                    ApplyQuickFilter(sql, and);
                }
            }, () => QuickColumn() is not null);
            c.SetCaption(id, () => CaptionOf(id));
        }
        foreach (var (id, op) in PromptFilters)
        {
            c.SetHandler(id, _ => new ValueTask(PromptQuickFilterAsync(op, _ui.InvokeModifiers.HasFlag(KeyModifiers.Shift))), () => QuickColumn() is not null);
            c.SetCaption(id, () => QuickColumn() is { } column ? DataFilters.Caption(DataFilters.Single(DialectOf()!, column.Column, op, "...")) : null);
        }
        foreach (var (id, op) in new[] { ("quickFilter.null", QuickFilterOperator.IsNull), ("quickFilter.notNull", QuickFilterOperator.IsNotNull) })
        {
            c.SetHandler(id, () =>
            {
                if (QuickColumn() is { } column)
                {
                    ApplyQuickFilter(DataFilters.Single(DialectOf()!, column.Column, op, string.Empty), _ui.InvokeModifiers.HasFlag(KeyModifiers.Shift));
                }
            }, () => QuickColumn() is not null);
            c.SetCaption(id, () => QuickColumn() is { } column ? DataFilters.Single(DialectOf()!, column.Column, op, string.Empty) : null);
        }
        foreach (var (id, op) in ClipboardFilters)
        {
            c.SetHandler(id, _ => new ValueTask(ClipboardQuickFilterAsync(op, _ui.InvokeModifiers.HasFlag(KeyModifiers.Shift))), () => QuickColumn() is not null);
            c.SetCaption(id, () => QuickColumn() is { } column && _clipboardText is { Length: > 0 and < DataFilters.MaxValuesLength } clip
                ? DataFilters.Caption(DataFilters.Single(DialectOf()!, column.Column, op, clip))
                : null);
        }
    }

    private IDialect? DialectOf() =>
        _dataTarget is { } target ? Sessions?.Connector.Find(target.Session)?.Host.ServerServices?.Dialect : null;

    /// <summary>Column focused in the Data grid (table definition and index in the full source).</summary>
    private (ColumnDefinition Column, int Inner)? QuickColumn()
    {
        if (_browser is not { } browser || DataGrid.Source is not ProjectedRowSource projected || DialectOf() is null
            || DataGrid.Selection.FocusedColumn is var focused && (focused < 0 || focused >= projected.Schema.Count))
        {
            return null;
        }
        var inner = projected.InnerColumn(DataGrid.Selection.FocusedColumn);
        var name = browser.Select.Columns[inner];
        return browser.Table.Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) is { } column ? (column, inner) : null;
    }

    private string? CaptionOf(string id) => _quickFilterSql.GetValueOrDefault(id) is { } sql ? DataFilters.Caption(sql) : null;

    /// <summary>
    /// Computes the items' SQL with the values of the focused column in every selected row (up to 1 KB) and reads the
    /// clipboard; with the menu, it also requests the 50 most frequent values.
    /// </summary>
    private void PrepareQuickFilters(bool loadMoreValues)
    {
        _quickFilterSql.Clear();
        if (QuickColumn() is not ({ } column, var inner) || _browser is not { } browser || DialectOf() is not { } dialect)
        {
            return;
        }
        var values = new List<string?>();
        var length = 0;
        foreach (var row in SelectedRows())
        {
            var text = browser.RawText(row, inner, out var truncated);
            if (truncated)
            {
                continue;
            }
            values.Add(text);
            length += text?.Length ?? 0;
            if (length > DataFilters.MaxValuesLength)
            {
                break;
            }
        }
        foreach (var (id, op) in FocusedFilters)
        {
            _quickFilterSql[id] = DataFilters.FromValues(dialect, column, values, op);
        }
        _ = ReadClipboardAsync();
        if (loadMoreValues)
        {
            _ = LoadMoreValuesAsync(browser, column);
        }
        _ui.Refresh();
    }

    /// <summary>Selected rows (including the focused one), in order.</summary>
    private IEnumerable<long> SelectedRows()
    {
        var selection = DataGrid.Selection;
        var any = false;
        foreach (var (start, end) in selection.Ranges)
        {
            for (var row = start; row <= end; row++)
            {
                any = true;
                yield return row;
            }
        }
        if (!any && selection.FocusedRow >= 0)
        {
            yield return selection.FocusedRow;
        }
    }

    private async Task ReadClipboardAsync()
    {
        if (Clipboard is not { } clipboard)
        {
            return;
        }
        try
        {
            _clipboardText = (await clipboard.TryGetTextAsync())?.Trim();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _clipboardText = null;
        }
        _ui.Refresh();
    }

    /// <summary>"More values": refused on tables over 2 GB (10 GB if the column has an index), as in HeidiSQL.</summary>
    private async Task LoadMoreValuesAsync(TableBrowser browser, ColumnDefinition column)
    {
        _moreValues = [new MenuItem { Header = "Loading ...", IsEnabled = false }];
        var size = _dataTarget is { } target && Sessions?.Connector.Find(target.Session) is { } open
            ? open.Metadata.CachedObjects(browser.Table.Ref.Schema)?.Objects.FirstOrDefault(o => o.Ref.Name == browser.Table.Ref.Name)?.SizeBytes
            : null;
        var indexed = browser.Table.Indexes.Any(i => i.Columns.Contains(column.Name, StringComparer.OrdinalIgnoreCase));
        var limit = (indexed ? 10L : 2L) * 1024 * 1024 * 1024;
        if (size > limit)
        {
            _moreValues = [new MenuItem { Header = $"Table too large for counting values (> {(indexed ? 10 : 2)} GB)", IsEnabled = false }];
            return;
        }
        try
        {
            var dialect = DialectOf()!;
            var top = await browser.TopValuesAsync(column.Name, CancellationToken.None);
            var items = new List<object>();
            foreach (var (value, count) in top)
            {
                var condition = value is null ? DataFilters.Single(dialect, column, QuickFilterOperator.IsNull, string.Empty) : DataFilters.Single(dialect, column, QuickFilterOperator.Equal, value);
                var item = new MenuItem { Header = LiteralText.Escaped(DataFilters.Caption($"{condition} ({count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)})")) };
                item.Click += (_, _) => ApplyQuickFilter(condition, and: true);
                items.Add(item);
            }
            _moreValues = items.Count > 0 ? items : [new MenuItem { Header = "(none)", IsEnabled = false }];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _context.Log.Append(LogCategory.Error, ex.Message);
            _moreValues = [new MenuItem { Header = LiteralText.Escaped(ex.Message), IsEnabled = false }];
        }
    }

    private IReadOnlyList<object> MoreValuesItems() => _moreValues;

    private async Task PromptQuickFilterAsync(QuickFilterOperator op, bool and)
    {
        if (QuickColumn() is not ({ } column, var inner) || _browser is not { } browser)
        {
            return;
        }
        var focused = browser.RawText(DataGrid.Selection.FocusedRow, inner, out _) ?? string.Empty;
        var value = await InputDialog.ShowAsync(this, "Quick filter", $"Filter value for column {column.Name}:", focused);
        if (value is not null && DialectOf() is { } dialect)
        {
            ApplyQuickFilter(DataFilters.Single(dialect, column, op, value), and);
        }
    }

    private async Task ClipboardQuickFilterAsync(QuickFilterOperator op, bool and)
    {
        await ReadClipboardAsync();
        if (QuickColumn() is ({ } column, _) && DialectOf() is { } dialect && _clipboardText is { Length: > 0 and < DataFilters.MaxValuesLength } clip)
        {
            ApplyQuickFilter(DataFilters.Single(dialect, column, op, clip), and);
        }
    }

    /// <summary>Applies a Quick Filter: with Shift it joins the current filter with AND; the panel opens with the new filter.</summary>
    private void ApplyQuickFilter(string sql, bool and)
    {
        var filter = and && _browser is { } browser ? DataFilters.Combine(browser.Options.Filter, sql) : sql;
        _dataView.FilterEditor.Text = filter;
        _dataView.FilterPanel.IsVisible = true;
        ApplyFilter(filter);
    }
}
