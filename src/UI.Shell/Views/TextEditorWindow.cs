using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Eunomia.Engine.Sql.Formatting;
using Eunomia.UI.Controls.Common.Accessibility;
using Eunomia.UI.Controls.Common.Brand;
using Eunomia.UI.Controls.Common.Dialogs;
using Eunomia.UI.Controls.Common.Theming;
using Eunomia.UI.Controls.SqlEditor;

namespace Eunomia.UI.Shell.Views;

/// <summary>
/// Window cell editor (appendix D B15, appendix C §3.18, <c>texteditor.pas</c>): AvaloniaEdit with word wrap (on by
/// default), highlighting picked in the "Syntax" combo (Text, JSON, XML, HTML, SQL; the initial one comes from the column
/// or the value) and "Format" to indent JSON, XML or SQL when the user asks; the status line "N characters (max: M),
/// L lines, cursor at y:x". Ctrl+Enter applies, Esc cancels, closing with changes asks "Apply modifications?". For
/// binary, the text is hex (<c>0x…</c>) and only accepts hexadecimal digits. The text returned is the editor's as it is:
/// opening, changing the highlighting or the word wrap does not change the value.
/// </summary>
public sealed partial class TextEditorWindow : Window
{
    [GeneratedRegex("^(0x)?[0-9a-fA-F]*$", RegexOptions.CultureInvariant)]
    private static partial Regex HexText();

    private readonly TextValueEditor _editor = new();
    private readonly ComboBox _syntax;
    private readonly Button _format = new() { Content = "Format", MinWidth = 70, HorizontalContentAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _status = new() { Margin = new Thickness(4, 2), Foreground = Brushes.Gray, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly string _original;
    private readonly long? _maxLength;
    private readonly bool _binary;
    private bool _answered;

    public TextEditorWindow(string title, string? value, long? maxLength, bool binary, TextSyntax? syntax = null, string? nativeType = null)
    {
        Icon = BrandAssets.WindowIcon;
        _original = value ?? string.Empty;
        _maxLength = maxLength;
        _binary = binary;
        Title = title;
        Width = 720;
        Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = UiFonts.Ui;
        FontSize = 12;

        _editor.Text = _original;
        // The caret starts at the beginning, as in HeidiSQL (the new document left it at the end).
        _editor.Editor.CaretOffset = 0;
        _editor.Editor.Named(title);
        _editor.AttachedToVisualTree += (_, _) => _editor.Editor.TextArea.Focus();
        var wrap = new CheckBox { Content = "Wrap long lines", IsChecked = true, Margin = new Thickness(4, 0) };
        wrap.IsCheckedChanged += (_, _) => _editor.WordWrap = wrap.IsChecked == true;
        _syntax = new ComboBox { ItemsSource = TextSyntaxes.All.Select(s => s.Caption).ToList(), MinWidth = 80, IsEnabled = !binary }.Named("Syntax");
        _syntax.SelectedIndex = (int)(binary ? TextSyntax.Text : syntax ?? TextSyntaxes.Detect(_original, nativeType));
        _editor.Syntax = Syntax;
        _syntax.SelectionChanged += (_, _) =>
        {
            _editor.Syntax = Syntax;
            SyntaxChosen = true;
            UpdateFormat();
            UpdateStatus();
        };
        ToolTip.SetTip(_format, "Indent the text once (JSON, XML or SQL)");
        _format.Click += (_, _) => Format();
        UpdateFormat();

        var ok = new Button { Content = "OK", MinWidth = 75, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Cancel", MinWidth = 75, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Click += (_, _) => Apply();
        cancel.Click += (_, _) => Cancel();
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        tools.Children.Add(wrap);
        tools.Children.Add(new TextBlock { Text = "Syntax:", VerticalAlignment = VerticalAlignment.Center });
        tools.Children.Add(_syntax);
        tools.Children.Add(_format);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var bottom = new DockPanel { Margin = new Thickness(4) };
        DockPanel.SetDock(buttons, Dock.Right);
        DockPanel.SetDock(tools, Dock.Left);
        bottom.Children.Add(buttons);
        bottom.Children.Add(tools);
        bottom.Children.Add(new Control());
        var root = new DockPanel();
        DockPanel.SetDock(_status, Dock.Bottom);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(_status);
        root.Children.Add(bottom);
        root.Children.Add(_editor);
        Content = root;

        _editor.Editor.Document.TextChanged += (_, _) => UpdateStatus();
        _editor.Editor.TextArea.Caret.PositionChanged += (_, _) => UpdateStatus();
        // Tunnelling: AvaloniaEdit handles Enter and Esc before the window sees them.
        AddHandler(KeyDownEvent, (_, e) =>
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
        }, RoutingStrategies.Tunnel);
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

    /// <summary>Current text (tests).</summary>
    public string Text
    {
        get => _editor.Text;
        set => _editor.Text = value;
    }

    /// <summary>Highlighting picked in the combo.</summary>
    public TextSyntax Syntax => _syntax.SelectedIndex >= 0 ? TextSyntaxes.All[_syntax.SelectedIndex].Syntax : TextSyntax.Text;

    /// <summary>The user changed the highlighting (it is saved for the column).</summary>
    public bool SyntaxChosen { get; private set; }

    /// <summary>Status line text (tests).</summary>
    public string Status => _status.Text ?? string.Empty;

    /// <summary>Editor (tests).</summary>
    internal TextValueEditor ValueEditor => _editor;

    /// <summary>Changes the highlighting as the combo does (tests).</summary>
    internal void SelectSyntax(TextSyntax syntax) => _syntax.SelectedIndex = (int)syntax;

    /// <summary>Format: indents once; invalid text stays the same and the status line says why.</summary>
    internal void Format()
    {
        if (!TextSyntaxes.TryFormat(Text, Syntax, out var formatted, out var error))
        {
            _status.Text = error;
            return;
        }
        if (!string.Equals(formatted, Text, StringComparison.Ordinal))
        {
            // A single undo step (Ctrl+Z goes back to the previous text).
            var document = _editor.Editor.Document;
            document.Replace(0, document.TextLength, formatted);
            _editor.Editor.CaretOffset = 0;
        }
    }

    private void UpdateFormat() => _format.IsEnabled = !_binary && TextSyntaxes.CanFormat(Syntax);

    private void UpdateStatus()
    {
        var document = _editor.Editor.Document;
        var caret = _editor.Editor.TextArea.Caret;
        var max = _maxLength is { } m ? m.ToString("N0", CultureInfo.CurrentCulture) : "unknown";
        var text = string.Create(CultureInfo.CurrentCulture, $"{document.TextLength:N0} characters (max: {max}), {document.LineCount:N0} lines, cursor at {caret.Line}:{caret.Column}");
        if (Syntax != TextSyntax.Text && _editor.HasUnhighlightedLines)
        {
            text += ". Long lines are not highlighted";
            if (TextSyntaxes.CanFormat(Syntax))
            {
                text += " (Format splits them)";
            }
        }
        _status.Text = text;
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

    /// <summary>
    /// Opens the editor and returns the text (or <c>null</c> on Cancel). <paramref name="syntax"/> is the highlighting
    /// saved for the column (otherwise it is detected from <paramref name="nativeType"/> and the value);
    /// <paramref name="syntaxChosen"/> receives the highlighting when the user changes it.
    /// </summary>
    public static async Task<string?> ShowAsync(Window owner, string title, string? value, long? maxLength, bool binary,
        TextSyntax? syntax = null, string? nativeType = null, Action<TextSyntax>? syntaxChosen = null)
    {
        var window = new TextEditorWindow(title, value, maxLength, binary, syntax, nativeType);
        var result = await window.ShowDialog<string?>(owner);
        if (window.SyntaxChosen)
        {
            syntaxChosen?.Invoke(window.Syntax);
        }
        return result;
    }
}
