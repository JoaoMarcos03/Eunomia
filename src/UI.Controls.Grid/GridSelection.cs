namespace Eunomia.UI.Controls.Grid;

/// <summary>How a focus action changes the selection.</summary>
public enum SelectMode
{
    /// <summary>Selects only the focused row (click or arrow without modifiers).</summary>
    Replace,

    /// <summary>Ctrl+click: toggles the row and makes it the anchor.</summary>
    Toggle,

    /// <summary>Shift: selects from the anchor to the focused row.</summary>
    Extend,

    /// <summary>Moves only the focus (Ctrl+arrows), without changing the selection.</summary>
    FocusOnly,
}

/// <summary>
/// Grid selection as in HeidiSQL (section 4.4, FAM-G05): a set of rows, stored as ranges
/// (1M selected rows cost bytes), plus a focused cell and an anchor. No rectangular selection (D10).
/// </summary>
public sealed class GridSelection
{
    // Inclusive [Start, End] ranges, sorted, without overlap or adjacency.
    private readonly List<(long Start, long End)> _ranges = [];

    public long FocusedRow { get; private set; } = -1;

    public int FocusedColumn { get; private set; }

    public long AnchorRow { get; private set; } = -1;

    public IReadOnlyList<(long Start, long End)> Ranges => _ranges;

    public long Count
    {
        get
        {
            long n = 0;
            foreach (var (s, e) in _ranges)
            {
                n += e - s + 1;
            }
            return n;
        }
    }

    public event EventHandler? Changed;

    /// <summary>Status bar text: <c>r12 : c3 (5 sel)</c> (1-based).</summary>
    public string StatusText => FocusedRow < 0 ? string.Empty : $"r{FocusedRow + 1} : c{FocusedColumn + 1} ({Count} sel)";

    public bool IsSelected(long row)
    {
        int lo = 0, hi = _ranges.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            var (s, e) = _ranges[mid];
            if (row < s)
            {
                hi = mid - 1;
            }
            else if (row > e)
            {
                lo = mid + 1;
            }
            else
            {
                return true;
            }
        }
        return false;
    }

    public void Focus(long row, int column, SelectMode mode = SelectMode.Replace)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        FocusedRow = row;
        FocusedColumn = Math.Max(0, column);
        switch (mode)
        {
            case SelectMode.Replace:
                _ranges.Clear();
                _ranges.Add((row, row));
                AnchorRow = row;
                break;
            case SelectMode.Toggle:
                if (IsSelected(row))
                {
                    Remove(row, row);
                }
                else
                {
                    Add(row, row);
                }
                AnchorRow = row;
                break;
            case SelectMode.Extend:
                if (AnchorRow < 0)
                {
                    AnchorRow = row;
                }
                _ranges.Clear();
                _ranges.Add((Math.Min(AnchorRow, row), Math.Max(AnchorRow, row)));
                break;
            case SelectMode.FocusOnly:
                break;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Changes only the focused column (Home/End, ←/→, Tab).</summary>
    public void FocusColumn(int column)
    {
        FocusedColumn = Math.Max(0, column);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Ctrl+A.</summary>
    public void SelectAll(long rowCount)
    {
        _ranges.Clear();
        if (rowCount > 0)
        {
            _ranges.Add((0, rowCount - 1));
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Ctrl+I: inverts the selection in <c>[0, rowCount)</c>.</summary>
    public void Invert(long rowCount)
    {
        var inverted = new List<(long, long)>();
        long next = 0;
        foreach (var (s, e) in _ranges)
        {
            if (s > next)
            {
                inverted.Add((next, Math.Min(s - 1, rowCount - 1)));
            }
            next = e + 1;
        }
        if (next < rowCount)
        {
            inverted.Add((next, rowCount - 1));
        }
        _ranges.Clear();
        _ranges.AddRange(inverted.Where(r => r.Item1 <= r.Item2));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        _ranges.Clear();
        FocusedRow = -1;
        AnchorRow = -1;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Add(long start, long end)
    {
        _ranges.Add((start, end));
        _ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(long Start, long End)>(_ranges.Count);
        foreach (var r in _ranges)
        {
            if (merged.Count > 0 && r.Start <= merged[^1].End + 1)
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, r.End));
            }
            else
            {
                merged.Add(r);
            }
        }
        _ranges.Clear();
        _ranges.AddRange(merged);
    }

    private void Remove(long start, long end)
    {
        var result = new List<(long, long)>(_ranges.Count + 1);
        foreach (var (s, e) in _ranges)
        {
            if (e < start || s > end)
            {
                result.Add((s, e));
                continue;
            }
            if (s < start)
            {
                result.Add((s, start - 1));
            }
            if (e > end)
            {
                result.Add((end + 1, e));
            }
        }
        _ranges.Clear();
        _ranges.AddRange(result);
    }
}
