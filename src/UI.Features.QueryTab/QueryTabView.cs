using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Eunomia.Engine.Results;
using Eunomia.UI.Controls.Common.Accessibility;
using Eunomia.UI.Controls.Common.Theming;
using Eunomia.UI.Controls.Grid;
using Eunomia.UI.Controls.SqlEditor;

namespace Eunomia.UI.Features.QueryTab;

/// <summary>
/// Content of a query tab (section 4.7; appendix C §1.11): SQL editor, helpers panel on the right and, below, the results
/// tabset with one grid per result set ("name (Nr × Mc)", tooltip with the SQL, Alt+←/→ between them). Panel sizes are
/// reported to whoever saves them.
/// </summary>
public sealed class QueryTabView : Grid
{
    private readonly ContentControl _helpersHost = new();
    private readonly TextBlock _target = new() { Margin = new Thickness(6, 1), VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
    private readonly Border _targetBar;

    public QueryTabView(double editorHeight, double helpersWidth)
    {
        Editor = new SqlEditorControl();
        Results = new TabControl { Padding = new Thickness(0) }.Named("Results");
        Results.Styles.Add(new Style(x => x.OfType<TabItem>())
        {
            Setters =
            {
                new Setter(TemplatedControl.FontSizeProperty, 11.0),
                new Setter(MinHeightProperty, 20.0),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(6, 1)),
            },
        });

        // Session the query will run on (D9): name and colour above the editor.
        _targetBar = new Border { Child = _target, IsVisible = false };
        ActualThemeVariantChanged += (_, _) => ApplyTargetColor();
        var editorPanel = new DockPanel();
        DockPanel.SetDock(_targetBar, Dock.Top);
        editorPanel.Children.Add(_targetBar);
        editorPanel.Children.Add(Editor);

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions($"*,4,{helpersWidth.ToString(CultureInfo.InvariantCulture)}") };
        var helpersSplitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns };
        helpersSplitter.DragCompleted += (_, _) => HelpersWidthChanged?.Invoke(this, top.ColumnDefinitions[2].ActualWidth);
        SetColumn(helpersSplitter, 1);
        SetColumn(_helpersHost, 2);
        top.Children.Add(editorPanel);
        top.Children.Add(helpersSplitter);
        top.Children.Add(_helpersHost);

        RowDefinitions = new RowDefinitions($"{editorHeight.ToString(CultureInfo.InvariantCulture)},4,*");
        RowDefinitions[0].MinHeight = 40;
        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Rows };
        splitter.DragCompleted += (_, _) => EditorHeightChanged?.Invoke(this, RowDefinitions[0].ActualHeight);
        SetRow(splitter, 1);
        SetRow(Results, 2);
        Children.Add(top);
        Children.Add(splitter);
        Children.Add(Results);
    }

    public SqlEditorControl Editor { get; }

    public TabControl Results { get; }

    /// <summary>Helpers panel (Columns, SQL functions, keywords, Snippets, History).</summary>
    public Control? Helpers
    {
        get => _helpersHost.Content as Control;
        set => _helpersHost.Content = value;
    }

    public event EventHandler<double>? EditorHeightChanged;

    public event EventHandler<double>? HelpersWidthChanged;

    /// <summary>Grids of the result tabs, in order.</summary>
    public IReadOnlyList<DataGridView> ResultGrids => Results.Items.OfType<TabItem>().Select(t => (DataGridView)t.Content!).ToList();

    /// <summary>Grid of the selected result tab.</summary>
    public DataGridControl? ActiveGrid => (Results.SelectedItem as TabItem)?.Content is DataGridView view ? view.Control : null;

    /// <summary>Creates the grid of a new result set (the source keeps filling) and selects the first one.</summary>
    public DataGridView AddResult(string caption, IRowSource source, string sql, uint sessionColor, Action<DataGridView>? setup = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var view = new DataGridView();
        setup?.Invoke(view);
        view.Control.SessionColor = sessionColor;
        view.Control.SetSource(source);
        var item = new TabItem { Header = caption, Content = view };
        ToolTip.SetTip(item, Tooltip(source, sql));
        Results.Items.Add(item);
        if (Results.Items.Count == 1)
        {
            Results.SelectedIndex = 0;
        }
        return view;
    }

    /// <summary>Final caption when the result set ends (rows known).</summary>
    public void SetResultCaption(int index, string caption, string sql)
    {
        if (index >= 0 && index < Results.Items.Count && Results.Items[index] is TabItem item)
        {
            item.Header = caption;
            if (item.Content is DataGridView view && view.Control.Source is { } source)
            {
                ToolTip.SetTip(item, Tooltip(source, sql));
            }
        }
    }

    /// <summary>"c columns × r rows" and the SQL (first 1 KiB), like HeidiSQL's balloon.</summary>
    private static string Tooltip(IRowSource source, string sql) =>
        string.Create(CultureInfo.CurrentCulture, $"{source.Schema.Count} columns × {source.RowCount.Count:N0} rows") + Environment.NewLine +
        (sql.Length > 1024 ? sql[..1024] : sql);

    /// <summary>Forgets the results of the previous run (the sources are released by whoever created them).</summary>
    public void ClearResults() => Results.Items.Clear();

    /// <summary>Alt+← / Alt+→: previous or next result tab.</summary>
    public void SelectResult(int delta)
    {
        if (Results.Items.Count == 0)
        {
            return;
        }
        Results.SelectedIndex = Math.Clamp(Results.SelectedIndex + delta, 0, Results.Items.Count - 1);
    }

    /// <summary>Text of the target session bar (null when hidden).</summary>
    public string? TargetText => _targetBar.IsVisible ? _target.Text : null;

    /// <summary>
    /// Target session (D9): name and colour; <paramref name="pinned"/> says the tab is pinned. The session colour goes in a
    /// bar on the left and as a soft tint on the background of the bar and of the results tabset (like the tree).
    /// </summary>
    public void SetTarget(string? session, uint color, bool pinned)
    {
        _targetColor = color;
        _targetBar.IsVisible = session is not null;
        _target.Text = session is null ? string.Empty : (pinned ? "Pinned to session: " : "Session: ") + session;
        ApplyTargetColor();
    }

    private uint _targetColor;

    private void ApplyTargetColor()
    {
        var color = _targetColor;
        if (color == 0)
        {
            _targetBar.Background = null;
            _targetBar.BorderThickness = default;
            Results.Background = null;
            return;
        }
        var panel = ThemeTokens.Of(ActualThemeVariant).Argb("surface.panel");
        var tint = new SolidColorBrush(Color.FromUInt32(ColorMath.SessionTint(color, panel)));
        _targetBar.Background = tint;
        _targetBar.BorderBrush = new SolidColorBrush(Color.FromUInt32(color | 0xFF000000));
        _targetBar.BorderThickness = new Thickness(3, 0, 0, 0);
        Results.Background = tint;
    }
}
