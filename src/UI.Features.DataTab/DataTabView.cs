using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Eunomia.App.Data;
using Eunomia.UI.Controls.Common.Accessibility;
using Eunomia.UI.Controls.Common.Text;
using Eunomia.UI.Controls.Common.Theming;
using Eunomia.UI.Controls.Grid;
using Eunomia.UI.Controls.SqlEditor;

namespace Eunomia.UI.Features.DataTab;

/// <summary>
/// Data tab (section 4.4; appendix D B1): top label ("db.table: 272 rows total (exact)", "Query exact row count" menu), on
/// the right Next, Show all, Sorting (n), Columns (v/t) and Filter, the filter panel and the grid. The view only shows
/// and reports; its user (the shell) loads the data and saves the per-table state.
/// </summary>
public sealed class DataTabView : DockPanel
{

    public DataTabView()
    {
        Label = new TextBlock { Margin = new Thickness(4, 3), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var exactItem = new MenuItem { Header = "Query exact row count", ToggleType = MenuItemToggleType.CheckBox };
        exactItem.Click += (_, _) =>
        {
            QueryExactCount = !QueryExactCount;
            exactItem.IsChecked = QueryExactCount;
            ExactCountChanged?.Invoke(this, EventArgs.Empty);
        };
        Label.ContextMenu = new ContextMenu { ItemsSource = new[] { exactItem } };

        NextButton = ToolButton("Next", "Ctrl+Alt+PgDn: load next rows");
        ShowAllButton = ToolButton("Show all", "Ctrl+Alt+End: load all rows (up to the maximum)");
        SortingButton = ToolButton("Sorting", "Sort the rows on the server");
        ColumnsButton = ToolButton("Columns", "Choose visible columns");
        FilterButton = ToolButton("Filter", "Filter rows with a WHERE clause");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(0, 1, 2, 1) };
        buttons.Children.Add(NextButton);
        buttons.Children.Add(ShowAllButton);
        buttons.Children.Add(SortingButton);
        buttons.Children.Add(ColumnsButton);
        buttons.Children.Add(FilterButton);

        var top = new DockPanel();
        SetDock(buttons, Dock.Right);
        top.Children.Add(buttons);
        top.Children.Add(Label);
        SetDock(top, Dock.Top);
        Children.Add(top);

        FilterPanel = new Border { IsVisible = false, Child = BuildFilterPanel() };
        SetDock(FilterPanel, Dock.Top);
        Children.Add(FilterPanel);
        FilterButton.Click += (_, _) =>
        {
            FilterPanel.IsVisible = !FilterPanel.IsVisible;
            if (FilterPanel.IsVisible)
            {
                FilterEditor.Editor.TextArea.Focus();
            }
        };

        Grid = new DataGridView();
        Children.Add(Grid);
    }

    public TextBlock Label { get; }

    public Button NextButton { get; }

    public Button ShowAllButton { get; }

    public Button SortingButton { get; }

    public Button ColumnsButton { get; }

    public Button FilterButton { get; }

    /// <summary>WHERE filter panel (hidden until used).</summary>
    public Border FilterPanel { get; }

    private DockPanel? _recentRow;

    /// <summary>Editor of the text pasted after <c>WHERE</c> (appendix D B9).</summary>
    public SqlEditorControl FilterEditor { get; private set; } = null!;

    /// <summary>"Recent filters": 20 per table, the most recent first; choosing one does not apply it.</summary>
    public ComboBox RecentFilters { get; private set; } = null!;

    /// <summary>"Create multi column filter:" (F7): rewrites the filter on every key; Enter applies.</summary>
    public TextBox MultiColumnBox { get; private set; } = null!;

    public Button ApplyFilterButton { get; private set; } = null!;

    public Button ClearFilterButton { get; private set; } = null!;

    /// <summary>Apply filter (button, F9 in the editor or Enter in the multi-column box).</summary>
    public event EventHandler? ApplyFilterRequested;

    public event EventHandler? ClearFilterRequested;

    /// <summary>The multi-column filter text changed (the shell rewrites the filter).</summary>
    public event EventHandler<string>? MultiColumnTextChanged;

    private Control BuildFilterPanel()
    {
        FilterEditor = new SqlEditorControl { MinHeight = 34 };
        FilterEditor.ShowLineNumbers = false;
        FilterEditor.Editor.TextChanged += (_, _) => FitFilterEditor();
        RecentFilters = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, IsVisible = false, PlaceholderText = "Recent filters" }.Named("Recent filters");
        RecentFilters.SelectionChanged += (_, _) =>
        {
            if (RecentFilters.SelectedItem is RecentFilter recent)
            {
                // Choosing a recent filter only puts it in the editor (appendix D B9).
                FilterEditor.Text = recent.Text;
            }
        };
        MultiColumnBox = new TextBox { MinHeight = 22, Padding = new Thickness(4, 1) }.Named("Create multi column filter");
        MultiColumnBox.TextChanged += (_, _) => MultiColumnTextChanged?.Invoke(this, MultiColumnBox.Text ?? string.Empty);
        MultiColumnBox.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                ApplyFilterRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
        };
        ApplyFilterButton = new Button { Content = "Apply filter", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        ApplyFilterButton.Click += (_, _) => ApplyFilterRequested?.Invoke(this, EventArgs.Empty);
        ClearFilterButton = new Button { Content = "Clear", MinWidth = 60, HorizontalContentAlignment = HorizontalAlignment.Center };
        ClearFilterButton.Click += (_, _) => ClearFilterRequested?.Invoke(this, EventArgs.Empty);

        var left = new DockPanel { Margin = new Thickness(2) };
        var recentRow = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
        var recentLabel = new TextBlock { Text = "Recent filters:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        SetDock(recentLabel, Dock.Left);
        recentRow.Children.Add(recentLabel);
        recentRow.Children.Add(RecentFilters);
        recentRow.IsVisible = false;
        _recentRow = recentRow;
        SetDock(recentRow, Dock.Top);
        left.Children.Add(recentRow);
        left.Children.Add(Themed.Bind(new Border { BorderThickness = new Thickness(1), Child = FilterEditor }, Border.BorderBrushProperty, "border"));

        var right = new StackPanel { Width = 260, Spacing = 4, Margin = new Thickness(4, 2) };
        right.Children.Add(new TextBlock { Text = "Create multi column filter:" });
        right.Children.Add(MultiColumnBox);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ApplyFilterButton);
        buttons.Children.Add(ClearFilterButton);
        right.Children.Add(buttons);

        var panel = new DockPanel();
        SetDock(right, Dock.Right);
        panel.Children.Add(right);
        panel.Children.Add(left);
        FitFilterEditor();
        return panel;
    }

    /// <summary>Automatic editor height: 2 to 8 lines (section 5.3).</summary>
    private void FitFilterEditor()
    {
        var lines = Math.Clamp(FilterEditor.Editor.Document.LineCount, 2, 8);
        FilterEditor.Height = (lines * 16) + 4;
    }

    /// <summary>The table's recent filters; the combo box stays hidden if there are none.</summary>
    public void SetRecentFilters(IReadOnlyList<string> filters)
    {
        ArgumentNullException.ThrowIfNull(filters);
        RecentFilters.ItemsSource = filters.Select(f => new RecentFilter(f)).ToList();
        RecentFilters.SelectedItem = null;
        RecentFilters.IsVisible = filters.Count > 0;
        _recentRow!.IsVisible = filters.Count > 0;
    }

    /// <summary>Filter button highlighted when a filter is active.</summary>
    public void SetFilterActive(bool active) => FilterButton.FontWeight = active ? FontWeight.Bold : FontWeight.Normal;

    /// <summary>A recent filter; the text shown collapses whitespace and is cut at 50 characters.</summary>
    public sealed record RecentFilter(string Text)
    {
        public override string ToString()
        {
            var collapsed = string.Join(' ', Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return collapsed.Length <= 50 ? collapsed : collapsed[..49] + "…";
        }
    }

    public DataGridView Grid { get; }

    /// <summary>"Query exact row count" in the label menu (<c>COUNT(*)</c> instead of the approximate count).</summary>
    public bool QueryExactCount { get; set; }

    public event EventHandler? ExactCountChanged;

    private static Button ToolButton(string caption, string hint)
    {
        var button = new Button
        {
            Content = caption,
            MinWidth = 72,
            Padding = new Thickness(6, 1),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Name = "btn" + caption.Replace(" ", string.Empty, StringComparison.Ordinal),
        };
        ToolTip.SetTip(button, hint);
        return button;
    }

    /// <summary>Next and Show all enabled only if the last load brought everything it asked for and there is room up to the maximum.</summary>
    public void SetPaging(bool canLoadMore)
    {
        NextButton.IsEnabled = canLoadMore;
        ShowAllButton.IsEnabled = canLoadMore;
    }

    /// <summary>"Sorting" or "Sorting (n)".</summary>
    public void SetSortCount(int count) =>
        SortingButton.Content = count > 0 ? $"Sorting ({count.ToString(CultureInfo.CurrentCulture)})" : "Sorting";

    /// <summary>"Columns (visible/total)".</summary>
    public void SetColumns(int visible, int total) =>
        ColumnsButton.Content = string.Create(CultureInfo.CurrentCulture, $"Columns ({visible}/{total})");

    /// <summary>
    /// "Sorting" dialog (appendix D B7): one row per column with the direction and delete; Reset, Add column, OK and Cancel.
    /// OK returns the new sort order.
    /// </summary>
    public void ShowSortingPopup(IReadOnlyList<string> columns, IReadOnlyList<DataSort> current, Action<IReadOnlyList<DataSort>> apply)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(apply);
        var items = current.ToList();
        var rows = new StackPanel { Spacing = 2 };
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedRight };

        void Render()
        {
            rows.Children.Clear();
            for (var i = 0; i < items.Count; i++)
            {
                var index = i;
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                row.Children.Add(new TextBlock { Text = (i + 1).ToString(CultureInfo.CurrentCulture) + ".", Width = 22, VerticalAlignment = VerticalAlignment.Center });
                var combo = new ComboBox { ItemsSource = columns, SelectedItem = columns.FirstOrDefault(c => string.Equals(c, items[i].Column, StringComparison.OrdinalIgnoreCase)), Width = 180 }
                    .Named("Sort column " + (i + 1).ToString(CultureInfo.CurrentCulture));
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedItem is string column)
                    {
                        items[index] = items[index] with { Column = column };
                    }
                };
                var direction = new Button { Content = items[i].Descending ? "DESC" : "ASC", Width = 52 };
                direction.Click += (_, _) =>
                {
                    items[index] = items[index] with { Descending = !items[index].Descending };
                    direction.Content = items[index].Descending ? "DESC" : "ASC";
                };
                var delete = new Button { Content = "×", Width = 26 };
                ToolTip.SetTip(delete, "Remove");
                delete.Click += (_, _) =>
                {
                    items.RemoveAt(index);
                    Render();
                };
                row.Children.Add(combo);
                row.Children.Add(direction);
                row.Children.Add(delete);
                rows.Children.Add(row);
            }
            if (items.Count == 0)
            {
                rows.Children.Add(new TextBlock { Text = "No sorting.", Foreground = Brushes.Gray, Margin = new Thickness(2, 4) });
            }
        }

        var reset = new Button { Content = "Reset" };
        reset.Click += (_, _) =>
        {
            items.Clear();
            Render();
        };
        var add = new Button { Content = "Add column" };
        add.Click += (_, _) =>
        {
            var unused = columns.FirstOrDefault(c => !items.Any(s => string.Equals(s.Column, c, StringComparison.OrdinalIgnoreCase)));
            if (unused is not null)
            {
                items.Add(new DataSort(unused, false));
                Render();
            }
        };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 60 };
        ok.Click += (_, _) =>
        {
            flyout.Hide();
            apply(items.ToList());
        };
        var cancel = new Button { Content = "Cancel", MinWidth = 60 };
        cancel.Click += (_, _) => flyout.Hide();
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 6, 0, 0) };
        bar.Children.Add(reset);
        bar.Children.Add(add);
        bar.Children.Add(ok);
        bar.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(4) };
        panel.Children.Add(rows);
        panel.Children.Add(bar);
        Render();
        flyout.Content = panel;
        flyout.ShowAt(SortingButton);
    }

    /// <summary>
    /// "Columns (v/t)" (appendix D B8): a checked list of every column, check all, filter and "Sort alphabetically";
    /// OK returns the hidden columns.
    /// </summary>
    public void ShowColumnsPopup(IReadOnlyList<string> columns, IReadOnlyCollection<string> hidden, Action<IReadOnlyList<string>> apply)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(hidden);
        ArgumentNullException.ThrowIfNull(apply);
        var visible = columns.ToDictionary(c => c, c => !hidden.Contains(c, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        var list = new StackPanel();
        var filter = new TextBox { PlaceholderText = "Filter", MinHeight = 22, Padding = new Thickness(4, 1) }.Named("Filter columns");
        var all = new CheckBox { Content = "Select / Deselect all", IsThreeState = true };
        var sorted = new CheckBox { Content = "Sort alphabetically" };
        var updatingAll = false;

        void UpdateAll()
        {
            updatingAll = true;
            all.IsChecked = visible.Values.All(v => v) ? true : visible.Values.Any(v => v) ? null : false;
            updatingAll = false;
        }

        void Render()
        {
            list.Children.Clear();
            var text = filter.Text ?? string.Empty;
            var shown = columns.Where(c => c.Contains(text, StringComparison.OrdinalIgnoreCase));
            if (sorted.IsChecked == true)
            {
                shown = shown.Order(StringComparer.CurrentCultureIgnoreCase);
            }
            foreach (var column in shown)
            {
                var box = new CheckBox { Content = LiteralText.Of(column), IsChecked = visible[column] }.Named(column);
                box.IsCheckedChanged += (_, _) =>
                {
                    visible[column] = box.IsChecked == true;
                    UpdateAll();
                };
                list.Children.Add(box);
            }
        }

        all.IsCheckedChanged += (_, _) =>
        {
            if (updatingAll)
            {
                return;
            }
            var value = all.IsChecked != false;
            foreach (var key in visible.Keys.ToList())
            {
                visible[key] = value;
            }
            UpdateAll();
            Render();
        };
        filter.TextChanged += (_, _) => Render();
        sorted.IsCheckedChanged += (_, _) => Render();
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 60 };
        ok.Click += (_, _) =>
        {
            flyout.Hide();
            apply(columns.Where(c => !visible[c]).ToList());
        };
        var cancel = new Button { Content = "Cancel", MinWidth = 60 };
        cancel.Click += (_, _) => flyout.Hide();
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        bar.Children.Add(ok);
        bar.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(4), Spacing = 4, Width = 260 };
        panel.Children.Add(all);
        panel.Children.Add(filter);
        panel.Children.Add(new ScrollViewer { Content = list, MaxHeight = 320 });
        panel.Children.Add(sorted);
        panel.Children.Add(bar);
        UpdateAll();
        Render();
        flyout.Content = panel;
        flyout.ShowAt(ColumnsButton);
    }
}
