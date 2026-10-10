using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Eunomia.UI.Controls.Common.Accessibility;
using Eunomia.UI.Controls.Common.Brand;
using Eunomia.UI.Controls.Common.Theming;

namespace Eunomia.UI.Controls.Common.Dialogs;

[Flags]
public enum MessageDialogButtons
{
    Ok = 1,
    Cancel = 2,
    Yes = 4,
    No = 8,
    Ignore = 16,
    OkCancel = Ok | Cancel,
    YesNo = Yes | No,
    YesNoCancel = Yes | No | Cancel,
}

public enum MessageDialogResult
{
    None,
    Ok,
    Cancel,
    Yes,
    No,
    Ignore,
}

/// <summary>Simple modal dialog (errors of user operations open a dialog, section 21.5).</summary>
public sealed class MessageDialog : Window
{
    private MessageDialogResult _result = MessageDialogResult.None;

    public MessageDialog(string title, string message, MessageDialogButtons buttons, string? verification = null)
    {
        Icon = BrandAssets.WindowIcon;
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        MaxWidth = 640;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = UiFonts.Ui;
        FontSize = 12;

        var text = new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 600, Margin = new Thickness(16, 16, 16, 12) };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(16, 0, 16, 16) };
        foreach (var (flag, caption, result) in new[]
                 {
                     (MessageDialogButtons.Yes, "Yes", MessageDialogResult.Yes),
                     (MessageDialogButtons.No, "No", MessageDialogResult.No),
                     (MessageDialogButtons.Ok, "OK", MessageDialogResult.Ok),
                     (MessageDialogButtons.Ignore, "Ignore", MessageDialogResult.Ignore),
                     (MessageDialogButtons.Cancel, "Cancel", MessageDialogResult.Cancel),
                 })
        {
            if (!buttons.HasFlag(flag))
            {
                continue;
            }
            var button = new Button { Content = caption, MinWidth = 75, HorizontalContentAlignment = HorizontalAlignment.Center, Name = "btn" + caption };
            button.IsDefault = result is MessageDialogResult.Ok or MessageDialogResult.Yes;
            button.IsCancel = result is MessageDialogResult.Cancel || (result == MessageDialogResult.No && !buttons.HasFlag(MessageDialogButtons.Cancel));
            button.Click += (_, _) =>
            {
                _result = result;
                Close(result);
            };
            row.Children.Add(button);
        }
        var panel = new StackPanel();
        panel.Children.Add(text);
        if (verification is not null)
        {
            // TaskDialog check box ("Disable foreign key checks", appendix B §6).
            Verification = new CheckBox { Content = verification, Margin = new Thickness(16, 0, 16, 12) };
            panel.Children.Add(Verification);
        }
        panel.Children.Add(row);
        Content = panel;
    }

    public CheckBox? Verification { get; }

    public static Task<MessageDialogResult> ShowAsync(Window owner, string title, string message, MessageDialogButtons buttons)
    {
        var dialog = new MessageDialog(title, message, buttons);
        return dialog.ShowDialog<MessageDialogResult>(owner);
    }

    /// <summary>Like <see cref="ShowAsync"/>, with a check box; also returns its state.</summary>
    public static async Task<(MessageDialogResult Result, bool Verified)> ShowWithVerificationAsync(Window owner, string title, string message, MessageDialogButtons buttons, string verification)
    {
        var dialog = new MessageDialog(title, message, buttons, verification);
        var result = await dialog.ShowDialog<MessageDialogResult>(owner).ConfigureAwait(true);
        return (result, dialog.Verification!.IsChecked == true);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_result == MessageDialogResult.None)
        {
            _result = MessageDialogResult.Cancel;
        }
    }
}

/// <summary>Asks for a text (Rename query tab, Set delimiter…).</summary>
public sealed class InputDialog : Window
{
    private readonly TextBox _box;

    public InputDialog(string title, string prompt, string value)
    {
        Icon = BrandAssets.WindowIcon;
        Title = title;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = UiFonts.Ui;
        FontSize = 12;
        _box = new TextBox { Text = value, Margin = new Thickness(0, 6, 0, 0) }.Named(prompt);
        _box.AttachedToVisualTree += (_, _) =>
        {
            _box.Focus();
            _box.SelectAll();
        };
        var ok = new Button { Content = "OK", MinWidth = 75, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Cancel", MinWidth = 75, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Click += (_, _) => Close(_box.Text);
        cancel.Click += (_, _) => Close(null);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = prompt });
        panel.Children.Add(_box);
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

    public static Task<string?> ShowAsync(Window owner, string title, string prompt, string value) =>
        new InputDialog(title, prompt, value).ShowDialog<string?>(owner);
}
