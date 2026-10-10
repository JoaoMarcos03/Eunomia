using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Eunomia.App.Commands;
using Eunomia.UI.Controls.Common.Icons;
using Eunomia.UI.Controls.Common.Text;
using Eunomia.UI.Shell.Input;

namespace Eunomia.UI.Shell.Commands;

/// <summary>
/// Generates menus, context menus and the toolbar from the command registry (section 21.2). Captions, shortcuts,
/// icons, enabled and checked state always come from the registry. Items without a handler (later versions) are hidden, and
/// with them the separators that would no longer separate anything and empty submenus (phase 13: the menu geography
/// stays, without items that do nothing). States are read again when a menu opens and, on the toolbar, every 250 ms.
/// </summary>
public sealed class CommandUi
{
    private static readonly Avalonia.Media.Geometry DropArrow = Avalonia.Media.Geometry.Parse("M0,0 L5,0 L2.5,3 Z");

    private readonly CommandRegistry _registry;
    private readonly List<(MenuItem Item, string Id)> _menuItems = [];
    private readonly List<(Control Button, Image Icon, string Id)> _buttons = [];
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<MenuItem, List<object>> _submenus = [];
    private Panel? _toolbar;
    private readonly DispatcherTimer _timer;
    private bool _refreshPosted;

    public CommandUi(CommandRegistry registry)
    {
        _registry = registry;
        _registry.StatesInvalidated += (_, _) => PostRefresh();
        IconFactory.ThemeChanged += (_, _) => Dispatcher.UIThread.Post(RefreshIcons);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => RefreshToolbar());
        _timer.Start();
    }

    /// <summary>Submenu items built at runtime ("Connect to ▸", "Editor commands ▸").</summary>
    public Func<string, IReadOnlyList<object>>? DynamicItems { get; set; }

    /// <summary>Entries of the toolbar drop-downs (Session manager, Refresh, Run, Load SQL file).</summary>
    public Func<string, IReadOnlyList<MenuEntry>>? DropdownEntries { get; set; }

    /// <summary>A handler threw an exception (the shell shows the error dialog).</summary>
    public event EventHandler<Exception>? CommandFailed;

    public Menu BuildMainMenu(IReadOnlyList<MenuDefinition> menus)
    {
        var menu = new Menu();
        foreach (var definition in menus)
        {
            // Each menu's items are only created the first time it opens (lighter startup, PR-01).
            var top = new MenuItem { Header = definition.Caption, ItemsSource = new[] { new MenuItem { Header = "..." } } };
            var built = false;
            top.SubmenuOpened += (_, _) =>
            {
                if (!built)
                {
                    built = true;
                    top.ItemsSource = BuildItems(definition.Items);
                }
                RefreshMenus();
                Arrange((IReadOnlyList<object>)top.ItemsSource!);
            };
            menu.Items.Add(top);
        }
        return menu;
    }

    /// <summary>Menu items (for menus, context menus and flyouts) from the layout entries.</summary>
    public List<object> BuildItems(IReadOnlyList<MenuEntry> entries)
    {
        var items = new List<object>();
        foreach (var entry in entries)
        {
            switch (entry)
            {
                case CommandEntry c:
                    if (!_registry.Get(c.CommandId).Hidden)
                    {
                        items.Add(CreateMenuItem(c.CommandId));
                    }
                    break;
                case SeparatorEntry:
                    items.Add(new Separator());
                    break;
                case SubmenuEntry s:
                    var children = BuildItems(s.Items);
                    var submenu = new MenuItem { Header = s.Caption, Icon = IconFactory.CreateImage(s.Icon), ItemsSource = children };
                    _submenus.AddOrUpdate(submenu, children);
                    items.Add(submenu);
                    break;
                case DynamicEntry d:
                    var dynamic = new MenuItem { Header = d.Caption };
                    dynamic.SubmenuOpened += (_, _) => FillDynamic(dynamic, d.Kind);
                    FillDynamic(dynamic, d.Kind);
                    items.Add(dynamic);
                    break;
            }
        }
        return items;
    }

    public ContextMenu BuildContextMenu(IReadOnlyList<MenuEntry> entries)
    {
        var items = BuildItems(entries);
        var menu = new ContextMenu { ItemsSource = items };
        menu.Opening += (_, _) =>
        {
            RefreshMenus();
            Arrange(items);
        };
        return menu;
    }

    /// <summary>Context menu created on demand: its items stop being tracked when the menu closes.</summary>
    public ContextMenu BuildTransientContextMenu(IReadOnlyList<MenuEntry> entries)
    {
        var first = _menuItems.Count;
        var items = BuildItems(entries);
        var menu = new ContextMenu { ItemsSource = items };
        var created = _menuItems.Skip(first).Select(x => x.Item).ToHashSet();
        menu.Closed += (_, _) => _menuItems.RemoveAll(x => created.Contains(x.Item));
        RefreshMenus();
        Arrange(items);
        return menu;
    }

    public MenuItem CreateMenuItem(string id)
    {
        var command = _registry.Get(id);
        var item = new MenuItem
        {
            Header = LiteralText.Escaped(command.Caption),
            InputGesture = KeyChordMapper.ToGesture(_registry.ShortcutOf(id)),
            ToggleType = command.Kind switch
            {
                CommandKind.Toggle => MenuItemToggleType.CheckBox,
                CommandKind.Radio => MenuItemToggleType.Radio,
                _ => MenuItemToggleType.None,
            },
            GroupName = command.RadioGroup,
        };
        ToolTip.SetShowOnDisabled(item, true);
        // Click modifiers (Shift in Quick Filter and in "Select top N rows"): Click does not carry them.
        var modifiers = KeyModifiers.None;
        item.AddHandler(InputElement.PointerPressedEvent, (_, e) => modifiers = e.KeyModifiers, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        item.AddHandler(InputElement.PointerReleasedEvent, (_, e) => modifiers = e.KeyModifiers, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        item.AddHandler(InputElement.KeyDownEvent, (_, e) => modifiers = e.KeyModifiers, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        item.Click += (_, e) =>
        {
            e.Handled = true;
            InvokeModifiers = modifiers;
            modifiers = KeyModifiers.None;
            Execute(id);
        };
        _menuItems.Add((item, id));
        UpdateMenuItem(item, id);
        return item;
    }

    public Control BuildToolbar(IReadOnlyList<ToolbarEntry> entries)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Height = 26, Spacing = 0, Margin = new Thickness(2, 0) };
        _toolbar = bar;
        foreach (var entry in entries)
        {
            if (entry.IsSeparator)
            {
                bar.Children.Add(new Border { Width = 1, Margin = new Thickness(4, 4), Classes = { "toolbar-separator" } });
                continue;
            }
            bar.Children.Add(CreateButton(entry.CommandId!, entry.Dropdown));
        }
        RefreshToolbar();
        return bar;
    }

    public Control CreateButton(string id, string? dropdown = null)
    {
        var command = _registry.Get(id);
        var icon = new Image { Width = 16, Height = 16, Source = IconFactory.Get(command.Icon) };

        // Plain buttons (light templates): the first startup frame draws all 30 at once (PR-01).
        var main = new Button { Content = icon, Focusable = false, Classes = { "toolbar" } };
        main.Click += (_, _) => Execute(id);
        Control button = main;
        if (dropdown is not null)
        {
            var flyout = new MenuFlyout();
            flyout.Opening += (_, _) =>
            {
                var entries = DropdownEntries?.Invoke(dropdown) ?? [];
                var dynamicItems = entries.Count == 0 ? DynamicItems?.Invoke(dropdown) : null;
                var built = dynamicItems is { Count: > 0 } ? dynamicItems : BuildItems(entries);
                flyout.ItemsSource = built;
                RefreshMenus();
                Arrange(built);
            };
            var arrow = new Button
            {
                Content = new Avalonia.Controls.Shapes.Path { Data = DropArrow, Fill = Avalonia.Media.Brushes.DimGray, Width = 5, Height = 3, Stretch = Avalonia.Media.Stretch.Fill },
                Flyout = flyout,
                Focusable = false,
                Classes = { "toolbar-arrow" },
            };
            var pair = new StackPanel { Orientation = Orientation.Horizontal };
            pair.Children.Add(main);
            pair.Children.Add(arrow);
            button = pair;
        }
        ToolTip.SetShowOnDisabled(button, true);
        button.Name = "tb_" + id;
        _buttons.Add((button, icon, id));
        return button;
    }

    /// <summary>Toolbar icons again (the theme changed); the menu ones are recreated when the menu opens.</summary>
    private void RefreshIcons()
    {
        foreach (var (button, icon, id) in _buttons)
        {
            icon.Source = IconFactory.Get(_registry.Get(id).Icon, button.IsEnabled);
        }
    }

    public void RefreshMenus()
    {
        foreach (var (item, id) in _menuItems)
        {
            UpdateMenuItem(item, id);
        }
    }

    public void RefreshToolbar()
    {
        var visibilityChanged = false;
        foreach (var (button, icon, id) in _buttons)
        {
            var state = _registry.StateOf(id);
            if (button.IsVisible != state.IsImplemented)
            {
                button.IsVisible = state.IsImplemented;
                visibilityChanged = true;
            }
            if (button.IsEnabled != state.CanExecute)
            {
                button.IsEnabled = state.CanExecute;
                icon.Source = IconFactory.Get(state.Command.Icon, state.CanExecute);
            }
            // Toggles: a "pressed" button (checked class) as in HeidiSQL.
            var main = button is StackPanel pair ? (Button)pair.Children[0] : (Button)button;
            if (main.Classes.Contains("checked") != state.IsChecked)
            {
                main.Classes.Set("checked", state.IsChecked);
            }
            var shortcut = _registry.ShortcutOf(id);
            var tip = state.Command.Caption + (shortcut is { } s ? $" ({s})" : string.Empty)
                + (state.Tooltip is { } t ? Environment.NewLine + t : state.Command.Hint is { } h ? Environment.NewLine + h : string.Empty);
            ToolTip.SetTip(button, tip);
        }
        if (visibilityChanged && _toolbar is not null)
        {
            Arrange([.. _toolbar.Children]);
        }
    }

    public void Refresh()
    {
        RefreshMenus();
        RefreshToolbar();
    }

    /// <summary>
    /// Modifiers of the last menu item chosen (valid during the command's handler; afterwards they go back to None).
    /// </summary>
    public KeyModifiers InvokeModifiers { get; private set; }

    public void Execute(string id)
    {
        _ = ExecuteAsync(id);
    }

    public async Task<bool> ExecuteAsync(string id)
    {
        try
        {
            try
            {
                return await _registry.ExecuteAsync(id).ConfigureAwait(true);
            }
            finally
            {
                InvokeModifiers = KeyModifiers.None;
            }
        }
#pragma warning disable CA1031 // An error in a command must not bring the application down: it goes to the error dialog.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            CommandFailed?.Invoke(this, ex);
            return false;
        }
        finally
        {
            Refresh();
        }
    }

    private void UpdateMenuItem(MenuItem item, string id)
    {
        var state = _registry.StateOf(id);
        item.IsVisible = state.IsImplemented;
        item.IsEnabled = state.CanExecute;
        item.IsChecked = state.IsChecked;
        // Command captions do not use access keys; dynamic ones carry data (Quick Filter values, defaults).
        item.Header = LiteralText.Escaped(state.Caption);
        item.InputGesture = KeyChordMapper.ToGesture(_registry.ShortcutOf(id));
        item.Icon = IconFactory.CreateImage(state.Command.Icon, state.CanExecute);
        ToolTip.SetTip(item, state.Tooltip ?? state.Command.Hint);
    }

    /// <summary>
    /// Separators and submenus of a list of items (menu or toolbar): a separator only shows between two groups with
    /// visible items; a submenu without visible items is hidden. Returns whether any item stayed visible.
    /// </summary>
    public bool Arrange(IReadOnlyList<object> items)
    {
        var anyVisible = false;
        Control? pending = null;
        foreach (var item in items)
        {
            if (item is Separator || (item is Border border && border.Classes.Contains("toolbar-separator")))
            {
                var separator = (Control)item;
                separator.IsVisible = false;
                if (anyVisible)
                {
                    pending ??= separator;
                }
                continue;
            }
            if (item is MenuItem submenu && _submenus.TryGetValue(submenu, out var children))
            {
                submenu.IsVisible = Arrange(children);
            }
            if (item is Control { IsVisible: true })
            {
                if (pending is not null)
                {
                    pending.IsVisible = true;
                    pending = null;
                }
                anyVisible = true;
            }
        }
        return anyVisible;
    }

    private void FillDynamic(MenuItem owner, string kind)
    {
        var items = DynamicItems?.Invoke(kind) ?? [];
        owner.ItemsSource = items.Count > 0 ? items : [new MenuItem { Header = "(none)", IsEnabled = false }];
    }

    private void PostRefresh()
    {
        if (_refreshPosted)
        {
            return;
        }
        _refreshPosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshPosted = false;
            Refresh();
        }, DispatcherPriority.Background);
    }
}
