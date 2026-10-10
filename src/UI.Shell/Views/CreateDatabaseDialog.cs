using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Eunomia.UI.Controls.Common.Accessibility;
using Eunomia.UI.Controls.Common.Brand;
using Eunomia.UI.Controls.Common.Theming;

namespace Eunomia.UI.Shell.Views;

/// <summary>Resultado do diálogo Create database.</summary>
public sealed record CreateDatabaseRequest(string Name, string? Collation);

/// <summary>
/// Create database (anexo B §5.3): Name, Collation com "Servers default: …" e o CREATE code ao vivo. Só o modo de
/// criação; alterar e renomear bases fica para o editor de bases.
/// </summary>
public sealed class CreateDatabaseDialog : Window
{
    private readonly TextBox _name;
    private readonly ComboBox _collation;
    private readonly TextBox _code;
    private readonly Button _ok;
    private readonly Func<string, string?, string> _preview;

    public CreateDatabaseDialog(IReadOnlyList<string> collations, string? preselected, string? serverDefault, Func<string, string?, string> preview)
    {
        Icon = BrandAssets.WindowIcon;
        ArgumentNullException.ThrowIfNull(collations);
        _preview = preview;
        Title = "Create database ...";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = UiFonts.Ui;
        FontSize = 12;

        _name = new TextBox { Name = "dbName" };
        _name.AttachedToVisualTree += (_, _) => _name.Focus();
        _collation = new ComboBox { Name = "dbCollation", ItemsSource = collations, SelectedItem = preselected, HorizontalAlignment = HorizontalAlignment.Stretch, MaxDropDownHeight = 320 };
        _code = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 64,
            FontFamily = UiFonts.Monospace,
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"), Margin = new Thickness(16, 16, 16, 0) };
        AddRow(grid, 0, "Name:", _name);
        AddRow(grid, 1, "Collation:", _collation);
        var hint = new TextBlock { Text = "Servers default: " + (serverDefault ?? "?"), Foreground = Brushes.Gray, Margin = new Thickness(0, 2, 0, 8) };
        Grid.SetRow(hint, 2);
        Grid.SetColumn(hint, 1);
        grid.Children.Add(hint);
        AddRow(grid, 3, "CREATE code:", _code);

        _ok = new Button { Content = "OK", MinWidth = 75, IsDefault = true, IsEnabled = false, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Cancel", MinWidth = 75, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        _ok.Click += (_, _) => Close(new CreateDatabaseRequest(_name.Text!.Trim(), _collation.SelectedItem as string));
        cancel.Click += (_, _) => Close(null);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(16, 12, 16, 16) };
        buttons.Children.Add(_ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel();
        panel.Children.Add(grid);
        panel.Children.Add(buttons);
        Content = panel;

        _name.TextChanged += (_, _) => Update();
        _collation.SelectionChanged += (_, _) => Update();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close(null);
            }
        };
        Update();
    }

    private static void AddRow(Grid grid, int row, string label, Control field)
    {
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 12, 4) };
        field.Named(label);
        Grid.SetRow(text, row);
        Grid.SetRow(field, row);
        Grid.SetColumn(field, 1);
        field.Margin = new Thickness(0, 4, 0, 4);
        grid.Children.Add(text);
        grid.Children.Add(field);
    }

    private void Update()
    {
        var name = _name.Text?.Trim() ?? string.Empty;
        _ok.IsEnabled = name.Length > 0;
        _code.Text = _preview(name, _collation.SelectedItem as string);
    }

    public static Task<CreateDatabaseRequest?> ShowAsync(Window owner, IReadOnlyList<string> collations, string? preselected, string? serverDefault, Func<string, string?, string> preview) =>
        new CreateDatabaseDialog(collations, preselected, serverDefault, preview).ShowDialog<CreateDatabaseRequest?>(owner);
}
