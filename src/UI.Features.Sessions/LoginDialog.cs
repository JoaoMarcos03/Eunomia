using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Eunomia.App.Sessions;
using Eunomia.UI.Controls.Common.Accessibility;
using Eunomia.UI.Controls.Common.Brand;
using Eunomia.UI.Controls.Common.Theming;

namespace Eunomia.UI.Features.Sessions;

/// <summary>
/// Login ("Prompt for credentials" ou password não gravada; anexo A §6): utilizador e password, preenchidos com os
/// da sessão. Os valores não são gravados na sessão.
/// </summary>
public sealed class LoginDialog : Window
{
    private readonly TextBox _user;
    private readonly TextBox _password;

    public LoginDialog(string session, string user)
    {
        Icon = BrandAssets.WindowIcon;
        Title = "Login - " + session;
        Width = 360;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = UiFonts.Ui;
        FontSize = 12;
        _user = new TextBox { Text = user, MinHeight = 24 };
        _password = new TextBox { PasswordChar = '•', MinHeight = 24, Name = "password" };
        var ok = new Button { Content = "Login", IsDefault = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Click += (_, _) => Close(new LoginCredentials(_user.Text ?? string.Empty, _password.Text ?? string.Empty));
        cancel.Click += (_, _) => Close(null);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), Margin = new Thickness(16) };
        void Row(int r, string label, Control control)
        {
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            control.Named(label);
            Grid.SetRow(text, r);
            Grid.SetRow(control, r);
            Grid.SetColumn(control, 1);
            control.Margin = new Thickness(0, 3);
            grid.Children.Add(text);
            grid.Children.Add(control);
        }
        Row(0, "Username:", _user);
        Row(1, "Password:", _password);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 2);
        Grid.SetColumnSpan(buttons, 2);
        grid.Children.Add(buttons);
        Content = grid;
        Opened += (_, _) => (string.IsNullOrEmpty(user) ? _user : _password).Focus();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close(null);
            }
        };
    }

    public static Task<LoginCredentials?> ShowAsync(Window owner, string session, string user) =>
        new LoginDialog(session, user).ShowDialog<LoginCredentials?>(owner);
}
