using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Eunomia.Engine.Sql.Filters;
using Eunomia.UI.Controls.Common.Accessibility;

namespace Eunomia.UI.Controls.Tree;

/// <summary>
/// Left panel of the window (section 4.3): a toolbar with "Database filter" and "Table filter" at 50/50 and the
/// ★ "Show only favorites" button, and the tree with a scrollbar. Filters apply on every key, without debounce;
/// Esc clears the box; the history (20 per box, global) is saved when the box loses focus.
/// </summary>
public sealed class DatabaseTreeView : DockPanel
{
    private readonly ScrollBar _scrollBar;
    private readonly Grid _filters;
    private bool _syncingScroll;

    public DatabaseTreeView()
    {
        DatabaseFilterBox = CreateFilterBox("Database filter", "A list of databases, separated by semicolon. Can contain regular expressions, e.g. mydb;test.*;project\\d+");
        TableFilterBox = CreateFilterBox("Table filter", "Can contain regular expressions, e.g. phpbb_\\d");
        FavoritesButton = new ToggleButton { Content = "★", Padding = new Avalonia.Thickness(6, 0), VerticalAlignment = VerticalAlignment.Stretch };
        ToolTip.SetTip(FavoritesButton, "Show only favorites");

        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,Auto"), Height = 26 };
        _filters = toolbar;
        Grid.SetColumn(TableFilterBox, 1);
        Grid.SetColumn(FavoritesButton, 2);
        toolbar.Children.Add(DatabaseFilterBox);
        toolbar.Children.Add(TableFilterBox);
        toolbar.Children.Add(FavoritesButton);
        SetDock(toolbar, Dock.Top);
        Children.Add(toolbar);

        _scrollBar = new ScrollBar { Orientation = Orientation.Vertical, AllowAutoHide = false, SmallChange = 1 };
        SetDock(_scrollBar, Dock.Right);
        Children.Add(_scrollBar);

        Tree = new DatabaseTreeControl();
        Children.Add(Tree);

        DatabaseFilterBox.TextChanged += (_, _) => Tree.Model?.SetDatabaseFilter(DatabaseFilterBox.Text ?? string.Empty);
        TableFilterBox.TextChanged += (_, _) => Tree.Model?.SetTableFilter(TableFilterBox.Text ?? string.Empty);
        DatabaseFilterBox.LostFocus += (_, _) => DatabaseHistory.Add(DatabaseFilterBox.Text ?? string.Empty);
        TableFilterBox.LostFocus += (_, _) => TableHistory.Add(TableFilterBox.Text ?? string.Empty);
        FavoritesButton.IsCheckedChanged += (_, _) => Tree.Model?.SetOnlyFavorites(FavoritesButton.IsChecked == true);

        Tree.ScrollChanged += (_, _) => SyncScrollBar();
        _scrollBar.ValueChanged += (_, e) =>
        {
            if (!_syncingScroll)
            {
                Tree.TopRow = (int)Math.Round(e.NewValue);
            }
        };
    }

    public DatabaseTreeControl Tree { get; }

    public TextBox DatabaseFilterBox { get; }

    public TextBox TableFilterBox { get; }

    public ToggleButton FavoritesButton { get; }

    public FilterHistory DatabaseHistory { get; } = new();

    public FilterHistory TableHistory { get; } = new();

    /// <summary>Display › Tree filters: shows or hides the filters toolbar.</summary>
    public bool FiltersVisible
    {
        get => _filters.IsVisible;
        set => _filters.IsVisible = value;
    }

    /// <summary>Ctrl+E: focuses the Table filter (there is no shortcut for the Database filter).</summary>
    public void FocusTableFilter() => TableFilterBox.Focus();

    private static TextBox CreateFilterBox(string placeholder, string hint)
    {
        var box = new TextBox { PlaceholderText = placeholder, MinHeight = 0, Padding = new Avalonia.Thickness(4, 2), VerticalContentAlignment = VerticalAlignment.Center }.Named(placeholder);
        ToolTip.SetTip(box, hint);
        Common.Input.CommandScope.SetContext(box, "treeFilter");
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                box.Text = string.Empty;
                e.Handled = true;
            }
        };
        return box;
    }

    private void SyncScrollBar()
    {
        _syncingScroll = true;
        try
        {
            _scrollBar.Maximum = Tree.MaxTopRow;
            _scrollBar.ViewportSize = Tree.ViewportRows;
            _scrollBar.LargeChange = Math.Max(1, Tree.ViewportRows - 1);
            _scrollBar.Value = Tree.TopRow;
        }
        finally
        {
            _syncingScroll = false;
        }
    }
}
