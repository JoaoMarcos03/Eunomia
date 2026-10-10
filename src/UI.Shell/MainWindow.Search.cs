using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Input.Platform;
using Eunomia.App.Search;
using Eunomia.Engine.Results;
using Eunomia.UI.Controls.Common.Dialogs;
using Eunomia.UI.Controls.Grid;
using Eunomia.UI.Controls.SqlEditor;
using Eunomia.UI.Shell.Views;

namespace Eunomia.UI.Shell;

/// <summary>
/// Find (Ctrl+F), Replace (Ctrl+R) and Find again (F3) (appendix C §4.7; FAM-Q10): in the focused editor (or the active query
/// tab's) or in the focused grid (or the active grid). The initial text is the selection, the word at the cursor or the
/// focused cell. In the grid it only searches, until editing arrives (phase 7). F3 repeats the last search from the cursor.
/// </summary>
public sealed partial class MainWindow
{
    private (SearchOptions Options, object Target)? _lastSearch;
    private CancellationTokenSource? _gridSearch;

    /// <summary>Replaces the dialog (tests): receives the initial text, Replace, the targets and the selected one.</summary>
    internal Func<string, bool, IReadOnlyList<SearchTarget>, int, SearchRequest?>? SearchDialogOverride { get; set; }

    private void InitSearch()
    {
        var c = _context.Commands;
        c.SetHandler("search.find", _ => new ValueTask(OpenSearchAsync(replace: false)), () => SearchTargets().Count > 0);
        c.SetHandler("search.replace", _ => new ValueTask(OpenSearchAsync(replace: true)), () => SearchTargets().Count > 0);
        c.SetHandler("search.findAgain", _ => new ValueTask(SearchAgainAsync()), () => _lastSearch is not null);
        c.SetHandler("query.toggleComment", () => EditingTarget()!.ToggleComment(), () => EditingTarget() is not null);
        c.SetHandler("editor.upperCase", () => EditingTarget()!.ChangeCase(upper: true), () => EditingTarget() is not null);
        c.SetHandler("editor.lowerCase", () => EditingTarget()!.ChangeCase(upper: false), () => EditingTarget() is not null);
        c.SetHandler("editor.moveLineUp", () => EditingTarget()!.MoveLines(-1), () => EditingTarget() is not null);
        c.SetHandler("editor.moveLineDown", () => EditingTarget()!.MoveLines(1), () => EditingTarget() is not null);
        c.SetHandler("query.reformat", ReformatSql, () => EditingTarget() is { Text.Length: > 0 });
        c.SetHandler("edit.copyTabsToSpaces", _ => new ValueTask(CopyTabsToSpacesAsync()), () => FocusedSqlEditor() is { Editor.SelectionLength: > 0 });
    }

    /// <summary>Reformat SQL (Ctrl+F8): the selection, or the whole text, through the internal formatter, as a single undo step.</summary>
    private void ReformatSql()
    {
        var editor = EditingTarget()!.Editor;
        var (start, length) = editor.SelectionLength > 0 ? (editor.SelectionStart, editor.SelectionLength) : (0, editor.Document.TextLength);
        var formatted = Eunomia.Engine.Sql.Formatting.SqlFormatter.Format(editor.Document.GetText(start, length));
        editor.Document.Replace(start, length, formatted);
    }

    /// <summary>Copy with tabs to spaces (Ctrl+Shift+C): the focused editor's selection, with each tab replaced by spaces.</summary>
    private async Task CopyTabsToSpacesAsync()
    {
        if (FocusedSqlEditor() is { } editor && Clipboard is { } clipboard)
        {
            var spaces = new string(' ', Math.Clamp(_context.Settings.Query.TabWidth, 1, 16));
            await clipboard.SetTextAsync(editor.Editor.SelectedText.Replace("\t", spaces, StringComparison.Ordinal));
        }
    }

    /// <summary>Editor for text operations: the focused one (if it can be written to), otherwise the active query tab's.</summary>
    private SqlEditorControl? EditingTarget() =>
        FocusedSqlEditor() is { Editor.IsReadOnly: false } focused ? focused
        : _tabs.ActiveQueryTab is not null ? ActiveQueryView?.Editor : null;

    /// <summary>
    /// Query › Editor commands ▸ (appendix C §1.7): AvaloniaEdit's editing commands, with the name split by spaces
    /// and the shortcut, running on the active query tab's editor.
    /// </summary>
    internal IReadOnlyList<object> EditorCommandItems()
    {
        AvaloniaEdit.RoutedCommand[] commands =
        [
            AvaloniaEdit.AvaloniaEditCommands.DeleteLine, AvaloniaEdit.AvaloniaEditCommands.RemoveLeadingWhitespace,
            AvaloniaEdit.AvaloniaEditCommands.RemoveTrailingWhitespace, AvaloniaEdit.AvaloniaEditCommands.ConvertToUppercase,
            AvaloniaEdit.AvaloniaEditCommands.ConvertToLowercase, AvaloniaEdit.AvaloniaEditCommands.ConvertToTitleCase,
            AvaloniaEdit.AvaloniaEditCommands.InvertCase, AvaloniaEdit.AvaloniaEditCommands.ConvertTabsToSpaces,
            AvaloniaEdit.AvaloniaEditCommands.ConvertSpacesToTabs, AvaloniaEdit.AvaloniaEditCommands.ConvertLeadingTabsToSpaces,
            AvaloniaEdit.AvaloniaEditCommands.ConvertLeadingSpacesToTabs, AvaloniaEdit.AvaloniaEditCommands.IndentSelection,
            AvaloniaEdit.AvaloniaEditCommands.ToggleOverstrike,
        ];
        var area = EditingTarget()?.Editor.TextArea;
        return commands.Select(command =>
        {
            var caption = Regex.Replace(command.Name, "(?<=[a-z])(?=[A-Z])", " ");
            if (command.Gesture is { } gesture)
            {
                caption += "   (" + gesture + ")";
            }
            var item = new Avalonia.Controls.MenuItem { Header = caption, IsEnabled = area is not null };
            item.Click += (_, _) =>
            {
                if (area is not null && command.CanExecute(null, area))
                {
                    command.Execute(null, area);
                }
            };
            return (object)item;
        }).ToList();
    }

    /// <summary>Possible targets: the editor and the grid, the focused one first.</summary>
    private List<object> SearchTargets()
    {
        var targets = new List<object>();
        var focusedEditor = FocusedSqlEditor();
        var focusedGrid = FocusedGrid();
        var editor = focusedEditor ?? (_tabs.ActiveQueryTab is not null ? ActiveQueryView?.Editor : null);
        var grid = focusedGrid ?? (_tabs.ActiveQueryTab is not null ? ActiveQueryView?.ActiveGrid : ReferenceEquals(_tabs.SelectedItem, _tabs.DataTab) ? DataGrid : null);
        if (grid is { Source: null })
        {
            grid = null;
        }
        if (focusedGrid is not null && grid is not null)
        {
            targets.Add(grid);
        }
        if (editor is not null)
        {
            targets.Add(editor);
        }
        if (focusedGrid is null && grid is not null)
        {
            targets.Add(grid);
        }
        return targets;
    }

    private SqlEditorControl? FocusedSqlEditor()
    {
        for (var v = FocusedElement as Avalonia.Visual; v is not null; v = Avalonia.VisualTree.VisualExtensions.GetVisualParent(v))
        {
            if (v is SqlEditorControl editor)
            {
                return editor;
            }
        }
        return null;
    }

    private static SearchTarget Describe(object target) => target switch
    {
        SqlEditorControl editor => new SearchTarget("SQL editor", !editor.Editor.IsReadOnly, editor.Editor.SelectionLength > 0),
        _ => new SearchTarget("Grid", CanReplace: false, HasSelection: false),
    };

    /// <summary>Initial text: the (single-line) selection or the word at the cursor; in the grid, the focused cell.</summary>
    private static string Prefill(object target)
    {
        if (target is SqlEditorControl editor)
        {
            var e = editor.Editor;
            if (e.SelectionLength > 0 && !e.SelectedText.Contains('\n', StringComparison.Ordinal))
            {
                return e.SelectedText;
            }
            var text = e.Text;
            var start = e.CaretOffset;
            var end = start;
            while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] is '_' or '$'))
            {
                start--;
            }
            while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] is '_' or '$'))
            {
                end++;
            }
            return text[start..end];
        }
        if (target is DataGridControl grid && grid.Selection.FocusedRow >= 0 && grid.GetDisplayText(grid.Selection.FocusedRow, grid.Selection.FocusedColumn) is { } cell
            && !ReferenceEquals(cell, DisplayFormatter.NullText))
        {
            return cell;
        }
        return string.Empty;
    }

    private async Task OpenSearchAsync(bool replace)
    {
        var targets = SearchTargets();
        if (targets.Count == 0)
        {
            return;
        }
        var described = targets.Select(Describe).ToList();
        var find = Prefill(targets[0]);
        var request = SearchDialogOverride is { } fake
            ? fake(find, replace, described, 0)
            : await SearchReplaceDialog.ShowAsync(this, find, replace, described, 0, _context.Settings.Search);
        if (request is null || request.Target < 0 || request.Target >= targets.Count)
        {
            return;
        }
        var target = targets[request.Target];
        _lastSearch = (request.Options, target);
        await SearchAsync(target, request.Options, request.ReplaceAll, again: false);
    }

    /// <summary>F3: the last search (or replace) again, from the cursor.</summary>
    private Task SearchAgainAsync() =>
        _lastSearch is { } last ? SearchAsync(last.Target, last.Options with { EntireScope = false }, replaceAll: false, again: true) : Task.CompletedTask;

    private async Task SearchAsync(object target, SearchOptions options, bool replaceAll, bool again)
    {
        try
        {
            switch (target)
            {
                case SqlEditorControl editor:
                    await SearchEditorAsync(editor, options, replaceAll, again);
                    break;
                case DataGridControl grid:
                    await SearchGridAsync(grid, options, again);
                    break;
            }
        }
        catch (ArgumentException ex)
        {
            // Invalid regex.
            ShowError(ex);
        }
        catch (RegexMatchTimeoutException ex)
        {
            ShowError(ex);
        }
    }

    private void NotFound(SearchOptions options) =>
        _context.Status.Info = string.Create(CultureInfo.InvariantCulture, $"Search string \"{options.Find}\" not found");

    private async Task SearchEditorAsync(SqlEditorControl editor, SearchOptions options, bool replaceAll, bool again)
    {
        var e = editor.Editor;
        var selectionStart = e.SelectionLength > 0 ? e.SelectionStart : e.CaretOffset;
        var selectionEnd = selectionStart + e.SelectionLength;
        var (scopeStart, scopeEnd) = options.SelectionOnly && e.SelectionLength > 0 ? (selectionStart, selectionEnd) : (0, e.Document.TextLength);
        if (options.Replace is not null && e.IsReadOnly)
        {
            options = options with { Replace = null };
        }

        if (replaceAll && options.Replace is not null && !options.PromptOnReplace)
        {
            var text = e.Text;
            var (replaced, count) = TextSearch.ReplaceAll(text, scopeStart, scopeEnd, options);
            if (count == 0)
            {
                NotFound(options);
                return;
            }
            var tail = text.Length - scopeEnd;
            e.Document.Replace(scopeStart, scopeEnd - scopeStart, replaced[scopeStart..(replaced.Length - tail)]);
            _context.Status.Info = string.Create(CultureInfo.InvariantCulture, $"{count} occurrence(s) replaced");
            return;
        }

        var from = options.EntireScope && !again
            ? (options.Backward ? scopeEnd : scopeStart)
            : (options.Backward ? selectionStart : selectionEnd);
        var replacedCount = 0;
        while (true)
        {
            var match = TextSearch.FindNext(e.Text, from, scopeStart, scopeEnd, options);
            if (match is not { } m)
            {
                if (replacedCount == 0)
                {
                    NotFound(options);
                }
                break;
            }
            e.Select(m.Start, m.Length);
            e.ScrollToLine(e.Document.GetLineByOffset(m.Start).LineNumber);
            if (options.Replace is null)
            {
                break;
            }
            var answer = options.PromptOnReplace
                ? await MessageDialog.ShowAsync(this, "Confirm", $"Replace this occurrence of \"{options.Find}\"?", MessageDialogButtons.YesNo | MessageDialogButtons.Cancel)
                : MessageDialogResult.Yes;
            if (answer == MessageDialogResult.Cancel)
            {
                break;
            }
            if (answer == MessageDialogResult.Yes)
            {
                e.Document.Replace(m.Start, m.Length, m.Replacement);
                e.Select(m.Start, m.Replacement.Length);
                scopeEnd += m.Replacement.Length - m.Length;
                replacedCount++;
                from = options.Backward ? m.Start : m.Start + m.Replacement.Length;
            }
            else
            {
                from = options.Backward ? m.Start : m.Start + m.Length;
            }
            if (!replaceAll)
            {
                break;
            }
        }
        if (replaceAll && replacedCount > 0)
        {
            _context.Status.Info = string.Create(CultureInfo.InvariantCulture, $"{replacedCount} occurrence(s) replaced");
        }
    }

    private async Task SearchGridAsync(DataGridControl grid, SearchOptions options, bool again)
    {
        if (grid.Source is not { } source)
        {
            return;
        }
        var columns = source.Schema.Count;
        long row;
        int column;
        if (options.EntireScope && !again)
        {
            (row, column) = options.Backward ? (source.RowCount.Count - 1, columns - 1) : (0, 0);
        }
        else
        {
            // After the focused cell.
            (row, column) = (Math.Max(0, grid.Selection.FocusedRow), grid.Selection.FocusedColumn + (options.Backward ? -1 : 1));
            if (column >= columns)
            {
                (row, column) = (row + 1, 0);
            }
            else if (column < 0)
            {
                (row, column) = (row - 1, columns - 1);
            }
            if (row < 0 || row >= source.RowCount.Count)
            {
                NotFound(options);
                return;
            }
        }
        _gridSearch?.Cancel();
        using var cts = new CancellationTokenSource();
        _gridSearch = cts;
        _context.Status.Busy = "Searching ...";
        try
        {
            var found = await RowSearch.FindAsync(source, new DisplayFormatter(grid.Culture), TextSearch.BuildRegex(options), row, column, options.Backward, cts.Token);
            if (found is { } cell)
            {
                grid.FocusCell(cell.Row, cell.Column);
            }
            else
            {
                NotFound(options);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_gridSearch, cts))
            {
                _gridSearch = null;
            }
            _context.Status.SetIdle();
        }
    }
}
