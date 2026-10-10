using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

namespace Eunomia.UI.Controls.Grid;

/// <summary>Grid com scrollbars vertical e horizontal. A scrollbar vertical trabalha em linhas.</summary>
public sealed class DataGridView : Avalonia.Controls.Grid
{
    private readonly ScrollBar _vertical;
    private readonly ScrollBar _horizontal;
    private bool _syncing;

    public DataGridView()
    {
        RowDefinitions = new RowDefinitions("*,Auto");
        ColumnDefinitions = new ColumnDefinitions("*,Auto");

        Control = new DataGridControl();
        _vertical = new ScrollBar { Orientation = Orientation.Vertical, AllowAutoHide = false, SmallChange = 1 };
        _horizontal = new ScrollBar { Orientation = Orientation.Horizontal, AllowAutoHide = false, SmallChange = 20 };
        SetColumn(_vertical, 1);
        SetRow(_horizontal, 1);
        Children.Add(Control);
        Children.Add(_vertical);
        Children.Add(_horizontal);

        Control.ScrollChanged += (_, _) => Sync();
        _vertical.ValueChanged += (_, e) =>
        {
            if (!_syncing)
            {
                Control.TopRow = (long)Math.Round(e.NewValue);
            }
        };
        _horizontal.ValueChanged += (_, e) =>
        {
            if (!_syncing)
            {
                Control.ScrollX = e.NewValue;
            }
        };
    }

    public DataGridControl Control { get; }

    private void Sync()
    {
        _syncing = true;
        try
        {
            _vertical.Maximum = Control.MaxTopRow;
            _vertical.ViewportSize = Control.ViewportRows;
            _vertical.LargeChange = Math.Max(1, Control.ViewportRows - 1);
            _vertical.Value = Control.TopRow;
            _horizontal.Maximum = Control.MaxScrollX;
            _horizontal.ViewportSize = Control.DataViewportWidth;
            _horizontal.LargeChange = Math.Max(20, Control.DataViewportWidth - 40);
            _horizontal.Value = Control.ScrollX;
        }
        finally
        {
            _syncing = false;
        }
    }
}
