using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Eunomia.UI.Controls.Common.Text;

namespace Eunomia.UI.Controls.Grid;

/// <summary>
/// Editor of a cell (appendix D B15; FAM-G07): creates the control that sits over the cell with the current value and
/// reads the value at the end. The grid's user chooses the editor per column (<see cref="DataGridControl.EditorProvider"/>).
/// </summary>
public abstract class GridCellEditor
{
    /// <summary>The editor uses ↑/↓ (the grid does not change rows with them while it is open).</summary>
    public virtual bool UsesArrows => false;

    /// <summary>Editor height in grid rows (the SET list grows downwards).</summary>
    public virtual int Rows => 1;

    public abstract Control Create(string? value, double fontSize, FontFamily font);

    /// <summary>Final value; <c>null</c> = no change.</summary>
    public abstract CellValue? Read(Control control, string? original);
}

/// <summary>Window editor (long text or text with line breaks, binary as hex): the grid asks its user for it.</summary>
public sealed class PopupCellEditor(bool binary) : GridCellEditor
{
    public bool IsBinary => binary;

    public override Control Create(string? value, double fontSize, FontFamily font) => throw new NotSupportedException();

    public override CellValue? Read(Control control, string? original) => null;
}

/// <summary>Single-line text, with the "…" button that asks for the window editor.</summary>
public sealed class TextCellEditor : GridCellEditor
{
    /// <summary>Click on "…" (the grid wires it to <see cref="DataGridControl.PopupEditRequested"/>).</summary>
    internal Action<string>? OnPopup { get; set; }

    public override Control Create(string? value, double fontSize, FontFamily font)
    {
        var box = new TextBox
        {
            Text = value ?? string.Empty,
            FontFamily = font,
            FontSize = fontSize,
            MinHeight = 0,
            MinWidth = 0,
            Padding = new Thickness(2, 0),
            BorderThickness = new Thickness(1),
            VerticalContentAlignment = VerticalAlignment.Center,
            AcceptsReturn = false,
        };
        if (OnPopup is { } popup)
        {
            var more = new Button { Content = "…", Padding = new Thickness(4, 0), MinHeight = 0, Focusable = false, VerticalAlignment = VerticalAlignment.Stretch };
            more.Click += (_, _) => popup(box.Text ?? string.Empty);
            box.InnerRightContent = more;
        }
        box.AttachedToVisualTree += (_, _) => box.SelectAll();
        return box;
    }

    public override CellValue? Read(Control control, string? original)
    {
        var text = ((TextBox)control).Text ?? string.Empty;
        return (original is null ? text.Length > 0 : text != original) ? new CellValue(text) : null;
    }
}

/// <summary>
/// List of values: ENUM, booleans, the foreign key list ("text (id)") or the functions of SQL function mode
/// (with free text and the value as an expression).
/// </summary>
public sealed class ChoiceCellEditor(IReadOnlyList<(string Value, string Caption)> items, bool allowText = false, bool isFunction = false) : GridCellEditor
{
    public IReadOnlyList<(string Value, string Caption)> Items { get; } = items;

    public bool IsFunction => isFunction;

    public override bool UsesArrows => true;

    public override Control Create(string? value, double fontSize, FontFamily font)
    {
        var combo = new ComboBox
        {
            ItemsSource = Items.Select(i => i.Caption).ToList(),
            IsEditable = allowText,
            FontFamily = font,
            FontSize = fontSize,
            MinHeight = 0,
            Padding = new Thickness(2, 0),
            MaxDropDownHeight = 16 * 20,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var index = Items.ToList().FindIndex(i => string.Equals(i.Value, value, StringComparison.Ordinal));
        if (index >= 0)
        {
            combo.SelectedIndex = index;
        }
        else if (allowText)
        {
            combo.Text = value ?? string.Empty;
        }
        combo.AttachedToVisualTree += (_, _) => combo.IsDropDownOpen = !allowText;
        return combo;
    }

    public override CellValue? Read(Control control, string? original)
    {
        var combo = (ComboBox)control;
        string? value;
        if (combo.SelectedIndex >= 0 && combo.SelectedIndex < Items.Count && (!allowText || combo.Text == Items[combo.SelectedIndex].Caption))
        {
            value = Items[combo.SelectedIndex].Value;
        }
        else if (allowText)
        {
            value = combo.Text ?? string.Empty;
        }
        else
        {
            return null;
        }
        return isFunction || !string.Equals(value, original, StringComparison.Ordinal) ? new CellValue(value, isFunction) : null;
    }
}

/// <summary>SET: one check box per value; the result is the checked ones separated by commas, in definition order.</summary>
public sealed class SetCellEditor(IReadOnlyList<string> items) : GridCellEditor
{
    public override bool UsesArrows => true;

    public override int Rows => Math.Clamp(items.Count, 1, 10);

    public override Control Create(string? value, double fontSize, FontFamily font)
    {
        var chosen = (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var panel = new StackPanel();
        foreach (var item in items)
        {
            // The value stays in Tag: Content is only the drawn text (a string lost its first "_" to AccessText).
            panel.Children.Add(new CheckBox { Content = LiteralText.Of(item), Tag = item, IsChecked = chosen.Contains(item), FontFamily = font, FontSize = fontSize, MinHeight = 0, Padding = new Thickness(2, 0) });
        }
        return new Border
        {
            Child = new ScrollViewer { Content = panel },
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Gray,
            Background = Brushes.White,
        };
    }

    public override CellValue? Read(Control control, string? original)
    {
        var panel = (StackPanel)((ScrollViewer)((Border)control).Child!).Content!;
        var value = string.Join(",", panel.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag!));
        return string.Equals(value, original ?? string.Empty, StringComparison.Ordinal) && original is not null ? null : new CellValue(value);
    }
}

/// <summary>
/// Date and time (appendix D B15, <c>TDateTimeEditorLink</c>): ↑/↓ change the segment under the cursor (year, month, day,
/// hour, minute, second, fractions), within the calendar limits; the rest of the text is typed by hand.
/// </summary>
public sealed class DateTimeCellEditor : GridCellEditor
{
    public override bool UsesArrows => true;

    public override Control Create(string? value, double fontSize, FontFamily font)
    {
        var box = (TextBox)new TextCellEditor().Create(value, fontSize, font);
        box.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Up or Key.Down && e.KeyModifiers == KeyModifiers.None)
            {
                var caret = box.CaretIndex;
                box.Text = Step(box.Text ?? string.Empty, caret, e.Key == Key.Up ? 1 : -1);
                box.CaretIndex = Math.Min(caret, box.Text.Length);
                e.Handled = true;
            }
        };
        return box;
    }

    public override CellValue? Read(Control control, string? original) => new TextCellEditor().Read(control, original);

    /// <summary>Adds <paramref name="delta"/> to the numeric segment where <paramref name="caret"/> is.</summary>
    public static string Step(string text, int caret, int delta)
    {
        ArgumentNullException.ThrowIfNull(text);
        var start = Math.Clamp(caret, 0, text.Length);
        if (start == text.Length || !char.IsAsciiDigit(text[start]))
        {
            start = Math.Max(0, start - 1);
        }
        if (text.Length == 0 || !char.IsAsciiDigit(text[start]))
        {
            return text;
        }
        var end = start;
        while (start > 0 && char.IsAsciiDigit(text[start - 1]))
        {
            start--;
        }
        while (end < text.Length && char.IsAsciiDigit(text[end]))
        {
            end++;
        }
        var segment = text[start..end];
        var number = long.Parse(segment, CultureInfo.InvariantCulture) + delta;
        // Limits per position in the yyyy-mm-dd hh:mm:ss format.
        var separator = start > 0 ? text[start - 1] : '\0';
        var isDate = text.IndexOf(' ', StringComparison.Ordinal) is var space && (space < 0 || start < space) && text.Contains('-', StringComparison.Ordinal);
        var dashBefore = text[..start].Count(c => c == '-');
        var colonBefore = text[..start].Count(c => c == ':');
        (long Min, long Max) range = separator switch
        {
            '-' when isDate && dashBefore == 1 => (1, 12),
            '-' when isDate && dashBefore == 2 => (1, 31),
            '.' => (0, (long)Math.Pow(10, segment.Length) - 1),
            ':' when colonBefore == 1 => (0, 59),
            ':' when colonBefore == 2 => (0, 59),
            ' ' => (0, 23),
            _ when !isDate && colonBefore == 0 && text.Contains(':', StringComparison.Ordinal) => (-838, 838),
            _ => (0, 9999),
        };
        number = number > range.Max ? range.Min : number < range.Min ? range.Max : number;
        var formatted = number < 0 ? "-" + (-number).ToString(new string('0', Math.Max(1, segment.Length)), CultureInfo.InvariantCulture)
            : number.ToString(new string('0', segment.Length), CultureInfo.InvariantCulture);
        return text[..start] + formatted + text[end..];
    }
}
