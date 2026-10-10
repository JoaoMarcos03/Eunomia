using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Eunomia.UI.Controls.Common.Text;

namespace Eunomia.UI.Controls.Grid;

/// <summary>
/// Editor de uma célula (anexo D B15; FAM-G07): cria o controlo que fica por cima da célula com o valor atual e lê o
/// valor no fim. Quem usa o grid escolhe o editor por coluna (<see cref="DataGridControl.EditorProvider"/>).
/// </summary>
public abstract class GridCellEditor
{
    /// <summary>O editor usa ↑/↓ (o grid não muda de linha com elas enquanto ele está aberto).</summary>
    public virtual bool UsesArrows => false;

    /// <summary>Altura do editor em linhas do grid (a lista do SET cresce para baixo).</summary>
    public virtual int Rows => 1;

    public abstract Control Create(string? value, double fontSize, FontFamily font);

    /// <summary>Valor final; <c>null</c> = sem alteração.</summary>
    public abstract CellValue? Read(Control control, string? original);
}

/// <summary>Editor em janela (texto longo ou com quebras de linha, binário em hex): o grid pede-o a quem o usa.</summary>
public sealed class PopupCellEditor(bool binary) : GridCellEditor
{
    public bool IsBinary => binary;

    public override Control Create(string? value, double fontSize, FontFamily font) => throw new NotSupportedException();

    public override CellValue? Read(Control control, string? original) => null;
}

/// <summary>Texto numa linha, com o botão "…" que pede o editor em janela.</summary>
public sealed class TextCellEditor : GridCellEditor
{
    /// <summary>Clique no "…" (o grid liga-o ao <see cref="DataGridControl.PopupEditRequested"/>).</summary>
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
/// Lista de valores: ENUM, booleanos, a lista da chave estrangeira ("texto (id)") ou as funções do modo SQL function
/// (com texto livre e o valor como expressão).
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

/// <summary>SET: uma caixa por valor; o resultado são os escolhidos separados por vírgulas, pela ordem da definição.</summary>
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
            // O valor fica no Tag: o Content é só o texto desenhado (uma string perdia o primeiro "_" no AccessText).
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
/// Data e hora (anexo D B15, <c>TDateTimeEditorLink</c>): ↑/↓ mudam o segmento debaixo do cursor (ano, mês, dia, hora,
/// minuto, segundo, frações), com os limites do calendário; o resto do texto escreve-se à mão.
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

    /// <summary>Soma <paramref name="delta"/> ao segmento numérico onde está <paramref name="caret"/>.</summary>
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
        // Limites por posição no formato yyyy-mm-dd hh:mm:ss.
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
