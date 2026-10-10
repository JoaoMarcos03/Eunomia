using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Eunomia.App.Sessions;
using Eunomia.App.Settings;
using Eunomia.Presentation.Sessions;
using Eunomia.UI.Controls.Common.Accessibility;
using Eunomia.UI.Controls.Common.Brand;
using Eunomia.UI.Controls.Common.Dialogs;
using Eunomia.UI.Controls.Common.Icons;
using Eunomia.UI.Controls.Common.Input;
using Eunomia.UI.Controls.Common.Text;
using Eunomia.UI.Controls.Common.Theming;
using Eunomia.UI.Controls.Common.TreeList;

namespace Eunomia.UI.Features.Sessions;

/// <summary>What the Session Manager asks of the shell (connecting, import/export, preferences).</summary>
public sealed class SessionManagerServices
{
    public required SessionManagerModel Model { get; init; }

    public required SessionManagerOptions Options { get; init; }

    /// <summary>Connects with the form values; returns whether it connected (errors already shown).</summary>
    public required Func<Window, SessionDefinition, LoginCredentials?, Task<bool>> OpenAsync { get; init; }

    public Func<Window, Task>? ImportSettingsAsync { get; init; }

    /// <summary>Imports the sessions of the HeidiSQL installed on this computer (only when there is one).</summary>
    public Func<Window, Task>? ImportHeidiSqlAsync { get; init; }

    public Func<Window, Task>? ExportSettingsAsync { get; init; }

    public Func<Window, Task>? ShowPreferencesAsync { get; init; }

    public Func<Window, Task>? ShowAboutAsync { get; init; }

    /// <summary>Connects temporarily with the form values and returns the server's databases (Databases field drop-down).</summary>
    public Func<SessionDefinition, LoginCredentials?, Task<IReadOnlyList<string>>>? ListDatabasesAsync { get; init; }

    public string Version { get; init; } = string.Empty;
}

/// <summary>
/// Session Manager (FAM-S01, S03; section 5.5; appendix A §1): list of sessions and folders with filter, columns, colours and
/// drag &amp; drop; Start, Settings, SSH tunnel, Advanced, SSL and Statistics tabs; New / Save / Delete on the left,
/// Open / Cancel / More on the right. Returns <c>true</c> if it connected a session.
/// </summary>
public sealed partial class SessionManagerWindow : Window
{
    private static readonly string[] ColumnHeaders = ["Session name", "Host", "Port", "User", "Version", "Last connect", "Counter", "Comment"];

    private readonly SessionManagerServices _services;
    private readonly SessionManagerModel _model;
    private readonly TreeListView _list = new();
    private readonly TextBox _filter;
    private readonly DispatcherTimer _filterTimer;
    private readonly TabControl _tabs = new TabControl().Named("Session settings");
    private readonly TabItem _startTab;
    private readonly List<TabItem> _sessionTabs = [];
    private readonly Button _save;
    private readonly Button _delete;
    private readonly Button _open;
    private readonly Grid _body;
    private bool _changingSelection;

    public SessionManagerWindow(SessionManagerServices services)
    {
        Icon = BrandAssets.WindowIcon;
        _services = services;
        _model = services.Model;
        var options = services.Options;
        Width = Math.Max(640, options.WindowWidth);
        Height = Math.Max(470, options.WindowHeight);
        MinWidth = 640;
        MinHeight = 470;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = UiFonts.Ui;
        FontSize = 12;
        UpdateTitle();

        // Session list.
        var list = _list.List;
        list.AllowDrag = true;
        list.AllowRename = true;
        list.GroupsFirst = options.FoldersAtTop;
        for (var i = 0; i < ColumnHeaders.Length; i++)
        {
            var width = i < options.ColumnWidths.Count ? options.ColumnWidths[i] : 80;
            list.Columns.Add(new TreeListColumn(ColumnHeaders[i], width, i is 2 or 6 ? TextAlignment.Right : TextAlignment.Left)
            {
                Visible = i < options.ColumnsVisible.Count ? options.ColumnsVisible[i] || i == 0 : true,
            });
        }
        list.SortBy(Math.Clamp(options.SortColumn, 0, ColumnHeaders.Length - 1), options.SortDescending);
        list.FocusedNodeChanged += (_, _) => OnListSelectionChanged();
        list.NodeActivated += async (_, node) =>
        {
            if (node.Tag is SessionTreeNode { Session: not null })
            {
                await OpenAsync();
            }
        };
        list.RenameRequested += (_, e) => OnRename(e);
        list.DropRequested += (_, e) => OnDrop(e);
        list.HeaderContextRequested += (_, p) => ShowColumnsMenu(p);
        list.ContextMenu = BuildListMenu();

        _filter = new TextBox { PlaceholderText = "Filter", MinHeight = 24, Padding = new Thickness(4, 2), Margin = new Thickness(0, 0, 0, 4) }.Named("Filter sessions");
        _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _filterTimer.Tick += (_, _) =>
        {
            _filterTimer.Stop();
            ApplyFilter();
        };
        _filter.TextChanged += (_, _) =>
        {
            _filterTimer.Stop();
            _filterTimer.Start();
        };

        var newButton = new SplitButton { Content = "New", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        newButton.Click += (_, _) => NewSession(inSelectedFolder: true);
        newButton.Flyout = new MenuFlyout
        {
            ItemsSource = new[]
            {
                Item("Session in root folder", () => NewSession(false)),
                Item("Session in selected folder", () => NewSession(true)),
                Item("Folder in root folder", () => NewFolder(false)),
                Item("Folder in selected folder", () => NewFolder(true)),
            },
        };
        _save = new Button { Content = "Save", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Name = "btnSave" };
        _save.Click += (_, _) => Save();
        _delete = new Button { Content = "Delete", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Name = "btnDelete" };
        _delete.Click += async (_, _) => await DeleteAsync();
        // "New" has the split button arrow: it takes the width it needs and the other two share the rest.
        newButton.MinWidth = 76;
        var leftButtons = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,4,*,4,*"), Margin = new Thickness(0, 6, 0, 0) };
        Grid.SetColumn(_save, 2);
        Grid.SetColumn(_delete, 4);
        leftButtons.Children.Add(newButton);
        leftButtons.Children.Add(_save);
        leftButtons.Children.Add(_delete);

        var left = new DockPanel { Margin = new Thickness(8, 8, 0, 8) };
        DockPanel.SetDock(_filter, Dock.Top);
        DockPanel.SetDock(leftButtons, Dock.Bottom);
        left.Children.Add(_filter);
        left.Children.Add(leftButtons);
        left.Children.Add(Themed.Bind(new Border { Child = _list, BorderThickness = new Thickness(1) }, Border.BorderBrushProperty, "border"));

        // Session tabs.
        _startTab = new TabItem { Header = "Start", Content = BuildStartTab() };
        _tabs.Items.Add(_startTab);
        foreach (var (header, icon, content) in new (string, string, Control)[]
                 {
                     ("Settings", "preferences", BuildSettingsTab()),
                     ("SSH tunnel", "console", BuildSshTab()),
                     ("Advanced", "maintenance", BuildAdvancedTab()),
                     ("SSL", "properties", BuildSslTab()),
                     ("Statistics", "export-grid", BuildStatisticsTab()),
                 })
        {
            var tab = new TabItem { Header = Caption(icon, header), Content = content, FontSize = 12, MinHeight = 24 };
            _sessionTabs.Add(tab);
            _tabs.Items.Add(tab);
        }

        _open = new Button { Content = "Open", IsDefault = true, Width = 90, HorizontalContentAlignment = HorizontalAlignment.Center, Name = "btnOpen" };
        _open.Click += async (_, _) => await OpenAsync();
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        cancel.Click += (_, _) => Close(false);
        var more = new SplitButton { Content = "More", Width = 90 };
        var moreMenu = new MenuFlyout
        {
            ItemsSource = new object[]
            {
                Item("Preferences", async () => { if (_services.ShowPreferencesAsync is { } p) { await p(this); } }, _services.ShowPreferencesAsync is not null),
                Item("Import settings file ...", async () => await ImportAsync(), _services.ImportSettingsAsync is not null),
                Item("Import sessions from HeidiSQL", async () => await ImportHeidiSqlAsync(), _services.ImportHeidiSqlAsync is not null),
                Item("Export settings file ...", async () => { if (_services.ExportSettingsAsync is { } x) { await x(this); } }, _services.ExportSettingsAsync is not null),
                Item("About", async () => { if (_services.ShowAboutAsync is { } a) { await a(this); } }, _services.ShowAboutAsync is not null),
            },
        };
        more.Flyout = moreMenu;
        more.Click += (_, _) => moreMenu.ShowAt(more);
        var rightButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6, Margin = new Thickness(0, 6, 0, 0) };
        rightButtons.Children.Add(_open);
        rightButtons.Children.Add(cancel);
        rightButtons.Children.Add(more);
        var right = new DockPanel { Margin = new Thickness(0, 8, 8, 8) };
        DockPanel.SetDock(rightButtons, Dock.Bottom);
        right.Children.Add(rightButtons);
        right.Children.Add(_tabs);

        _body = new Grid { ColumnDefinitions = new ColumnDefinitions($"{Math.Max(200, options.ListWidth).ToString(CultureInfo.InvariantCulture)},6,*") };
        _body.ColumnDefinitions[0].MinWidth = 200;
        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Background = Brushes.Transparent };
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(right, 2);
        _body.Children.Add(left);
        _body.Children.Add(splitter);
        _body.Children.Add(right);
        Content = _body;

        KeyDown += OnWindowKeyDown;
        Closing += OnClosing;
        Opened += (_, _) =>
        {
            Reload();
            var last = options.LastActiveSession is { } id ? _model.Catalog.Find(id) : null;
            if (last is not null)
            {
                FocusSession(last);
            }
            else
            {
                ShowTabsFor(null);
            }
            list.Focus();
        };
    }

    /// <summary>The session list (tests).</summary>
    internal TreeListControl SessionList => _list.List;

    internal TextBox FilterBox => _filter;

    internal Button SaveButton => _save;

    internal TabControl Tabs => _tabs;

    /// <summary>Applies the filter now, without the 500 ms debounce (tests).</summary>
    internal void ApplyFilterNow() => ApplyFilter();

    private static StackPanel Caption(string icon, string text)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        if (IconFactory.CreateImage(icon) is { } image)
        {
            panel.Children.Add(image);
        }
        panel.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    private static MenuItem Item(string header, Action action, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    private void UpdateTitle()
    {
        var path = _model.Selected?.Path ?? (_model.SelectedFolder is { Count: > 0 } f ? string.Join('\\', f) : null);
        Title = $"Eunomia {_services.Version} - Session manager" + (path is null ? string.Empty : ": " + path);
    }

    /// <summary>Rebuilds the list from the catalog (keeps the selection and open folders).</summary>
    internal void Reload()
    {
        var expanded = _list.List.AllNodes().Where(n => n.IsExpanded && n.Tag is SessionTreeNode { IsFolder: true }).Select(n => FolderKey((SessionTreeNode)n.Tag!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tree = _model.Catalog.BuildTree(_services.Options.FoldersAtTop);
        _list.List.SetRoots(tree.Select(n => ToNode(n, expanded)));
        ApplyFilter();
        RefreshButtons();
    }

    private static string FolderKey(SessionTreeNode node) => string.Join('\\', node.OwnPath);

    private TreeListNode ToNode(SessionTreeNode node, HashSet<string> expanded)
    {
        if (node.Session is not { } s)
        {
            var folder = new TreeListNode(node.Name) { Icon = "open", IsGroup = true, Tag = node, IsExpanded = expanded.Contains(FolderKey(node)) };
            foreach (var child in node.Children)
            {
                folder.Add(ToNode(child, expanded));
            }
            return folder;
        }
        var modified = ReferenceEquals(s, _model.Selected) && _model.IsModified;
        return new TreeListNode(
            s.Name + (modified ? " *" : string.Empty),
            s.Host,
            s.Port.ToString(CultureInfo.InvariantCulture),
            s.User,
            s.Statistics.ServerVersionFull,
            s.Statistics.LastConnect?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty,
            s.Statistics.ConnectCount.ToString(CultureInfo.InvariantCulture),
            s.Comment.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' '))
        {
            Icon = VendorIcon(s),
            Background = s.Color,
            Tag = node,
        };
    }

    /// <summary>Icon by network type and vendor of the last connection (appendix A §1.2).</summary>
    private static string VendorIcon(SessionDefinition s) =>
        NetTypes.IsMySqlFamily(s.NetType) ? "database" : s.NetType is NetType.Sqlite or NetType.SqliteEncrypted ? "table" : "session-manager";

    private void ApplyFilter()
    {
        var match = SessionCatalog.CreateFilter(_filter.Text ?? string.Empty);
        _list.List.SetFilter(string.IsNullOrEmpty(_filter.Text) ? null : n => n.Tag is SessionTreeNode { Session: { } s } && match(s));
    }

    private void FocusSession(SessionDefinition session)
    {
        var node = _list.List.AllNodes().FirstOrDefault(n => n.Tag is SessionTreeNode t && ReferenceEquals(t.Session, session));
        if (node is not null)
        {
            _list.List.FocusedNode = node;
        }
    }

    private void FocusFolder(IReadOnlyList<string> folder)
    {
        var node = _list.List.AllNodes().FirstOrDefault(n => n.Tag is SessionTreeNode { IsFolder: true } t && SessionCatalog.SamePath(t.OwnPath, folder));
        if (node is not null)
        {
            _list.List.FocusedNode = node;
        }
    }

    private async void OnListSelectionChanged()
    {
        if (_changingSelection)
        {
            return;
        }
        var target = _list.List.FocusedNode?.Tag as SessionTreeNode;
        if (target?.Session is { } s && ReferenceEquals(s, _model.Selected))
        {
            return;
        }
        if (_model.NeedsSavePrompt)
        {
            var answer = await MessageDialog.ShowAsync(this, "Session manager", $"Save modifications of session \"{_model.Selected!.Path}\"?", MessageDialogButtons.YesNoCancel);
            if (answer == MessageDialogResult.Cancel)
            {
                _changingSelection = true;
                FocusSession(_model.Selected!);
                _changingSelection = false;
                return;
            }
            if (answer == MessageDialogResult.Yes)
            {
                _model.Save();
            }
        }
        if (target?.Session is { } session)
        {
            _model.SelectSession(session);
        }
        else
        {
            _model.SelectFolder(target?.OwnPath);
        }
        ShowTabsFor(_model.Draft);
        UpdateTitle();
        RefreshListMarkers();
    }

    /// <summary>Start only with a folder or nothing; the other tabs only with a session (appendix A §1.1).</summary>
    private void ShowTabsFor(SessionDefinition? draft)
    {
        _startTab.IsVisible = draft is null;
        foreach (var tab in _sessionTabs)
        {
            tab.IsVisible = draft is not null;
        }
        if (draft is null)
        {
            _tabs.SelectedItem = _startTab;
            UpdateStartText();
        }
        else
        {
            if (_tabs.SelectedItem is not TabItem { IsVisible: true })
            {
                _tabs.SelectedItem = _sessionTabs[0];
            }
            LoadForm(draft);
        }
        RefreshButtons();
    }

    internal void RefreshButtons()
    {
        _save.IsEnabled = _model.IsModified;
        _delete.IsEnabled = _model.Selected is not null || _model.SelectedFolder is { Count: > 0 };
        _open.IsEnabled = _model.Selected is not null;
    }

    private void RefreshListMarkers()
    {
        foreach (var node in _list.List.AllNodes())
        {
            if (node.Tag is SessionTreeNode { Session: { } s })
            {
                node.Cells[0] = s.Name + (ReferenceEquals(s, _model.Selected) && _model.IsModified ? " *" : string.Empty);
            }
        }
        _list.List.InvalidateVisual();
    }

    private void NewSession(bool inSelectedFolder)
    {
        var session = _model.NewSession(inSelectedFolder);
        Reload();
        FocusSession(session);
        _list.List.BeginRename();
    }

    private void NewFolder(bool inSelectedFolder)
    {
        var folder = _model.NewFolder(inSelectedFolder);
        Reload();
        FocusFolder(folder);
        _list.List.BeginRename();
    }

    private void Save()
    {
        if (!_model.IsModified)
        {
            return;
        }
        _model.Save();
        Reload();
        RefreshButtons();
    }

    private async Task DeleteAsync()
    {
        string? question = _model.Selected is { } s ? $"Delete session \"{s.Path}\"?"
            : _model.SelectedFolder is { Count: > 0 } f ? $"Delete folder \"{string.Join('\\', f)}\" and all sessions in it?"
            : null;
        if (question is null)
        {
            return;
        }
        if (await MessageDialog.ShowAsync(this, "Confirm", question, MessageDialogButtons.YesNo) != MessageDialogResult.Yes)
        {
            return;
        }
        _model.DeleteSelected();
        Reload();
        ShowTabsFor(null);
        UpdateTitle();
    }

    private async Task DuplicateAsync()
    {
        if (_model.Draft is not { } draft)
        {
            return;
        }
        var name = await InputDialog.ShowAsync(this, "Duplicate session", "New session name:", _model.Catalog.UniqueName(draft.Folder, draft.Name));
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        try
        {
            var copy = _model.Duplicate(name);
            Reload();
            FocusSession(copy);
        }
        catch (InvalidOperationException ex)
        {
            await MessageDialog.ShowAsync(this, "Error", ex.Message, MessageDialogButtons.Ok);
        }
    }

    private async void OnRename(TreeListRenameEventArgs e)
    {
        if (e.Node.Tag is not SessionTreeNode node)
        {
            return;
        }
        var error = _model.Rename(node, e.Text);
        if (error is not null)
        {
            await MessageDialog.ShowAsync(this, "Error", error, MessageDialogButtons.Ok);
        }
        Reload();
        if (node.Session is { } s)
        {
            FocusSession(s);
        }
        UpdateTitle();
    }

    private async void OnDrop(TreeListDropEventArgs e)
    {
        if (e.Source.Tag is not SessionTreeNode source)
        {
            return;
        }
        var target = e.Target?.Tag as SessionTreeNode;
        if (target is not null && source.IsFolder && target.IsFolder && SessionCatalog.IsPrefix(source.OwnPath, target.OwnPath))
        {
            return;
        }
        var error = _model.Move(source, target);
        if (error is not null && !error.StartsWith("Already", StringComparison.Ordinal) && !error.StartsWith("Cannot", StringComparison.Ordinal))
        {
            await MessageDialog.ShowAsync(this, "Error", error, MessageDialogButtons.Ok);
        }
        Reload();
    }

    private async Task OpenAsync()
    {
        if (_model.Draft is null)
        {
            return;
        }
        var (values, credentials) = _model.ValuesForOpen();
        _open.IsEnabled = false;
        _open.Content = "Connecting ...";
        try
        {
            var ok = await _services.OpenAsync(this, values, credentials);
            _model.RecordConnectAttempt(values);
            Reload();
            if (ok)
            {
                _services.Options.LastActiveSession = values.Id;
                Close(true);
            }
        }
        finally
        {
            _open.Content = "Open";
            RefreshButtons();
        }
    }

    private async Task ImportHeidiSqlAsync()
    {
        if (_services.ImportHeidiSqlAsync is { } import)
        {
            await import(this);
            Reload();
        }
    }

    private async Task ImportAsync()
    {
        if (_services.ImportSettingsAsync is { } import)
        {
            await import(this);
            Reload();
        }
    }

    private ContextMenu BuildListMenu()
    {
        var foldersAtTop = new MenuItem { Header = "Folders at top", ToggleType = MenuItemToggleType.CheckBox, IsChecked = _services.Options.FoldersAtTop };
        foldersAtTop.Click += (_, _) =>
        {
            _services.Options.FoldersAtTop = !_services.Options.FoldersAtTop;
            foldersAtTop.IsChecked = _services.Options.FoldersAtTop;
            _list.List.GroupsFirst = _services.Options.FoldersAtTop;
            Reload();
        };
        return new ContextMenu
        {
            ItemsSource = new object[]
            {
                WithGesture(Item("Rename", () => _list.List.BeginRename()), "F2"),
                WithGesture(Item("Save", Save), "Ctrl+S"),
                WithGesture(Item("Duplicate / save as ...", () => _ = DuplicateAsync()), "F12"),
                WithGesture(Item("Delete", () => _ = DeleteAsync()), "Delete"),
                new Separator(),
                Item("New session", () => NewSession(true)),
                Item("New folder", () => NewFolder(true)),
                new Separator(),
                WithGesture(Item("Filter ...", () => _filter.Focus()), "Ctrl+F"),
                foldersAtTop,
            },
        };
    }

    private static MenuItem WithGesture(MenuItem item, string gesture)
    {
        item.InputGesture = KeyGesture.Parse(gesture);
        return item;
    }

    private void ShowColumnsMenu(Point point)
    {
        var items = new List<object>();
        for (var i = 1; i < _list.List.Columns.Count; i++)
        {
            var column = _list.List.Columns[i];
            var item = new MenuItem { Header = LiteralText.Escaped(column.Header), ToggleType = MenuItemToggleType.CheckBox, IsChecked = column.Visible };
            item.Click += (_, _) =>
            {
                column.Visible = !column.Visible;
                _list.List.InvalidateVisual();
            };
            items.Add(item);
        }
        var menu = new ContextMenu { ItemsSource = items };
        menu.Open(_list.List);
    }

    private async void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && PlatformKeys.Normalize(e.KeyModifiers) == KeyModifiers.Control)
        {
            Save();
            e.Handled = true;
        }
        else if (e.Key == Key.F12)
        {
            e.Handled = true;
            await DuplicateAsync();
        }
        else if (e.Key == Key.F && PlatformKeys.Normalize(e.KeyModifiers) == KeyModifiers.Control)
        {
            _filter.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && _list.List.IsKeyboardFocusWithin && !_list.List.IsRenaming)
        {
            e.Handled = true;
            await DeleteAsync();
        }
    }

    private bool _closeConfirmed;

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        // "Save modifications?" also on closing (appendix A §1.5).
        if (!_closeConfirmed && _model.NeedsSavePrompt)
        {
            e.Cancel = true;
            var answer = await MessageDialog.ShowAsync(this, "Session manager", $"Save modifications of session \"{_model.Selected!.Path}\"?", MessageDialogButtons.YesNoCancel);
            if (answer == MessageDialogResult.Cancel)
            {
                return;
            }
            if (answer == MessageDialogResult.Yes)
            {
                _model.Save();
            }
            else
            {
                _model.Discard();
            }
            _closeConfirmed = true;
            Close(false);
            return;
        }
        var options = _services.Options;
        if (WindowState == WindowState.Normal)
        {
            options.WindowWidth = (int)Width;
            options.WindowHeight = (int)Height;
        }
        options.ListWidth = (int)_body.ColumnDefinitions[0].ActualWidth;
        options.ColumnWidths = [.. _list.List.Columns.Select(c => (int)c.Width)];
        options.ColumnsVisible = [.. _list.List.Columns.Select(c => c.Visible)];
        options.SortColumn = _list.List.SortColumn;
        options.SortDescending = _list.List.SortDescending;
    }
}
