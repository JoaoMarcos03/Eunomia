using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Eunomia.App.Settings;
using Eunomia.UI.Controls.Common.Accessibility;
using Eunomia.UI.Controls.Common.Icons;

namespace Eunomia.UI.Shell.Views;

/// <summary>
/// A query tab: caption ("Query", "Query #2"), <c>*</c> when modified, and content. The content (editor,
/// helpers and results) is only created when the tab is shown or used for the first time (lighter startup, PR-01).
/// </summary>
public sealed class QueryTab
{
    private readonly TextBlock _caption;
    private readonly Border _session;
    private readonly Func<Control> _factory;
    private Control? _content;

    internal QueryTab(TabItem item, TextBlock caption, Border session, Func<Control> factory, bool closable)
    {
        Item = item;
        _caption = caption;
        _session = session;
        _factory = factory;
        IsClosable = closable;
    }

    /// <summary>Session the tab runs on (D9): a bar with the colour and a tooltip with the name; "(pinned)" when pinned.</summary>
    public void SetSession(string? session, uint color, bool pinned)
    {
        _session.IsVisible = session is not null && (color != 0 || pinned);
        _session.Background = color != 0 ? new SolidColorBrush(Color.FromUInt32(color)) : Brushes.Gray;
        _session.Width = pinned ? 6 : 4;
        ToolTip.SetTip(Item, session is null ? null : pinned ? $"Pinned to session: {session}" : $"Session: {session}");
    }

    public TabItem Item { get; }

    public Control Content
    {
        get
        {
            if (_content is null)
            {
                _content = _factory();
                Item.Content = _content;
            }
            return _content;
        }
    }

    public bool HasContent => _content is not null;

    public bool IsClosable { get; }

    public string Name { get; private set; } = string.Empty;

    public bool IsModified { get; private set; }

    public void SetCaption(string name, bool modified)
    {
        Name = name;
        IsModified = modified;
        _caption.Text = modified ? name + " *" : name;
    }
}

/// <summary>
/// Main tabs (section 5.3, appendix C §1.8, FAM-L04/L05): Host, Database, Table, Data, Query and Query #N, with
/// a close button on the query tabs, "+" after the last one, closing by double click or middle click (preferences)
/// and several rows when they do not fit.
/// </summary>
public sealed class MainTabsView : TabControl
{
    private readonly TabOptions _options;
    private readonly TabItem _plus;
    private readonly List<QueryTab> _queryTabs = [];
    private readonly Func<Control> _queryContentFactory;
    private int _nextQueryNumber = 2;

    public MainTabsView(TabOptions options, Func<Control> queryContentFactory)
    {
        _options = options;
        _queryContentFactory = queryContentFactory;
        this.Named("Main tabs");
        Padding = new Thickness(0);
        ItemsPanel = new FuncTemplate<Panel?>(() => options.MultipleLines ? new WrapPanel() : new StackPanel { Orientation = Orientation.Horizontal });

        HostTab = Fixed("Host", "session-manager");
        DatabaseTab = Fixed("Database", "database");
        TableTab = Fixed("Table", "table");
        DataTab = Fixed("Data", "table");
        TableTab.IsVisible = false;
        _plus = new TabItem { Header = Header("+", null, null), Name = "tabPlus", Padding = new Thickness(6, 2) };
        ToolTip.SetTip(_plus, "New query tab");
        Items.Add(HostTab);
        Items.Add(DatabaseTab);
        Items.Add(TableTab);
        Items.Add(DataTab);
        AddQueryTab(select: false, closable: false, name: "Query");
        Items.Add(_plus);
        SelectionChanged += OnSelectionChanged;
        DoubleTapped += OnDoubleTapped;
        ContextRequested += OnContextRequested;
    }

    protected override Type StyleKeyOverride => typeof(TabControl);

    public TabItem HostTab { get; }

    public TabItem DatabaseTab { get; }

    public TabItem TableTab { get; }

    public TabItem DataTab { get; }

    public IReadOnlyList<QueryTab> QueryTabs => _queryTabs;

    public QueryTab? ActiveQueryTab => _queryTabs.FirstOrDefault(q => ReferenceEquals(q.Item, SelectedItem));

    /// <summary>Query tab under the mouse when the tab bar context menu opened (the actions act on it).</summary>
    public QueryTab? ContextTab { get; set; }

    /// <summary>Target tab of the actions: the context menu's, if any, otherwise the active one.</summary>
    public QueryTab? TargetQueryTab => ContextTab ?? ActiveQueryTab;

    /// <summary>Tabs in multiple lines (popupMainTabs).</summary>
    public void SetMultipleLines(bool value)
    {
        _options.MultipleLines = value;
        ItemsPanel = new FuncTemplate<Panel?>(() => value ? new WrapPanel() : new StackPanel { Orientation = Orientation.Horizontal });
    }

    /// <summary>Close query tabs to the right (stops if the user cancels one).</summary>
    public async Task CloseQueryTabsRightOfAsync(QueryTab tab)
    {
        var index = Items.IndexOf(tab.Item);
        foreach (var other in _queryTabs.Where(q => q.IsClosable && Items.IndexOf(q.Item) > index).ToList())
        {
            if (!await RequestCloseAsync(other))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Asks before closing (changed tab: "Save changes to file …?" / "Save content of tab …?"); returns false if the
    /// user cancels.
    /// </summary>
    public Func<QueryTab, Task<bool>>? ConfirmClose { get; set; }

    /// <summary>Close requested by the user: asks (if needed) and closes.</summary>
    public async Task<bool> RequestCloseAsync(QueryTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (!tab.IsClosable || !_queryTabs.Contains(tab) || CanCloseQueryTab?.Invoke(tab) == false)
        {
            return false;
        }
        if (ConfirmClose is { } confirm && !await confirm(tab))
        {
            return false;
        }
        CloseQueryTab(tab);
        return true;
    }

    public event EventHandler<QueryTab>? QueryTabClosing;

    /// <summary>Close veto (a tab with a running query does not close, appendix C §4.3).</summary>
    public Func<QueryTab, bool>? CanCloseQueryTab { get; set; }

    public QueryTab AddQueryTab(bool select = true, bool closable = true, string? name = null)
    {

        var caption = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        var session = new Border { Width = 4, Height = 14, CornerRadius = new CornerRadius(1), VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
        var item = new TabItem { Padding = new Thickness(8, 2, 4, 2), FontSize = 12, MinHeight = 24 };
        var tab = new QueryTab(item, caption, session, _queryContentFactory, closable);
        Button? close = null;
        if (closable)
        {
            close = new Button
            {
                Content = IconFactory.CreateImage("close-tab"),
                Padding = new Thickness(0),
                Width = 16,
                Height = 16,
                Margin = new Thickness(4, 0, 0, 0),
                Focusable = false,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
            };
            ToolTip.SetTip(close, "Close query tab");
            close.Click += (_, e) =>
            {
                e.Handled = true;
                _ = RequestCloseAsync(tab);
            };
        }
        var header = Header(null, "run", caption, close);
        header.Children.Insert(header.Children.IndexOf(caption), session);
        item.Header = header;
        item.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Middle && _options.CloseOnMiddleClick && tab.IsClosable)
            {
                e.Handled = true;
                _ = RequestCloseAsync(tab);
            }
        };
        tab.SetCaption(name ?? $"Query #{_nextQueryNumber++}", modified: false);
        var index = _plus is null || !Items.Contains(_plus) ? Items.Count : Items.IndexOf(_plus);
        Items.Insert(index, item);
        _queryTabs.Add(tab);
        if (select)
        {
            SelectedItem = item;
        }
        return tab;
    }

    private readonly List<TabItem> _diagramTabs = [];

    /// <summary>Diagram tabs ("Diagram: database", section 17), between the query tabs and the "+".</summary>
    public IReadOnlyList<TabItem> DiagramTabs => _diagramTabs;

    public event EventHandler<TabItem>? DiagramTabClosed;

    /// <summary>Diagram tab with the session colour in the header and a close button.</summary>
    public TabItem AddDiagramTab(string caption, Control content, uint sessionColor)
    {
        ArgumentNullException.ThrowIfNull(content);
        var item = new TabItem { Padding = new Thickness(8, 2, 4, 2), FontSize = 12, MinHeight = 24, Content = content };
        var close = new Button
        {
            Content = IconFactory.CreateImage("close-tab"),
            Padding = new Thickness(0),
            Width = 16,
            Height = 16,
            Margin = new Thickness(4, 0, 0, 0),
            Focusable = false,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
        };
        ToolTip.SetTip(close, "Close diagram");
        close.Click += (_, e) =>
        {
            e.Handled = true;
            CloseDiagramTab(item);
        };
        var header = Header(caption, "diagram", null, close);
        if (sessionColor != 0)
        {
            header.Children.Insert(0, new Border { Width = 4, Height = 14, CornerRadius = new CornerRadius(1), VerticalAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(Color.FromUInt32(sessionColor | 0xFF000000)) });
        }
        item.Header = header;
        item.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Middle && _options.CloseOnMiddleClick)
            {
                e.Handled = true;
                CloseDiagramTab(item);
            }
        };
        Items.Insert(Items.IndexOf(_plus), item);
        _diagramTabs.Add(item);
        SelectedItem = item;
        return item;
    }

    public void CloseDiagramTab(TabItem item)
    {
        if (!_diagramTabs.Remove(item))
        {
            return;
        }
        var index = Items.IndexOf(item);
        var wasSelected = ReferenceEquals(SelectedItem, item);
        Items.Remove(item);
        DiagramTabClosed?.Invoke(this, item);
        if (wasSelected)
        {
            SelectedIndex = Math.Max(0, Math.Min(index - 1, Items.Count - 2));
        }
    }

    public void CloseQueryTab(QueryTab tab)
    {
        if (!tab.IsClosable || !_queryTabs.Contains(tab) || CanCloseQueryTab?.Invoke(tab) == false)
        {
            return;
        }
        QueryTabClosing?.Invoke(this, tab);
        var index = Items.IndexOf(tab.Item);
        var wasSelected = ReferenceEquals(SelectedItem, tab.Item);
        _queryTabs.Remove(tab);
        Items.Remove(tab.Item);
        if (wasSelected)
        {
            // As in HeidiSQL: the tab to the left of the closed one becomes active.
            SelectedIndex = Math.Max(0, Math.Min(index - 1, Items.Count - 2));
        }
    }

    public async Task CloseAllQueryTabsAsync()
    {
        foreach (var tab in _queryTabs.Where(t => t.IsClosable).ToList())
        {
            if (!await RequestCloseAsync(tab))
            {
                return;
            }
        }
    }

    /// <summary>Visible tabs, in order (for Ctrl+1…5 and Ctrl+Tab), without the "+".</summary>
    public IReadOnlyList<TabItem> VisibleTabs =>
        Items.OfType<TabItem>().Where(t => t.IsVisible && !ReferenceEquals(t, _plus)).ToList();

    public void SelectVisible(int index)
    {
        var tabs = VisibleTabs;
        if (index >= 0 && index < tabs.Count)
        {
            SelectedItem = tabs[index];
        }
    }

    public void SelectRelative(int delta)
    {
        var tabs = VisibleTabs;
        if (tabs.Count == 0)
        {
            return;
        }
        var current = SelectedItem is TabItem t ? tabs.ToList().IndexOf(t) : 0;
        SelectedItem = tabs[((current + delta) % tabs.Count + tabs.Count) % tabs.Count];
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ActiveQueryTab is { } active)
        {
            _ = active.Content;
        }
        if (ReferenceEquals(SelectedItem, _plus))
        {
            AddQueryTab();
        }
    }

    /// <summary>Tab bar context menu (popupMainTabs); created on demand by the shell.</summary>
    public Func<ContextMenu>? HeaderMenuFactory { get; set; }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        // Only on the headers: the tabs' content (grid, editor) has its own menus.
        if (HeaderMenuFactory is null || e.Source is not Visual source || FindTabItem(source) is not { } item || !IsInHeader(source, item))
        {
            return;
        }
        ContextTab = _queryTabs.FirstOrDefault(q => ReferenceEquals(q.Item, item));
        var menu = HeaderMenuFactory();
        menu.Closed += (_, _) => ContextTab = null;
        menu.Open(item);
        e.Handled = true;
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Double click on a query tab closes it (preference); on the empty space to the right of the tabs it opens a new one.
        if (e.Source is Visual source && FindTabItem(source) is { } item)
        {
            var tab = _queryTabs.FirstOrDefault(q => ReferenceEquals(q.Item, item));
            if (tab is not null && _options.CloseOnDoubleClick && tab.IsClosable && IsInHeader(source, item))
            {
                _ = RequestCloseAsync(tab);
                e.Handled = true;
            }
        }
    }

    private static bool IsInHeader(Visual source, TabItem item)
    {
        for (var v = source; v is not null && !ReferenceEquals(v, item); v = v.GetVisualParent())
        {
            if (ReferenceEquals(v, item.Content))
            {
                return false;
            }
        }
        return true;
    }

    private static TabItem? FindTabItem(Visual source)
    {
        for (var v = source; v is not null; v = v.GetVisualParent())
        {
            if (v is TabItem item)
            {
                return item;
            }
        }
        return null;
    }

    private static TabItem Fixed(string caption, string icon) => new()
    {
        Header = Header(caption, icon, null),
        Padding = new Thickness(8, 2),
        Name = "tab" + caption,
    };

    private static StackPanel Header(string? text, string? icon, TextBlock? caption, Control? trailing = null)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        if (IconFactory.CreateImage(icon) is { } image)
        {
            panel.Children.Add(image);
        }
        panel.Children.Add(caption ?? new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        if (trailing is not null)
        {
            panel.Children.Add(trailing);
        }
        return panel;
    }

    /// <summary>Changes the caption and icon of a fixed tab (the Table tab uses the object kind's icon).</summary>
    public static void SetFixedHeader(TabItem tab, string text, string icon)
    {
        ArgumentNullException.ThrowIfNull(tab);
        tab.Header = Header(text, icon, null);
    }

    /// <summary>Changes the caption of a fixed tab ("Database: shop", "Table: products").</summary>
    public static void SetFixedCaption(TabItem tab, string text)
    {
        if (tab.Header is StackPanel panel && panel.Children.OfType<TextBlock>().FirstOrDefault() is { } block)
        {
            block.Text = text;
        }
    }
}

internal static class VisualExtensions
{
    public static Visual? GetVisualParent(this Visual visual) => Avalonia.VisualTree.VisualExtensions.GetVisualParent(visual);
}
