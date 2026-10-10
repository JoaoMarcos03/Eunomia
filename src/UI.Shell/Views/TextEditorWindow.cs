using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Eunomia.UI.Controls.Common.Accessibility;
using Eunomia.UI.Controls.Common.Brand;
using Eunomia.UI.Controls.Common.Dialogs;
using Eunomia.UI.Controls.Common.Theming;

namespace Eunomia.UI.Shell.Views;

/// <summary>
/// Editor de célula em janela (anexo D B15, <c>texteditor.pas</c>): texto com quebra de linhas opcional e a linha de
/// estado "N characters (max: M), L lines, cursor at y:x"; Ctrl+Enter aplica, Esc cancela, fechar com alterações
/// pergunta "Apply modifications?". Em binário, o texto é hex (<c>0x…</c>) e só aceita dígitos hexadecimais.
/// </summary>
public sealed partial class TextEditorWindow : Window
{
    [GeneratedRegex("^(0x)?[0-9a-fA-F]*$", RegexOptions.CultureInvariant)]
    private static partial Regex HexText();

    private readonly TextBox _text;
    private readonly TextBlock _status = new() { Margin = new Thickness(4, 2), Foreground = Brushes.Gray };
    private readonly string _original;
    private readonly long? _maxLength;
    private readonly bool _binary;
    private bool _answered;

    public TextEditorWindow(string title, string? value, long? maxLength, bool binary)
    {
        Icon = BrandAssets.WindowIcon;
        _original = value ?? string.Empty;
        _maxLength = maxLength;
        _binary = binary;
        Title = title;
        Width = 640;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = UiFonts.Ui;
        FontSize = 12;

        _text = new TextBox
        {
            Text = _original,
            AcceptsReturn = !binary,
            AcceptsTab = !binary,
            TextWrapping = binary ? TextWrapping.Wrap : TextWrapping.NoWrap,
            FontFamily = UiFonts.Monospace,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        _text.Named(title);
        _text.AttachedToVisualTree += (_, _) => _text.Focus();
        var wrap = new CheckBox { Content = "Wrap long lines", IsChecked = binary, Margin = new Thickness(4, 0) };
        wrap.IsCheckedChanged += (_, _) => _text.TextWrapping = wrap.IsChecked == true ? TextWrapping.Wrap : TextWrapping.NoWrap;
        var ok = new Button { Content = "OK", MinWidth = 75, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Cancel", MinWidth = 75, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Click += (_, _) => Apply();
        cancel.Click += (_, _) => Cancel();
        var bottom = new DockPanel { Margin = new Thickness(4) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Right);
        DockPanel.SetDock(wrap, Dock.Left);
        bottom.Children.Add(buttons);
        bottom.Children.Add(wrap);
        bottom.Children.Add(_status);
        var root = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(_text);
        Content = root;

        _text.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty || e.Property == TextBox.CaretIndexProperty)
            {
                UpdateStatus();
            }
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Control)
            {
                e.Handled = true;
                Apply();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Cancel();
            }
        };
        Closing += async (_, e) =>
        {
            if (_answered || Text == _original)
            {
                return;
            }
            e.Cancel = true;
            var answer = await MessageDialog.ShowAsync(this, "Confirm", "Apply modifications?", MessageDialogButtons.YesNo | MessageDialogButtons.Cancel);
            if (answer == MessageDialogResult.Yes)
            {
                Apply();
            }
            else if (answer == MessageDialogResult.No)
            {
                Cancel();
            }
        };
        UpdateStatus();
    }

    /// <summary>Texto atual (testes).</summary>
    public string Text
    {
        get => _text.Text ?? string.Empty;
        set => _text.Text = value;
    }

    /// <summary>Texto da linha de estado (testes).</summary>
    public string Status => _status.Text ?? string.Empty;

    private void UpdateStatus()
    {
        var text = Text;
        var caret = Math.Clamp(_text.CaretIndex, 0, text.Length);
        var before = text.AsSpan(0, caret);
        var line = before.Count('\n') + 1;
        var column = caret - (before.LastIndexOf('\n') + 1) + 1;
        var lines = text.AsSpan().Count('\n') + 1;
        var max = _maxLength is { } m ? m.ToString("N0", CultureInfo.CurrentCulture) : "unknown";
        _status.Text = string.Create(CultureInfo.CurrentCulture, $"{text.Length:N0} characters (max: {max}), {lines:N0} lines, cursor at {line}:{column}");
    }

    private void Apply()
    {
        var text = Text;
        if (_binary && !HexText().IsMatch(text.Trim()))
        {
            _ = MessageDialog.ShowAsync(this, "Error", "Binary values must be hexadecimal digits (0-9, A-F).", MessageDialogButtons.Ok);
            return;
        }
        _answered = true;
        var value = _binary ? text.Trim() : text;
        if (_binary && !value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            value = "0x" + value;
        }
        Close(value);
    }

    private void Cancel()
    {
        _answered = true;
        Close(null);
    }

    public static Task<string?> ShowAsync(Window owner, string title, string? value, long? maxLength, bool binary) =>
        new TextEditorWindow(title, value, maxLength, binary).ShowDialog<string?>(owner);
}
