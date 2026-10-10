using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Eunomia.UI.Controls.Common.Accessibility;
using Eunomia.UI.Controls.Common.Dialogs;

namespace Eunomia.UI.Features.Sessions;

/// <summary>Formulário de duas colunas (rótulo / controlo) dos tabs do Session Manager.</summary>
internal sealed class FormBuilder
{
    private readonly Grid _grid = new() { ColumnDefinitions = new ColumnDefinitions("170,*"), Margin = new Thickness(8, 8, 8, 4) };

    public Control Root => new ScrollViewer { Content = _grid, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

    public T Row<T>(string? label, T control, double topSpacing = 0)
        where T : Control
    {
        var row = _grid.RowDefinitions.Count;
        _grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        if (label is not null)
        {
            control.Named(label);
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2 + topSpacing, 8, 2) };
            Grid.SetRow(text, row);
            _grid.Children.Add(text);
        }
        control.Margin = new Thickness(0, 2 + topSpacing, 0, 2);
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        _grid.Children.Add(control);
        return control;
    }

    public CheckBox Check(string caption, double topSpacing = 0) => Row(null, new CheckBox { Content = caption, MinHeight = 22, Padding = new Thickness(4, 0, 0, 0) }, topSpacing);

    public TextBox Text(string label, string? placeholder = null, bool password = false) =>
        Row(label, new TextBox { PlaceholderText = placeholder, PasswordChar = password ? '•' : default, MinHeight = 24, Padding = new Thickness(4, 2) });

    public NumericUpDown Number(string label, int min, int max) =>
        Row(label, new NumericUpDown { Minimum = min, Maximum = max, Increment = 1, FormatString = "0", Width = 120, HorizontalAlignment = HorizontalAlignment.Left, MinHeight = 24 });

    /// <summary>Caixa de texto com botão "..." que abre o seletor de ficheiros.</summary>
    public TextBox File(string label, string title, Func<TopLevel?> topLevel, params FilePickerFileType[] types)
    {
        var box = new TextBox { MinHeight = 24, Padding = new Thickness(4, 2) }.Named(label);
        var browse = new Button { Content = "...", Width = 28, Margin = new Thickness(4, 0, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Center }.Named("Browse");
        browse.Click += async (_, _) =>
        {
            if (topLevel() is not { } owner)
            {
                return;
            }
            var files = await PathPicker.OpenFilesAsync(owner, new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = [.. types, FilePickerFileTypes.All],
            });
            if (files.Count > 0)
            {
                box.Text = files[0];
            }
        };
        var dock = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        dock.Children.Add(browse);
        dock.Children.Add(box);
        Row(label, dock);
        return box;
    }
}
