using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Eunomia.UI.Controls.Common.Brand;
using Eunomia.UI.Controls.Common.Theming;

namespace Eunomia.UI.Shell.Views;

/// <summary>Help › About (and "About" in the Session Manager): logo, name, version and the runtime environment.</summary>
public sealed class AboutDialog : Window
{
    /// <param name="runtime">Environment line; by default the running .NET version (the snapshots pin it).</param>
    public AboutDialog(string version, string? runtime = null)
    {
        Icon = BrandAssets.WindowIcon;
        Title = "About Eunomia";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = UiFonts.Ui;
        FontSize = 12;

        var logo = new Image { Source = BrandAssets.Logo, Width = 96, Height = 96, Name = "imgLogo", VerticalAlignment = VerticalAlignment.Top };
        var text = new StackPanel { Spacing = 4, Margin = new Thickness(20, 4, 0, 0), MaxWidth = 340 };
        text.Children.Add(new TextBlock { Text = "Eunomia", FontSize = 22, FontWeight = FontWeight.SemiBold });
        text.Children.Add(new TextBlock { Text = "Version " + version });
        text.Children.Add(new TextBlock
        {
            Text = "Database client for Windows, successor of HeidiSQL.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        });
        text.Children.Add(Themed.Bind(new TextBlock { Text = runtime ?? $".NET {Environment.Version}, Avalonia 12" }, TextBlock.ForegroundProperty, "text.muted"));

        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 20, 20, 12) };
        top.Children.Add(logo);
        top.Children.Add(text);

        var ok = new Button { Content = "OK", MinWidth = 75, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = true, IsCancel = true, Name = "btnOK" };
        ok.Click += (_, _) => Close();
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 0, 20, 16) };
        row.Children.Add(ok);

        var panel = new StackPanel();
        panel.Children.Add(top);
        panel.Children.Add(row);
        Content = panel;
    }

    public static Task ShowAsync(Window owner, string version) => new AboutDialog(version).ShowDialog(owner);
}
