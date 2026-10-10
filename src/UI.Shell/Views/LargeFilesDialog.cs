using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Eunomia.UI.Controls.Common.Brand;
using Eunomia.UI.Controls.Common.Theming;

namespace Eunomia.UI.Shell.Views;

public enum LargeFilesChoice
{
    Cancel,
    Run,
    Load,
}

/// <summary>
/// "Opening large files" (appendix B §4.1): one of the chosen files is over 5 MiB. Three options as large buttons, like
/// the command links of HeidiSQL's TaskDialog: run the files without opening them in the editor, open them in the
/// editor (may use a lot of memory) or cancel; the file list opens below.
/// </summary>
public sealed class LargeFilesDialog : Window
{
    public LargeFilesDialog(IReadOnlyList<(string Path, long Bytes)> files)
    {
        Icon = BrandAssets.WindowIcon;
        ArgumentNullException.ThrowIfNull(files);
        Title = "Opening large files";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = UiFonts.Ui;
        FontSize = 12;

        var total = files.Sum(f => f.Bytes);
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = "Selected files have a size of " + FormatSize(total),
            FontSize = 15,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x4E, 0x99)),
            TextWrapping = TextWrapping.Wrap,
        });
        var list = new TextBlock
        {
            Text = string.Join(Environment.NewLine, files.Select(f => f.Path + "  (" + FormatSize(f.Bytes) + ")")),
            TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(new Expander { Header = "Files", Content = list, HorizontalAlignment = HorizontalAlignment.Stretch });
        panel.Children.Add(Link("Run file(s) directly", "... without loading into the editor", LargeFilesChoice.Run));
        panel.Children.Add(Link("Load file(s) into the editor", "Can cause large memory usage", LargeFilesChoice.Load));
        panel.Children.Add(Link("Cancel", null, LargeFilesChoice.Cancel));
        Content = panel;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close(LargeFilesChoice.Cancel);
            }
        };
    }

    private Button Link(string caption, string? note, LargeFilesChoice choice)
    {
        var content = new StackPanel { Spacing = 1 };
        content.Children.Add(new TextBlock { Text = "➜  " + caption, FontSize = 13 });
        if (note is not null)
        {
            content.Children.Add(new TextBlock { Text = note, Margin = new Thickness(22, 0, 0, 0), Foreground = Brushes.Gray });
        }
        var button = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 6),
            IsCancel = choice == LargeFilesChoice.Cancel,
        };
        button.Click += (_, _) => Close(choice);
        return button;
    }

    internal static string FormatSize(long bytes) => bytes >= 1024L * 1024 * 1024
        ? string.Create(CultureInfo.CurrentCulture, $"{bytes / (1024.0 * 1024 * 1024):0.0} GiB")
        : string.Create(CultureInfo.CurrentCulture, $"{bytes / (1024.0 * 1024):0.0} MiB");

    public static async Task<LargeFilesChoice> ShowAsync(Window owner, IReadOnlyList<(string Path, long Bytes)> files) =>
        await new LargeFilesDialog(files).ShowDialog<LargeFilesChoice?>(owner) ?? LargeFilesChoice.Cancel;
}
