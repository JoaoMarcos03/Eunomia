using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Eunomia.App.Query;
using Eunomia.App.Settings;
using Eunomia.App.Transfer;
using Eunomia.Engine.Transfer;
using Eunomia.UI.Controls.Common.Accessibility;
using Eunomia.UI.Controls.Common.Brand;
using Eunomia.UI.Controls.Common.Dialogs;
using Eunomia.UI.Controls.Common.Theming;

namespace Eunomia.UI.Features.Transfer;

/// <summary>Corre o import pedido pelo diálogo (o shell junta a sessão).</summary>
public delegate Task<CsvImportResult> CsvImportRunner(CsvImportRequest request, IProgress<CsvImportProgress> progress, CancellationToken cancellationToken);

/// <summary>
/// Tools › Import CSV file (FAM-E07; anexo B §3.1): ficheiro e codificação, Ignore first N lines, Low priority, números
/// locais, Truncate, Keep dialog open; separadores com sequências de barra invertida (<c>;</c>, <c>"</c> opcional,
/// <c>"</c>, <c>\r\n</c>); duplicados INSERT, INSERT IGNORE ou REPLACE (omissão); servidor (<c>LOAD DATA</c>) ou cliente;
/// base, tabela e colunas marcadas com Up/Down/All (mapeamento posicional). Durante o import, Import passa a Cancel e
/// a barra mostra os bytes lidos.
/// </summary>
public sealed class CsvImportDialog : Window
{
    private readonly CsvImportSettings _settings;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<string>>> _loadTables;
    private readonly Func<string, string, CancellationToken, Task<IReadOnlyList<CsvTargetColumn>>> _loadColumns;
    private readonly CsvImportRunner _run;
    private readonly TextBox _file = new TextBox { PlaceholderText = "Doubleclick to select file", MinHeight = 22, Padding = new Thickness(4, 1) }.Named("Filename");
    private readonly ComboBox _encoding = new() { ItemsSource = SqlFile.LoadEncodings, MinWidth = 180 };
    private readonly NumericUpDown _ignoreLines = new() { Minimum = 0, Maximum = 1_000_000, Increment = 1, FormatString = "0", Width = 110 };
    private readonly CheckBox _lowPriority = Option("Low priority, avoid high server load");
    private readonly CheckBox _localNumbers = Option("Input file contains local formatted numbers, e.g. 1.234,56 in Germany");
    private readonly CheckBox _truncate = Option("Truncate destination table before import");
    private readonly CheckBox _keepOpen = Option("Keep dialog open after import");
    private readonly TextBox _fieldTerminator = Small();
    private readonly TextBox _encloser = Small();
    private readonly CheckBox _optionally = Option("optionally");
    private readonly TextBox _escaper = Small();
    private readonly TextBox _lineTerminator = Small();
    private readonly RadioButton _insert = Radio("INSERT (may throw errors)", "dup");
    private readonly RadioButton _insertIgnore = Radio("INSERT IGNORE (duplicates)", "dup");
    private readonly RadioButton _replace = Radio("REPLACE (duplicates)", "dup");
    private readonly RadioButton _server = Radio("Server parses file contents (LOAD DATA)", "method");
    private readonly RadioButton _client = Radio("Client parses file contents", "method");
    private readonly ComboBox _database = new() { MinWidth = 220 };
    private readonly ComboBox _table = new() { MinWidth = 220 };
    private readonly ListBox _columns = new() { Height = 180 };
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 1000, Height = 14 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _import = new() { Content = "Import!", IsDefault = true, MinWidth = 85, HorizontalContentAlignment = HorizontalAlignment.Center };
    private readonly Button _close = new() { Content = "Close", IsCancel = true, MinWidth = 85, HorizontalContentAlignment = HorizontalAlignment.Center };
    private List<(CsvTargetColumn Column, CheckBox Check)> _columnItems = [];
    private CancellationTokenSource? _running;
    private int _tablesVersion;

    public CsvImportDialog(
        CsvImportSettings settings,
        IReadOnlyList<string> databases,
        Func<string, CancellationToken, Task<IReadOnlyList<string>>> loadTables,
        Func<string, string, CancellationToken, Task<IReadOnlyList<CsvTargetColumn>>> loadColumns,
        CsvImportRunner run,
        string? initialDatabase = null,
        string? initialTable = null)
    {
        Icon = BrandAssets.WindowIcon;
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(databases);
        ArgumentNullException.ThrowIfNull(loadTables);
        ArgumentNullException.ThrowIfNull(loadColumns);
        ArgumentNullException.ThrowIfNull(run);
        _settings = settings;
        _loadTables = loadTables;
        _loadColumns = loadColumns;
        _run = run;
        Title = "Import CSV file";
        Width = 760;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = UiFonts.Ui;
        FontSize = 12;

        // Valores gravados (o Truncate começa sempre desligado).
        _file.Text = settings.FileName;
        _encoding.SelectedItem = SqlFile.LoadEncodings.Contains(settings.Encoding) ? settings.Encoding : SqlFile.AutoDetect;
        _ignoreLines.Value = Math.Max(0, settings.IgnoreLines);
        _lowPriority.IsChecked = settings.LowPriority;
        _localNumbers.IsChecked = settings.LocalNumbers;
        _keepOpen.IsChecked = settings.KeepDialogOpen;
        _fieldTerminator.Text = settings.FieldTerminator;
        _encloser.Text = settings.FieldEncloser;
        _optionally.IsChecked = settings.OptionallyEnclosed;
        _escaper.Text = settings.FieldEscaper;
        _lineTerminator.Text = settings.LineTerminator;
        (settings.Duplicates switch { 0 => _insert, 1 => _insertIgnore, _ => _replace }).IsChecked = true;
        (settings.Method == 1 ? _client : _server).IsChecked = true;
        _server.IsCheckedChanged += (_, _) => UpdateEnabled();

        _database.ItemsSource = databases;
        _database.SelectionChanged += async (_, _) => await LoadTablesAsync(null);
        _table.SelectionChanged += async (_, _) => await LoadColumnsAsync();
        _file.DoubleTapped += async (_, _) => await BrowseAsync();
        _import.Click += async (_, _) => await ImportOrCancelAsync();
        _close.Click += (_, _) =>
        {
            if (_running is null)
            {
                Close();
            }
        };
        Closing += (_, _) => _running?.Cancel();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _running is null)
            {
                Close();
            }
        };

        // Linhas compactas na lista das colunas (o ListBoxItem do Fluent é alto).
        _columns.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(MinHeightProperty, 0.0),
                new Setter(Avalonia.Controls.Primitives.TemplatedControl.PaddingProperty, new Thickness(6, 0)),
            },
        });
        Avalonia.Automation.AutomationProperties.SetName(_columns, "Destination columns");
        Avalonia.Automation.AutomationProperties.SetName(_ignoreLines, "Ignore first lines");
        Avalonia.Automation.AutomationProperties.SetName(_fieldTerminator, "Fields terminated by");
        Avalonia.Automation.AutomationProperties.SetName(_encloser, "Fields enclosed by");
        Avalonia.Automation.AutomationProperties.SetName(_escaper, "Fields escaped by");
        Avalonia.Automation.AutomationProperties.SetName(_lineTerminator, "Lines terminated by");
        Avalonia.Automation.AutomationProperties.SetName(_bar, "Import progress");
        Content = Layout();
        UpdateEnabled();
        if (initialDatabase is not null && databases.Contains(initialDatabase))
        {
            InitialTable = initialTable;
            _database.SelectedItem = initialDatabase;
        }
    }

    // ---------- Para testes ----------

    internal TextBox FileBox => _file;

    internal ComboBox TableCombo => _table;

    internal Button ImportButton => _import;

    internal TextBlock StatusText => _status;

    internal RadioButton ClientRadio => _client;

    internal CheckBox OptionallyBox => _optionally;

    internal IReadOnlyList<(CsvTargetColumn Column, CheckBox Check)> ColumnItems => _columnItems;

    /// <summary>Carregamento de tabelas ou colunas em curso.</summary>
    internal Task Loading { get; private set; } = Task.CompletedTask;

    private string? InitialTable { get; set; }

    internal void MoveColumn(int index, int delta) => Move(index, delta);

    // ---------- Destino ----------

    private Task LoadTablesAsync(string? select)
    {
        var task = LoadTablesCoreAsync(select ?? InitialTable);
        InitialTable = null;
        Loading = task;
        return task;
    }

    private async Task LoadTablesCoreAsync(string? select)
    {
        var version = ++_tablesVersion;
        _table.ItemsSource = null;
        SetColumns([]);
        if (_database.SelectedItem is not string database)
        {
            return;
        }
        IReadOnlyList<string> tables;
        try
        {
            tables = await _loadTables(database, CancellationToken.None);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _status.Text = "Error: " + e.Message;
            return;
        }
        if (version != _tablesVersion)
        {
            return;
        }
        _table.ItemsSource = tables;
        _table.SelectedItem = select is not null && tables.Contains(select) ? select : tables.FirstOrDefault();
    }

    private Task LoadColumnsAsync()
    {
        var task = LoadColumnsCoreAsync();
        Loading = Task.WhenAll(Loading, task);
        return task;
    }

    private async Task LoadColumnsCoreAsync()
    {
        if (_database.SelectedItem is not string database || _table.SelectedItem is not string table)
        {
            SetColumns([]);
            return;
        }
        try
        {
            SetColumns(await _loadColumns(database, table, CancellationToken.None));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _status.Text = "Error: " + e.Message;
        }
    }

    /// <summary>Todas as colunas marcadas, pela ordem da tabela.</summary>
    private void SetColumns(IReadOnlyList<CsvTargetColumn> columns)
    {
        _columnItems = [.. columns.Select(c => (c, new CheckBox { Content = Text(c.Name), IsChecked = true, MinHeight = 22 }))];
        RefreshColumns();
    }

    private void RefreshColumns()
    {
        var selected = _columns.SelectedIndex;
        _columns.ItemsSource = _columnItems.Select(i => i.Check).ToList();
        _columns.SelectedIndex = Math.Min(selected, _columnItems.Count - 1);
    }

    private void Move(int index, int delta)
    {
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _columnItems.Count)
        {
            return;
        }
        (_columnItems[index], _columnItems[target]) = (_columnItems[target], _columnItems[index]);
        RefreshColumns();
        _columns.SelectedIndex = target;
    }

    private void CheckAll()
    {
        var check = _columnItems.Any(i => i.Check.IsChecked != true);
        foreach (var item in _columnItems)
        {
            item.Check.IsChecked = check;
        }
    }

    /// <summary>"optionally" só conta para o LOAD DATA, como no HeidiSQL.</summary>
    private void UpdateEnabled() => _optionally.IsEnabled = _server.IsChecked == true && _running is null;

    private async Task BrowseAsync()
    {
        var files = await PathPicker.OpenFilesAsync(this, new FilePickerOpenOptions
        {
            Title = "Import CSV file",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("CSV files") { Patterns = ["*.csv", "*.txt", "*.tsv"] }, FilePickerFileTypes.All],
        });
        if (files.Count > 0)
        {
            _file.Text = files[0];
        }
    }

    // ---------- Import ----------

    private async Task ImportOrCancelAsync()
    {
        if (_running is { } running)
        {
            await running.CancelAsync();
            _status.Text = "Cancelling...";
            return;
        }
        var path = (_file.Text ?? string.Empty).Trim();
        if (!File.Exists(path))
        {
            await MessageDialog.ShowAsync(this, Title ?? string.Empty, path.Length == 0 ? "Please select a file." : "File not found: " + path, MessageDialogButtons.Ok);
            return;
        }
        var columns = _columnItems.Where(i => i.Check.IsChecked == true).Select(i => i.Column).ToList();
        if (_database.SelectedItem is not string database || _table.SelectedItem is not string table || columns.Count == 0)
        {
            await MessageDialog.ShowAsync(this, Title ?? string.Empty, "Please select a table and at least one column.", MessageDialogButtons.Ok);
            return;
        }
        if (string.IsNullOrEmpty(_fieldTerminator.Text) || string.IsNullOrEmpty(_lineTerminator.Text))
        {
            await MessageDialog.ShowAsync(this, Title ?? string.Empty, "Field and line terminators cannot be empty.", MessageDialogButtons.Ok);
            return;
        }
        SaveSettings(path);
        var request = new CsvImportRequest
        {
            Path = path,
            Encoding = _encoding.SelectedItem as string ?? SqlFile.AutoDetect,
            Database = database,
            Table = table,
            Columns = columns,
            Csv = new CsvOptions
            {
                FieldTerminator = CsvOptions.Unescape(_fieldTerminator.Text ?? string.Empty),
                Encloser = CsvOptions.Unescape(_encloser.Text ?? string.Empty),
                OptionallyEnclosed = _optionally.IsChecked == true,
                Escaper = CsvOptions.Unescape(_escaper.Text ?? string.Empty),
                LineTerminator = CsvOptions.Unescape(_lineTerminator.Text ?? string.Empty),
                IgnoreLines = (int)(_ignoreLines.Value ?? 0),
            },
            Duplicates = _insert.IsChecked == true ? CsvDuplicates.Insert : _insertIgnore.IsChecked == true ? CsvDuplicates.InsertIgnore : CsvDuplicates.Replace,
            LowPriority = _lowPriority.IsChecked == true,
            LocalNumbers = _localNumbers.IsChecked == true,
            Truncate = _truncate.IsChecked == true,
            Method = _client.IsChecked == true ? CsvImportMethod.Client : CsvImportMethod.Server,
        };

        _running = new CancellationTokenSource();
        SetRunning(true);
        _status.Text = "Importing...";
        _bar.Value = 0;
        var close = false;
        try
        {
            var result = await _run(request, new Progress<CsvImportProgress>(ShowProgress), _running.Token);
            ShowProgress(new CsvImportProgress(result.BytesRead, result.TotalBytes, result.Rows, result.Elapsed));
            var c = CultureInfo.CurrentCulture;
            _status.Text = result.Cancelled
                ? string.Create(c, $"Cancelled after {result.Rows:N0} rows.")
                : result.Rows == 0
                    ? "No rows were imported. Possible causes: the file is empty, the encoding is wrong, or the field or line terminators do not match the file."
                    : string.Create(c, $"{result.Rows:N0} rows imported in {result.Elapsed.TotalSeconds:0.0} s") + (result.Warnings > 0 ? string.Create(c, $", {result.Warnings:N0} warnings (see the SQL log).") : ".");
            close = !result.Cancelled && result.Rows > 0 && _keepOpen.IsChecked != true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _status.Text = "Error: " + e.Message;
            await MessageDialog.ShowAsync(this, "Error", e.Message, MessageDialogButtons.Ok);
        }
        finally
        {
            _running.Dispose();
            _running = null;
            SetRunning(false);
        }
        if (close)
        {
            Close();
        }
    }

    private void ShowProgress(CsvImportProgress progress)
    {
        _bar.Value = progress.TotalBytes > 0 ? Math.Clamp(progress.BytesRead * 1000.0 / progress.TotalBytes, 0, 1000) : 0;
        if (_running is not null)
        {
            _status.Text = string.Create(CultureInfo.CurrentCulture, $"Importing... {progress.Rows:N0} rows, {progress.BytesRead / (1024.0 * 1024):0.0} / {progress.TotalBytes / (1024.0 * 1024):0.0} MiB");
        }
    }

    private void SaveSettings(string path)
    {
        _settings.FileName = path;
        _settings.Encoding = _encoding.SelectedItem as string ?? SqlFile.AutoDetect;
        _settings.IgnoreLines = (int)(_ignoreLines.Value ?? 0);
        _settings.LowPriority = _lowPriority.IsChecked == true;
        _settings.LocalNumbers = _localNumbers.IsChecked == true;
        _settings.KeepDialogOpen = _keepOpen.IsChecked == true;
        _settings.FieldTerminator = _fieldTerminator.Text ?? string.Empty;
        _settings.FieldEncloser = _encloser.Text ?? string.Empty;
        _settings.OptionallyEnclosed = _optionally.IsChecked == true;
        _settings.FieldEscaper = _escaper.Text ?? string.Empty;
        _settings.LineTerminator = _lineTerminator.Text ?? string.Empty;
        _settings.Duplicates = _insert.IsChecked == true ? 0 : _insertIgnore.IsChecked == true ? 1 : 2;
        _settings.Method = _client.IsChecked == true ? 1 : 0;
    }

    private void SetRunning(bool running)
    {
        _import.Content = running ? "Cancel" : "Import!";
        _close.IsEnabled = !running;
        foreach (var control in new Control[] { _file, _encoding, _ignoreLines, _lowPriority, _localNumbers, _truncate, _keepOpen, _fieldTerminator, _encloser, _escaper,
            _lineTerminator, _insert, _insertIgnore, _replace, _server, _client, _database, _table, _columns })
        {
            control.IsEnabled = !running;
        }
        UpdateEnabled();
    }

    // ---------- Layout ----------

    private static TextBlock Text(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center };

    private static CheckBox Option(string text) => new() { Content = Text(text), MinHeight = 24 };

    private static RadioButton Radio(string text, string group) => new() { Content = Text(text), GroupName = group, MinHeight = 24 };

    private static TextBox Small() => new() { Width = 70, MinHeight = 22, Padding = new Thickness(4, 1) };

    private static Control Section(string title, params Control[] children)
    {
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 4, 0, 2) });
        foreach (var child in children)
        {
            stack.Children.Add(child);
        }
        return stack;
    }

    private static Control Row(string label, Control control, double labelWidth = 130)
    {
        var row = new DockPanel { Margin = new Thickness(0, 2) };
        var text = new TextBlock { Text = label, Width = labelWidth, VerticalAlignment = VerticalAlignment.Center };
        control.Named(label);
        DockPanel.SetDock(text, Dock.Left);
        row.Children.Add(text);
        control.HorizontalAlignment = control is TextBox { Width: > 0 } or NumericUpDown ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        row.Children.Add(control);
        return row;
    }

    private Control Layout()
    {
        var browse = new Button { Content = "...", Padding = new Thickness(6, 1) };
        browse.Click += async (_, _) => await BrowseAsync();
        var fileRow = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        fileRow.Children.Add(browse);
        fileRow.Children.Add(_file);

        var encloserRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        encloserRow.Children.Add(_encloser);
        encloserRow.Children.Add(_optionally);

        var left = new StackPanel { Spacing = 4 };
        left.Children.Add(Section("Input file", Row("Filename:", fileRow, 70), Row("Encoding:", _encoding, 70)));
        left.Children.Add(Section("Options", Row("Ignore first lines:", _ignoreLines), _lowPriority, _localNumbers, _truncate, _keepOpen));
        left.Children.Add(Section("Control characters",
            Row("Fields terminated by:", _fieldTerminator),
            Row("Fields enclosed by:", encloserRow),
            Row("Fields escaped by:", _escaper),
            Row("Lines terminated by:", _lineTerminator)));

        var up = new Button { Content = "Up" };
        up.Click += (_, _) => Move(_columns.SelectedIndex, -1);
        var down = new Button { Content = "Down" };
        down.Click += (_, _) => Move(_columns.SelectedIndex, 1);
        var all = new Button { Content = "All" };
        all.Click += (_, _) => CheckAll();
        var columnButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
        columnButtons.Children.Add(up);
        columnButtons.Children.Add(down);
        columnButtons.Children.Add(all);

        var right = new StackPanel { Spacing = 4 };
        right.Children.Add(Section("Handling of duplicate rows", _insert, _insertIgnore, _replace));
        right.Children.Add(Section("Method", _server, _client));
        right.Children.Add(Section("Destination", Row("Database:", _database, 70), Row("Table:", _table, 70),
            new TextBlock { Text = "Columns (file field k goes to the k-th checked column):", Margin = new Thickness(0, 4, 0, 2) },
            Themed.Bind(new Border { BorderThickness = new Thickness(1), Child = _columns }, Border.BorderBrushProperty, "border"), columnButtons));

        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,16,*") };
        columns.Children.Add(left);
        Grid.SetColumn(right, 2);
        columns.Children.Add(right);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_import);
        buttons.Children.Add(_close);

        var panel = new StackPanel { Margin = new Thickness(12), Spacing = 8 };
        panel.Children.Add(columns);
        panel.Children.Add(_bar);
        panel.Children.Add(_status);
        panel.Children.Add(buttons);
        return panel;
    }

    public static Task ShowAsync(
        Window owner,
        CsvImportSettings settings,
        IReadOnlyList<string> databases,
        Func<string, CancellationToken, Task<IReadOnlyList<string>>> loadTables,
        Func<string, string, CancellationToken, Task<IReadOnlyList<CsvTargetColumn>>> loadColumns,
        CsvImportRunner run,
        string? initialDatabase,
        string? initialTable) =>
        new CsvImportDialog(settings, databases, loadTables, loadColumns, run, initialDatabase, initialTable).ShowDialog(owner);
}
