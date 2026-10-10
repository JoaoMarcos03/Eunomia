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
/// Gera menus, context menus e a toolbar a partir do registo de comandos (secção 21.2). Captions, atalhos,
/// ícones, estado ativo e check vêm sempre do registo. Itens sem handler (versões seguintes) ficam escondidos, e com
/// eles os separadores que deixariam de separar alguma coisa e os submenus vazios (Fase 13: a geografia dos menus
/// mantém-se, sem itens que não fazem nada). Os estados são relidos quando um menu abre e, na toolbar, a cada 250 ms.
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

    /// <summary>Itens de submenus construídos em runtime ("Connect to ▸", "Editor commands ▸").</summary>
    public Func<string, IReadOnlyList<object>>? DynamicItems { get; set; }

    /// <summary>Entradas dos dropdowns da toolbar (Session manager, Refresh, Run, Load SQL file).</summary>
    public Func<string, IReadOnlyList<MenuEntry>>? DropdownEntries { get; set; }

    /// <summary>Um handler lançou uma exceção (o shell mostra o diálogo de erro).</summary>
    public event EventHandler<Exception>? CommandFailed;

    public Menu BuildMainMenu(IReadOnlyList<MenuDefinition> menus)
    {
        var menu = new Menu();
        foreach (var definition in menus)
        {
            // Os itens de cada menu só são criados na primeira abertura (arranque mais leve, PR-01).
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

    /// <summary>Itens de menu (para menus, context menus e flyouts) a partir das entradas do layout.</summary>
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

    /// <summary>Context menu criado a pedido: os itens deixam de ser seguidos quando o menu fecha.</summary>
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
        // Modificadores do clique (Shift no Quick Filter e no "Select top N rows"): o Click não os traz.
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

        // Botões simples (templates leves): o primeiro frame do arranque desenha os 30 de uma vez (PR-01).
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

    /// <summary>Ícones da toolbar de novo (o tema mudou); os dos menus são recriados quando o menu abre.</summary>
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
            // Toggles: botão "afundado" (classe checked) como no HeidiSQL.
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
    /// Modificadores do último item de menu escolhido (válidos durante o handler do comando; a seguir voltam a None).
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
#pragma warning disable CA1031 // Um erro de um comando não pode derrubar a aplicação: vai para o diálogo de erro.
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
        // As legendas dos comandos não usam teclas de acesso; as dinâmicas levam dados (valores do Quick Filter, defaults).
        item.Header = LiteralText.Escaped(state.Caption);
        item.InputGesture = KeyChordMapper.ToGesture(_registry.ShortcutOf(id));
        item.Icon = IconFactory.CreateImage(state.Command.Icon, state.CanExecute);
        ToolTip.SetTip(item, state.Tooltip ?? state.Command.Hint);
    }

    /// <summary>
    /// Separadores e submenus de uma lista de itens (menu ou toolbar): um separador só aparece entre dois grupos com
    /// itens visíveis; um submenu sem itens visíveis esconde-se. Devolve se ficou algum item visível.
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
