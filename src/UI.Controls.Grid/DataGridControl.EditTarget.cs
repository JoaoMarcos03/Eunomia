using Avalonia.Controls;
using Avalonia.Input.Platform;
using Eunomia.UI.Controls.Common.Input;

namespace Eunomia.UI.Controls.Grid;

/// <summary>
/// Edit menu actions in the grid (section 4.6; appendix D B17): Copy copies the focused cell; Cut copies and empties it; Paste
/// puts the whole clipboard text in the focused cell (NULL if the last copy was a NULL and the clipboard is empty); Select all
/// selects the rows.
/// </summary>
public sealed partial class DataGridControl
{
    bool IEditTarget.CanCopy => Source is not null;

    bool IEditTarget.CanCut => CanWriteFocusedCell;

    bool IEditTarget.CanPaste => CanWriteFocusedCell;

    bool IEditTarget.CanSelectAll => RowCount > 0;

    bool IEditTarget.CanUndo => false;

    private bool CanWriteFocusedCell => Source is not null && !IsReadOnly && Selection.FocusedRow >= 0 && Selection.FocusedColumn >= 0;

    Task IEditTarget.CopyAsync() => CopyFocusedCellAsync();

    async Task IEditTarget.CutAsync()
    {
        await CopyFocusedCellAsync().ConfigureAwait(true);
        await SetCellValueAsync(Selection.FocusedRow, Selection.FocusedColumn, new CellValue(string.Empty)).ConfigureAwait(true);
    }

    async Task IEditTarget.PasteAsync()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }
        var text = await clipboard.TryGetTextAsync().ConfigureAwait(true);
        var value = string.IsNullOrEmpty(text) && _clipboardIsNull ? CellValue.Null : new CellValue(text ?? string.Empty);
        await SetCellValueAsync(Selection.FocusedRow, Selection.FocusedColumn, value).ConfigureAwait(true);
    }

    void IEditTarget.SelectAll() => Execute(GridCommand.SelectAll);

    void IEditTarget.Undo()
    {
    }
}
