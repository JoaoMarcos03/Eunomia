namespace Eunomia.UI.Controls.Grid;

/// <summary>Como uma ação de foco altera a seleção.</summary>
public enum SelectMode
{
    /// <summary>Seleciona só a linha focada (clique ou seta sem modificadores).</summary>
    Replace,

    /// <summary>Ctrl+clique: inverte a linha e passa a ser a âncora.</summary>
    Toggle,

    /// <summary>Shift: seleciona da âncora até à linha focada.</summary>
    Extend,

    /// <summary>Move só o foco (Ctrl+setas), sem mudar a seleção.</summary>
    FocusOnly,
}

/// <summary>
/// Seleção do grid igual ao HeidiSQL (secção 4.4, FAM-G05): conjunto de linhas, guardado como ranges
/// (1M linhas selecionadas custam bytes), mais uma célula focada e uma âncora. Sem seleção retangular (D10).
/// </summary>
public sealed class GridSelection
{
    // Ranges [Start, End] inclusivos, ordenados e sem sobreposição nem adjacência.
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

    /// <summary>Texto da status bar: <c>r12 : c3 (5 sel)</c> (base 1).</summary>
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

    /// <summary>Muda só a coluna focada (Home/End, ←/→, Tab).</summary>
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

    /// <summary>Ctrl+I: inverte a seleção em <c>[0, rowCount)</c>.</summary>
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
