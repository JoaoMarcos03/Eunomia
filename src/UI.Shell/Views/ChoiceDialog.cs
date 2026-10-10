using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Eunomia.UI.Controls.Common.Accessibility;
using Eunomia.UI.Controls.Common.Brand;
using Eunomia.UI.Controls.Common.Theming;

namespace Eunomia.UI.Shell.Views;

/// <summary>Choice of one value from a list (the Load SQL file encoding, appendix C §4.3).</summary>
public sealed class ChoiceDialog : Window
{
    private readonly ComboBox _choices;

    public ChoiceDialog(string title, string prompt, IReadOnlyList<string> items, string? selected)
    {
        Icon = BrandAssets.WindowIcon;
        ArgumentNullException.ThrowIfNull(items);
        Title = title;
        Width = 360;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = UiFonts.Ui;
        FontSize = 12;
        _choices = new ComboBox { ItemsSource = items, SelectedItem = selected ?? items.FirstOrDefault(), HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 6, 0, 0) }.Named(prompt);
        var ok = new Button { Content = "OK", MinWidth = 75, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Cancel", MinWidth = 75, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Click += (_, _) => Close(_choices.SelectedItem as string);
        cancel.Click += (_, _) => Close(null);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = prompt });
        panel.Children.Add(_choices);
        panel.Children.Add(buttons);
        Content = panel;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close(null);
            }
        };
    }

    public static Task<string?> ShowAsync(Window owner, string title, string prompt, IReadOnlyList<string> items, string? selected) =>
        new ChoiceDialog(title, prompt, items, selected).ShowDialog<string?>(owner);
}
