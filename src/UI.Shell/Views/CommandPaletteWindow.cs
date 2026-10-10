using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Eunomia.App.Commands;
using Eunomia.UI.Controls.Common.Accessibility;
using Eunomia.UI.Controls.Common.Brand;
using Eunomia.UI.Controls.Common.Text;
using Eunomia.UI.Controls.Common.Theming;

namespace Eunomia.UI.Shell.Views;

/// <summary>
/// Paleta de comandos (Tools ▸ Command palette, Ctrl+Shift+P; roadmap 1.x #11): uma caixa de pesquisa por cima da lista
/// dos comandos que podem correr agora (legenda, categoria e atalho). ↑/↓ escolhem, Enter ou duplo clique fecham com o id
/// do comando, Esc fecha sem nada. Não substitui menus nem atalhos do HeidiSQL: só os encontra pelo nome.
/// </summary>
public sealed class CommandPaletteWindow : Window
{
    private readonly IReadOnlyList<CommandMatch> _commands;
    private readonly TextBox _search = new TextBox { PlaceholderText = "Type a command name", Name = "paletteSearch" }.Named("Command");
    private readonly ListBox _list = new ListBox { Name = "paletteList" }.Named("Commands");

    public CommandPaletteWindow(IReadOnlyList<CommandMatch> commands)
    {
        Icon = BrandAssets.WindowIcon;
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        Title = "Command palette";
        Width = 520;
        Height = 380;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = UiFonts.Ui;
        FontSize = 12;

        // A mudança da propriedade é síncrona (o TextChanged chega depois, despachado).
        _search.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                Fill();
            }
        };
        _search.KeyDown += OnSearchKeyDown;
        _search.AttachedToVisualTree += (_, _) => _search.Focus();
        _list.DoubleTapped += (_, _) => Accept();
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Accept();
            }
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close(null);
            }
        };
        var root = new DockPanel { Margin = new Thickness(8) };
        DockPanel.SetDock(_search, Dock.Top);
        _list.Margin = new Thickness(0, 6, 0, 0);
        root.Children.Add(_search);
        root.Children.Add(_list);
        Content = root;
        Fill();
    }

    public TextBox SearchBox => _search;

    /// <summary>Ids dos comandos mostrados, pela ordem (testes).</summary>
    public IReadOnlyList<string> Shown => [.. _list.Items.OfType<ListBoxItem>().Select(i => ((CommandMatch)i.Tag!).Id)];

    /// <summary>Fecha com o comando escolhido (o primeiro, se nenhum estiver escolhido).</summary>
    public void Accept()
    {
        var item = _list.SelectedItem as ListBoxItem ?? _list.Items.OfType<ListBoxItem>().FirstOrDefault();
        if (item?.Tag is CommandMatch match)
        {
            Close(match.Id);
        }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        var count = _list.ItemCount;
        switch (e.Key)
        {
            case Key.Down when count > 0:
                _list.SelectedIndex = Math.Min(count - 1, _list.SelectedIndex + 1);
                _list.ScrollIntoView(_list.SelectedIndex);
                e.Handled = true;
                break;
            case Key.Up when count > 0:
                _list.SelectedIndex = Math.Max(0, _list.SelectedIndex - 1);
                _list.ScrollIntoView(_list.SelectedIndex);
                e.Handled = true;
                break;
            case Key.Enter:
                e.Handled = true;
                Accept();
                break;
        }
    }

    private void Fill()
    {
        _list.Items.Clear();
        foreach (var match in CommandSearch.Filter(_commands, _search.Text))
        {
            _list.Items.Add(Row(match));
        }
        _list.SelectedIndex = _list.ItemCount > 0 ? 0 : -1;
    }

    private static ListBoxItem Row(CommandMatch match)
    {
        var row = new DockPanel();
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        right.Children.Add(Themed.Bind(LiteralText.Of(match.Category), TextBlock.ForegroundProperty, "text.muted"));
        right.Children.Add(new TextBlock { Text = match.Shortcut ?? string.Empty, MinWidth = 90, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(right);
        var caption = LiteralText.Of(match.IsChecked ? match.Caption + "  ✓" : match.Caption);
        caption.TextTrimming = TextTrimming.CharacterEllipsis;
        row.Children.Add(caption);
        return new ListBoxItem { Content = row, Tag = match, Padding = new Thickness(6, 3) }.Named(match.Caption);
    }
}
