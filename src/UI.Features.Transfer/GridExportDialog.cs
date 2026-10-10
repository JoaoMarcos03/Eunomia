using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Eunomia.App.Commands;
using Eunomia.App.Export;
using Eunomia.App.Settings;
using Eunomia.UI.Controls.Common.Accessibility;
using Eunomia.UI.Controls.Common.Brand;
using Eunomia.UI.Controls.Common.Dialogs;
using Eunomia.UI.Controls.Common.Text;
using Eunomia.UI.Controls.Common.Theming;

namespace Eunomia.UI.Features.Transfer;

/// <summary>O que o diálogo Export grid rows pediu (o shell lê as linhas e escreve).</summary>
public sealed record GridExportRequest(
    GridExportFormat Format,
    bool ToClipboard,
    string FileName,
    string Encoding,
    bool SelectedOnly,
    GridExportOptions Options,
    bool FocusedColumnOnly,
    bool IncludeQuery,
    bool OpenFile);

/// <summary>
/// Export grid rows (anexo B §1.4; FAM-C05): clipboard ou ficheiro (placeholders, 10 recentes, codificação), formato,
/// linhas selecionadas ou todas as carregadas, opções e separadores do Delimited text, e "Save clipboard settings" (as
/// opções do Copy as, separadas das do diálogo). As escolhas ficam gravadas no OK.
/// </summary>
public sealed class GridExportDialog : Window
{
    private static readonly string[] Placeholders = ["%session", "%host", "%u", "%db", "%date", "%d", "%m", "%y", "%h", "%i", "%s"];

    private readonly ExportOptions _settings;
    private readonly Func<string, string> _expand;
    private readonly RadioButton _toClipboard = new() { Content = "Copy to clipboard", GroupName = "target" };
    private readonly RadioButton _toFile = new() { Content = "File", GroupName = "target" };
    private readonly TextBox _fileName = new TextBox { PlaceholderText = "Doubleclick to select file", MinHeight = 22, Padding = new Thickness(4, 1) }.Named("Filename");
    private readonly ComboBox _encoding = new() { ItemsSource = ExportFile.Encodings, MinWidth = 160 };
    private readonly ComboBox _format = new ComboBox { ItemsSource = HeidiSqlCatalog.CopyAsFormats.Select(f => f.Caption).ToList(), MinWidth = 200 }.Named("Output format");
    private readonly RadioButton _selection = new() { GroupName = "rows" };
    private readonly RadioButton _complete = new() { GroupName = "rows" };
    private readonly CheckBox _columnNames = new() { Content = "Include column names" };
    private readonly CheckBox _autoIncrement = new() { Content = "Include auto increment column" };
    private readonly CheckBox _focusedOnly = new();
    private readonly CheckBox _includeQuery = new() { Content = "Include SQL query" };
    private readonly CheckBox _removeLinebreaks = new() { Content = "Remove linebreaks from contents" };
    private readonly CheckBox _openFile = new() { Content = "Open file after creation" };
    private readonly TextBox _separator = SmallBox();
    private readonly TextBox _encloser = SmallBox();
    private readonly TextBox _terminator = SmallBox();
    private readonly TextBox _null = SmallBox();

    public GridExportDialog(ExportOptions settings, long selectedRows, long allRows, string? focusedColumn, Func<string, string> expandFileName)
    {
        Icon = BrandAssets.WindowIcon;
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(expandFileName);
        _settings = settings;
        _expand = expandFileName;
        Title = "Export grid rows";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = UiFonts.Ui;
        FontSize = 12;

        var c = CultureInfo.CurrentCulture;
        _selection.Content = string.Create(c, $"Selection ({selectedRows:N0} rows)");
        _complete.Content = string.Create(c, $"Complete ({allRows:N0} rows)");
        _focusedOnly.Content = $"Only focused column ({focusedColumn ?? "-"})";
        _focusedOnly.IsEnabled = focusedColumn is not null;

        // Valores gravados.
        _toClipboard.IsChecked = settings.OutputToClipboard;
        _toFile.IsChecked = !settings.OutputToClipboard;
        _fileName.Text = settings.FileName;
        _encoding.SelectedItem = ExportFile.Encodings.Contains(settings.Encoding) ? settings.Encoding : ExportFile.DefaultEncoding;
        _format.SelectedIndex = Math.Clamp(settings.Format, 0, HeidiSqlCatalog.CopyAsFormats.Count - 1);
        _selection.IsChecked = settings.SelectedRowsOnly && selectedRows > 0;
        _complete.IsChecked = !(_selection.IsChecked ?? false);
        _columnNames.IsChecked = settings.ColumnNames;
        _autoIncrement.IsChecked = settings.AutoIncrement;
        _focusedOnly.IsChecked = settings.FocusedColumnOnly && focusedColumn is not null;
        _includeQuery.IsChecked = settings.IncludeQuery;
        _removeLinebreaks.IsChecked = settings.RemoveLinebreaks;
        _openFile.IsChecked = settings.OpenFile;
        _separator.Text = settings.Separator;
        _encloser.Text = settings.Encloser;
        _terminator.Text = settings.Terminator;
        _null.Text = settings.NullValue;

        // Escrever no nome escolhe "File"; o formato muda a extensão e a extensão escolhe o formato.
        _fileName.TextChanged += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_fileName.Text))
            {
                _toFile.IsChecked = true;
            }
            UpdateEnabled();
        };
        _fileName.LostFocus += (_, _) =>
        {
            if (ExportFile.FormatOf(_fileName.Text ?? string.Empty) is { } format && Format != format)
            {
                _format.SelectedIndex = (int)format;
            }
        };
        _fileName.DoubleTapped += async (_, _) => await BrowseAsync();
        _format.SelectionChanged += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_fileName.Text))
            {
                _fileName.Text = ExportFile.WithExtension(_fileName.Text, Format);
            }
            UpdateEnabled();
        };
        _toClipboard.IsCheckedChanged += (_, _) => UpdateEnabled();

        var browse = new Button { Content = "...", Padding = new Thickness(6, 1) };
        browse.Click += async (_, _) => await BrowseAsync();
        var recent = new Button { Content = "▾", Padding = new Thickness(4, 1) };
        recent.Flyout = new MenuFlyout { ItemsSource = RecentMenu(settings.RecentFiles) };
        var fileRow = new DockPanel();
        DockPanel.SetDock(recent, Dock.Left);
        DockPanel.SetDock(browse, Dock.Right);
        fileRow.Children.Add(recent);
        fileRow.Children.Add(browse);
        fileRow.Children.Add(_fileName);

        var output = Section("Output target",
            _toClipboard,
            Row(_toFile, fileRow),
            Row(new TextBlock { Text = "Encoding:", VerticalAlignment = VerticalAlignment.Center }, _encoding));
        var format = Section("Output format", _format);
        var rows = Section("Row selection", _selection, _complete);
        var options = Section("Options", _columnNames, _autoIncrement, _focusedOnly, _includeQuery, _removeLinebreaks, _openFile);
        var separators = Section("Delimited text",
            Row(Label("Field separator:"), _separator),
            Row(Label("Encloser:"), _encloser),
            Row(Label("Line terminator:"), _terminator),
            Row(Label("NULL value:"), _null));

        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 75, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Click += async (_, _) => await AcceptAsync();
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 75, HorizontalContentAlignment = HorizontalAlignment.Center };
        cancel.Click += (_, _) => Close(null);
        var saveClipboard = new Button { Content = "Save clipboard settings" };
        ToolTip.SetTip(saveClipboard, "Use these options for \"Copy as\"");
        saveClipboard.Click += (_, _) => SaveClipboardSettings();
        var buttons = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(saveClipboard, Dock.Left);
        buttons.Children.Add(saveClipboard);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(ok);
        right.Children.Add(cancel);
        buttons.Children.Add(right);

        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*") };
        var left = new StackPanel { Spacing = 8 };
        left.Children.Add(format);
        left.Children.Add(rows);
        left.Children.Add(separators);
        var rightColumn = new StackPanel { Spacing = 8 };
        rightColumn.Children.Add(options);
        Grid.SetColumn(rightColumn, 2);
        columns.Children.Add(left);
        columns.Children.Add(rightColumn);

        var panel = new StackPanel { Margin = new Thickness(12), Spacing = 8 };
        panel.Children.Add(output);
        panel.Children.Add(columns);
        panel.Children.Add(buttons);
        Content = panel;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close(null);
            }
        };
        UpdateEnabled();
    }

    private GridExportFormat Format => (GridExportFormat)Math.Max(0, _format.SelectedIndex);

    private static TextBox SmallBox() => new() { Width = 80, MinHeight = 22, Padding = new Thickness(4, 1) };

    private static TextBlock Label(string text) => new() { Text = text, Width = 110, VerticalAlignment = VerticalAlignment.Center };

    private static Control Row(Control first, Control second)
    {
        var row = new DockPanel { Margin = new Thickness(0, 2) };
        if (first is TextBlock { Text: { } label })
        {
            second.Named(label);
        }
        first.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(first, Dock.Left);
        row.Children.Add(first);
        row.Children.Add(second);
        return row;
    }

    private static Control Section(string title, params Control[] children)
    {
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 2) });
        foreach (var child in children)
        {
            stack.Children.Add(child);
        }
        return stack;
    }

    private List<object> RecentMenu(IReadOnlyList<string> recentFiles)
    {
        var items = new List<object>();
        foreach (var file in recentFiles)
        {
            var item = new MenuItem { Header = LiteralText.Escaped(file) };
            item.Click += (_, _) => _fileName.Text = file;
            items.Add(item);
        }
        if (items.Count > 0)
        {
            items.Add(new Separator());
        }
        foreach (var placeholder in Placeholders)
        {
            var item = new MenuItem { Header = placeholder };
            item.Click += (_, _) => _fileName.Text = (_fileName.Text ?? string.Empty) + placeholder;
            items.Add(item);
        }
        return items;
    }

    /// <summary>Delimited text: separadores editáveis; NULL também em Excel CSV e Markdown; codificação só para ficheiro.</summary>
    private void UpdateEnabled()
    {
        var delimited = Format == GridExportFormat.DelimitedText;
        _separator.IsEnabled = delimited;
        _encloser.IsEnabled = delimited;
        _terminator.IsEnabled = delimited;
        _null.IsEnabled = Format is GridExportFormat.DelimitedText or GridExportFormat.ExcelCsv or GridExportFormat.Markdown;
        _includeQuery.IsEnabled = Format is GridExportFormat.Html or GridExportFormat.Xml or GridExportFormat.Markdown or GridExportFormat.Json;
        _encoding.IsEnabled = _toFile.IsChecked == true;
        _openFile.IsEnabled = _toFile.IsChecked == true;
    }

    private async Task BrowseAsync()
    {
        var file = await PathPicker.SaveFileAsync(this, new FilePickerSaveOptions
        {
            Title = "Export grid rows",
            DefaultExtension = GridExporter.Extension(Format),
            SuggestedFileName = string.IsNullOrWhiteSpace(_fileName.Text) ? "export." + GridExporter.Extension(Format) : Path.GetFileName(_fileName.Text),
            ShowOverwritePrompt = false,
        });
        if (file is { } path)
        {
            _fileName.Text = path;
        }
    }

    private GridExportOptions OptionsFromControls() => new()
    {
        IncludeColumnNames = _columnNames.IsChecked == true,
        IncludeAutoIncrement = _autoIncrement.IsChecked == true,
        RemoveLinebreaks = _removeLinebreaks.IsChecked == true,
        Separator = _separator.Text ?? string.Empty,
        Encloser = _encloser.Text ?? string.Empty,
        Terminator = _terminator.Text ?? string.Empty,
        NullValue = _null.Text ?? string.Empty,
        ToClipboard = _toClipboard.IsChecked == true,
    };

    /// <summary>"Save clipboard settings": as opções atuais passam a ser as do Copy as.</summary>
    private void SaveClipboardSettings()
    {
        _settings.ClipboardColumnNames = _columnNames.IsChecked == true;
        _settings.ClipboardAutoIncrement = _autoIncrement.IsChecked == true;
        _settings.ClipboardRemoveLinebreaks = _removeLinebreaks.IsChecked == true;
        _settings.ClipboardSeparator = _separator.Text ?? string.Empty;
        _settings.ClipboardEncloser = _encloser.Text ?? string.Empty;
        _settings.ClipboardTerminator = _terminator.Text ?? string.Empty;
        _settings.ClipboardNullValue = _null.Text ?? string.Empty;
    }

    private async Task AcceptAsync()
    {
        var toClipboard = _toClipboard.IsChecked == true;
        var fileName = (_fileName.Text ?? string.Empty).Trim();
        if (!toClipboard)
        {
            if (fileName.Length == 0)
            {
                await MessageDialog.ShowAsync(this, "Export grid rows", "Please select a file name.", MessageDialogButtons.Ok);
                return;
            }
            var expanded = _expand(fileName);
            if (File.Exists(expanded)
                && await MessageDialog.ShowAsync(this, "Confirm", $"Overwrite file {expanded}?", MessageDialogButtons.YesNo) != MessageDialogResult.Yes)
            {
                return;
            }
            _settings.RecentFiles = ExportFile.AddRecent(_settings.RecentFiles, fileName);
        }
        // As escolhas ficam gravadas só no OK.
        _settings.OutputToClipboard = toClipboard;
        _settings.FileName = fileName;
        _settings.Encoding = _encoding.SelectedItem as string ?? ExportFile.DefaultEncoding;
        _settings.Format = (int)Format;
        _settings.SelectedRowsOnly = _selection.IsChecked == true;
        _settings.ColumnNames = _columnNames.IsChecked == true;
        _settings.AutoIncrement = _autoIncrement.IsChecked == true;
        _settings.FocusedColumnOnly = _focusedOnly.IsChecked == true;
        _settings.IncludeQuery = _includeQuery.IsChecked == true;
        _settings.RemoveLinebreaks = _removeLinebreaks.IsChecked == true;
        _settings.OpenFile = _openFile.IsChecked == true;
        _settings.Separator = _separator.Text ?? string.Empty;
        _settings.Encloser = _encloser.Text ?? string.Empty;
        _settings.Terminator = _terminator.Text ?? string.Empty;
        _settings.NullValue = _null.Text ?? string.Empty;
        Close(new GridExportRequest(
            Format,
            toClipboard,
            toClipboard ? string.Empty : _expand(fileName),
            _settings.Encoding,
            _selection.IsChecked == true,
            OptionsFromControls(),
            _focusedOnly.IsChecked == true,
            _includeQuery.IsEnabled && _includeQuery.IsChecked == true,
            _openFile.IsChecked == true && !toClipboard));
    }

    public static Task<GridExportRequest?> ShowAsync(Window owner, ExportOptions settings, long selectedRows, long allRows, string? focusedColumn, Func<string, string> expandFileName) =>
        new GridExportDialog(settings, selectedRows, allRows, focusedColumn, expandFileName).ShowDialog<GridExportRequest?>(owner);
}
