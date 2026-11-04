using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Eunomia.Engine.Sql.Filters;
using Eunomia.UI.Controls.Common.Accessibility;
using Eunomia.UI.Controls.Common.Input;
using Eunomia.UI.Controls.Common.Theming;
using Path = Avalonia.Controls.Shapes.Path;

namespace Eunomia.UI.Controls.Tree;

/// <summary>
/// Left panel of the window (section 4.3): a toolbar with "Database filter" and "Table filter" at 50/50 and the
/// ★ "Show only favorites" button, and the tree with a scrollbar. Filters apply on every key, without debounce;
/// Esc or the clear button (on the right, while there is text) clears the box; the history (20 per box, global) is saved
/// when the box loses focus.
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
        FavoritesButton = CreateFavoritesButton();

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

    /// <summary>
    /// ★ toggle: a drawn star centred in the button (the text glyph sat off centre, its position depends on the font),
    /// an outline in the muted colour when off, and when on the star filled in the favourites colour on a matching
    /// tint, the same star the tree draws next to a favourite (the accent blue of a checked Fluent button read as
    /// "selected", not as "favourites only").
    /// </summary>
    private static ToggleButton CreateFavoritesButton()
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            for (var i = 0; i < 10; i++)
            {
                var radius = i % 2 == 0 ? 6.5 : 2.8;
                var angle = (-Math.PI / 2) + (i * Math.PI / 5);
                var point = new Point(7 + (radius * Math.Cos(angle)), 7.6 + (radius * Math.Sin(angle)));
                if (i == 0)
                {
                    g.BeginFigure(point, true);
                }
                else
                {
                    g.LineTo(point);
                }
            }
            g.EndFigure(true);
        }
        var star = new Path { Data = geometry, Width = 14, Height = 14, StrokeThickness = 1.2, StrokeJoin = PenLineJoin.Round };
        var button = new ToggleButton
        {
            Content = star,
            Width = 28,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        }.Named("Show only favorites");
        ToolTip.SetTip(button, "Show only favorites");
        button.Styles.Add(new Style(x => x.OfType<ToggleButton>().Descendant().OfType<Path>())
        {
            Setters =
            {
                new Setter(Shape.FillProperty, Brushes.Transparent),
                new Setter(Shape.StrokeProperty, new DynamicResourceExtension(ThemeTokens.Key("text.muted"))),
            },
        });
        button.Styles.Add(new Style(x => x.OfType<ToggleButton>().Class(":checked").Descendant().OfType<Path>())
        {
            Setters =
            {
                new Setter(Shape.FillProperty, new DynamicResourceExtension(ThemeTokens.Key("tree.star"))),
                new Setter(Shape.StrokeProperty, new DynamicResourceExtension(ThemeTokens.Key("tree.star"))),
            },
        });
        foreach (var (variant, theme) in new[] { (ThemeVariant.Light, ThemeTokens.Light), (ThemeVariant.Dark, ThemeTokens.Dark) })
        {
            var tint = new SolidColorBrush(theme.Color("list.highlight"));
            var border = new SolidColorBrush(theme.Color("tree.star"));
            button.Resources.ThemeDictionaries[variant] = new ResourceDictionary
            {
                ["ToggleButtonBackgroundChecked"] = tint,
                ["ToggleButtonBackgroundCheckedPointerOver"] = tint,
                ["ToggleButtonBackgroundCheckedPressed"] = tint,
                ["ToggleButtonBorderBrushChecked"] = border,
                ["ToggleButtonBorderBrushCheckedPointerOver"] = border,
                ["ToggleButtonBorderBrushCheckedPressed"] = border,
            };
        }
        return button;
    }

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
        var box = new TextBox { PlaceholderText = placeholder, MinHeight = 0, Padding = new Avalonia.Thickness(4, 2), VerticalContentAlignment = VerticalAlignment.Center }.Named(placeholder).WithClearButton();
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
