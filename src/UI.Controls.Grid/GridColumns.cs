namespace Eunomia.UI.Controls.Grid;

/// <summary>
/// Widths of the data columns (GridModel of section 15.1) with prefix sums, to find the
/// visible columns by binary search (section 15.2). Does not include the <c>#</c> column, which is fixed.
/// </summary>
public sealed class GridColumns
{
    /// <summary>Automatic width limit (<c>MaxColWidth</c>, section 4.4).</summary>
    public const double MaxAutoWidth = 300;
    public const double MinWidth = 24;

    private double[] _widths = [];
    private double[] _offsets = [0];

    public int Count => _widths.Length;

    public double TotalWidth => _offsets[^1];

    public double WidthOf(int column) => _widths[column];

    /// <summary>x position of the start of the column, relative to the start of the data area.</summary>
    public double OffsetOf(int column) => _offsets[column];

    public void SetWidths(ReadOnlySpan<double> widths)
    {
        _widths = widths.ToArray();
        for (var i = 0; i < _widths.Length; i++)
        {
            _widths[i] = Math.Max(MinWidth, _widths[i]);
        }
        Recompute();
    }

    public void SetWidth(int column, double width)
    {
        _widths[column] = Math.Max(MinWidth, width);
        Recompute();
    }

    /// <summary>Column at position x (relative to the start of the data area), or -1 outside the columns.</summary>
    public int ColumnAt(double x)
    {
        if (x < 0 || x >= TotalWidth || _widths.Length == 0)
        {
            return -1;
        }
        // Largest i with _offsets[i] <= x.
        var index = Array.BinarySearch(_offsets, x);
        if (index < 0)
        {
            index = ~index - 1;
        }
        return Math.Min(index, _widths.Length - 1);
    }

    /// <summary>Columns that intersect <c>[scrollX, scrollX + viewportWidth)</c>.</summary>
    public (int First, int Last) VisibleRange(double scrollX, double viewportWidth)
    {
        if (_widths.Length == 0)
        {
            return (0, -1);
        }
        var first = Math.Max(0, ColumnAt(Math.Max(0, scrollX)));
        var last = ColumnAt(Math.Min(TotalWidth - 0.001, scrollX + viewportWidth - 0.001));
        if (last < 0)
        {
            last = _widths.Length - 1;
        }
        return (first, last);
    }

    private void Recompute()
    {
        _offsets = new double[_widths.Length + 1];
        for (var i = 0; i < _widths.Length; i++)
        {
            _offsets[i + 1] = _offsets[i] + _widths[i];
        }
    }
}
