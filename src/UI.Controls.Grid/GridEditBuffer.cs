namespace Eunomia.UI.Controls.Grid;

/// <summary>
/// Edited cell value: <see cref="Text"/> <c>null</c> = NULL; with <see cref="IsFunction"/>, an SQL expression that goes
/// as is (Insert value ▸ SQL function, Ctrl+F2).
/// </summary>
public readonly record struct CellValue(string? Text, bool IsFunction = false)
{
    public static CellValue Null => new(null);

    public bool IsNull => Text is null && !IsFunction;
}

/// <summary>Row to save: index in the grid, whether it is new and the changed values per grid column.</summary>
public sealed record GridRowCommit(long Row, bool IsInsert, IReadOnlyDictionary<int, CellValue> Values);

/// <summary>Request for the window editor for a cell: current value and whether it is binary (hex).</summary>
public sealed record GridPopupEditEventArgs(long Row, int Column, string? Value, bool IsBinary);

/// <summary>Result of a save: whether it saved and the values read back from the server (they sit on top of the source).</summary>
public sealed record GridCommitResult(bool Ok, IReadOnlyDictionary<int, CellValue>? Row = null)
{
    public static GridCommitResult Failed { get; } = new(false);
}

/// <summary>
/// Edit buffer kept apart from the blocks (sections 11.5 and 15.1, EditModel): changes accumulate on one row
/// and are saved when moving to another row or with Ctrl+Enter; moving to another column on the same row does not save
/// (FAM-G08). New rows (↓ on the last row) are appended at the end. In the spike, "saving" moves the values into an
/// overlay of saved values (there is no server) and raises <see cref="RowCommitted"/>.
/// </summary>
public sealed class GridEditBuffer
{
    private readonly Dictionary<int, CellValue> _pending = [];
    private readonly Dictionary<long, Dictionary<int, CellValue>> _committed = [];

    /// <summary>Row with unsaved changes, or -1.</summary>
    public long PendingRow { get; private set; } = -1;

    /// <summary>New rows appended after the source rows.</summary>
    public long InsertedRows { get; private set; }

    /// <summary>The pending row is a new row not yet saved.</summary>
    public bool PendingIsInsert { get; private set; }

    public bool HasPending => PendingRow >= 0;

    public event EventHandler<long>? RowCommitted;

    /// <summary>Edited value (pending or saved) of the cell, if any.</summary>
    public bool TryGetValue(long row, int column, out CellValue value)
    {
        if (row == PendingRow && _pending.TryGetValue(column, out value))
        {
            return true;
        }
        if (_committed.TryGetValue(row, out var cells) && cells.TryGetValue(column, out value))
        {
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>Unsaved values of the pending row, per column.</summary>
    public IReadOnlyDictionary<int, CellValue> PendingValues => _pending;

    /// <summary>Already saved value (not the pending one) of the cell, if any.</summary>
    public bool TryGetCommitted(long row, int column, out CellValue value)
    {
        if (_committed.TryGetValue(row, out var cells) && cells.TryGetValue(column, out value))
        {
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>Value saved to the server and read back, on top of the source (until the grid changes data).</summary>
    public void SetCommitted(long row, int column, CellValue value)
    {
        if (!_committed.TryGetValue(row, out var cells))
        {
            cells = [];
            _committed[row] = cells;
        }
        cells[column] = value;
    }

    /// <summary>Cell with an unsaved change (red triangle).</summary>
    public bool IsModified(long row, int column) => row == PendingRow && _pending.ContainsKey(column);

    public void SetValue(long row, int column, CellValue value)
    {
        if (PendingRow >= 0 && PendingRow != row)
        {
            throw new InvalidOperationException("There are unsaved changes on another row; call Commit() first.");
        }
        PendingRow = row;
        _pending[column] = value;
    }

    /// <summary>Appends a new row at index <paramref name="sourceRows"/> + the rows already inserted.</summary>
    public long InsertRow(long sourceRows)
    {
        if (HasPending)
        {
            throw new InvalidOperationException("Save the pending row before inserting.");
        }
        var row = sourceRows + InsertedRows;
        InsertedRows++;
        PendingRow = row;
        PendingIsInsert = true;
        return row;
    }

    /// <summary>Saves the pending row. Returns <c>false</c> if there was nothing to save.</summary>
    public bool Commit()
    {
        if (!HasPending)
        {
            return false;
        }
        var row = PendingRow;
        if (!_committed.TryGetValue(row, out var cells))
        {
            cells = [];
            _committed[row] = cells;
        }
        foreach (var (column, value) in _pending)
        {
            cells[column] = value;
        }
        ClearPending();
        RowCommitted?.Invoke(this, row);
        return true;
    }

    /// <summary>Forgets everything (pending, saved in memory and new rows): the grid changed data.</summary>
    public void Reset()
    {
        ClearPending();
        _committed.Clear();
        InsertedRows = 0;
    }

    /// <summary>Esc outside editing: discards the row's changes (and the row, if it is new).</summary>
    public void Discard()
    {
        if (PendingIsInsert)
        {
            InsertedRows--;
        }
        ClearPending();
    }

    private void ClearPending()
    {
        _pending.Clear();
        PendingRow = -1;
        PendingIsInsert = false;
    }
}
