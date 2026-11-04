using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Eunomia.App.Settings;
using Eunomia.App.Transfer;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Transfer;
using Eunomia.UI.Controls.Common.Accessibility;
using Eunomia.UI.Controls.Common.Brand;
using Eunomia.UI.Controls.Common.Dialogs;
using Eunomia.UI.Controls.Common.Input;
using Eunomia.UI.Controls.Common.Text;
using Eunomia.UI.Controls.Common.Theming;

namespace Eunomia.UI.Features.Transfer;

/// <summary>Runs the export requested by the dialog (the shell adds the session, the host and the version).</summary>
public delegate Task<SqlExportResult> SqlExportRunner(SqlExportRequest request, IProgress<SqlExportProgress> progress, CancellationToken cancellationToken);

/// <summary>Runs an export to a database or a server: the export progress, then the run on the target.</summary>
public delegate Task<SqlExportToServerResult> SqlExportToServerRunner(
    SqlExportRequest request, IProgress<SqlExportProgress> exportProgress, IProgress<SqlFileProgress> importProgress, CancellationToken cancellationToken);

/// <summary>A saved session offered as "Server: &lt;name&gt;".</summary>
public sealed record SqlExportServer(Guid Id, string Name);

/// <summary>
/// The outputs "Database" and "Server: &lt;session&gt;" (appendix B §2.3.1): the other saved sessions, the databases of a
/// session (<c>null</c> = the source one; another one connects first), the checks of the shell (same server, read-only)
/// and the runner.
/// </summary>
public sealed record SqlExportTargets(
    IReadOnlyList<SqlExportServer> Servers,
    Func<Guid?, CancellationToken, Task<IReadOnlyList<string>>> LoadDatabases,
    Func<SqlExportRequest, string?> Validate,
    SqlExportToServerRunner Run);

/// <summary>
/// Tools › Export database as SQL (FAM-E06; appendix B §2.3.1): a tree of databases and objects with check boxes and filters
/// (regular expressions), Drop/Create of databases and tables (Drop implies Create and shows in bold as "Drop!!"),
/// data (No data, Delete + insert, Insert, Insert ignore, Replace), Max INSERT size, output (file, ZIP, directory,
/// clipboard) with recent entries, HeidiSQL's options and the consistent snapshot. The bottom grid shows each object
/// (Database, Table, Rows "N / P%", Duration) while the job runs; Export becomes Cancel.
/// </summary>
public sealed class SqlExportDialog : Window
{
    private const int MaxRecent = 20;

    private static readonly string[] DataModes =
    [
        "No data", "Delete + insert (truncate existing data)", "Insert", "Insert ignore (do not update existing)", "Replace existing data",
    ];

    private static readonly string[] FileOutputs =
    [
        "Single .sql file", "ZIP compressed .sql file", "Directory - one file per object in database subdirectories", "Clipboard",
    ];

    /// <summary>First item of the target databases of a server: the source database names.</summary>
    public const string SameAsSource = "[Same as on source server]";

    private readonly SqlExportSettings _settings;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<DbObjectInfo>>> _loadObjects;
    private readonly SqlExportRunner _run;
    private readonly List<DatabaseNode> _databases = [];
    private readonly TreeView _tree = new();
    private readonly TextBox _databaseFilter = Box("Database filter (regular expression)").WithClearButton();
    private readonly TextBox _tableFilter = Box("Table filter (regular expression)").WithClearButton();
    private readonly CheckBox _dropDatabases = new() { Content = "Drop" };
    private readonly CheckBox _createDatabases = new() { Content = "Create" };
    private readonly CheckBox _dropTables = new() { Content = "Drop" };
    private readonly CheckBox _createTables = new() { Content = "Create" };
    private readonly ComboBox _data = new() { ItemsSource = DataModes, MinWidth = 260 };
    private readonly NumericUpDown _maxInsert = new() { Minimum = 0, Maximum = 1024 * 1024, Increment = 64, FormatString = "0", Width = 130 };
    private readonly ComboBox _output = new() { MinWidth = 260 };
    private readonly SqlExportTargets? _targets;
    private readonly List<string> _outputs;
    private readonly ComboBox _targetDatabase = new() { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _pathLabel = new() { Text = "Filename:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3) };
    private DockPanel? _pathRow;
    private CancellationTokenSource? _targetLoad;
    private readonly TextBox _path = Box("File name");
    private readonly Button _recent = new() { Content = "▾", Padding = new Thickness(4, 1) };
    private readonly CheckBox _addComments = Option("Add comments");
    private readonly CheckBox _wrapTransactions = Option("Wrap data DML in transactions");
    private readonly CheckBox _removeAutoIncrement = Option("Remove AUTO_INCREMENT clauses");
    private readonly CheckBox _removeDefiner = Option("Remove DEFINER clauses");
    private readonly CheckBox _snapshot = Option("Consistent snapshot (one connection, no parallel tables)");
    private readonly Grid _results = new() { ColumnDefinitions = new ColumnDefinitions("*,*,150,100") };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly Button _export = new() { Content = "Export", IsDefault = true, MinWidth = 85, HorizontalContentAlignment = HorizontalAlignment.Center };
    private readonly Button _close = new() { Content = "Close", IsCancel = true, MinWidth = 85, HorizontalContentAlignment = HorizontalAlignment.Center };
    private readonly Dictionary<(string Database, string Name, DbObjectKind Kind), TextBlock[]> _resultRows = [];
    private CancellationTokenSource? _running;
    private bool _updatingChecks;

    public SqlExportDialog(
        SqlExportSettings settings,
        IReadOnlyList<string> databases,
        Func<string, CancellationToken, Task<IReadOnlyList<DbObjectInfo>>> loadObjects,
        SqlExportRunner run,
        string? initialDatabase = null,
        IReadOnlyCollection<string>? initialObjects = null,
        SqlExportTargets? targets = null)
    {
        Icon = BrandAssets.WindowIcon;
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(databases);
        ArgumentNullException.ThrowIfNull(loadObjects);
        ArgumentNullException.ThrowIfNull(run);
        _settings = settings;
        _loadObjects = loadObjects;
        _run = run;
        _targets = targets;
        _outputs = [.. FileOutputs];
        if (targets is not null)
        {
            _outputs.Add("Database");
            _outputs.AddRange(targets.Servers.Select(s => "Server: " + s.Name));
        }
        _output.ItemsSource = _outputs;
        Title = "Export database as SQL";
        Width = 900;
        Height = 640;
        MinWidth = 700;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = UiFonts.Ui;
        FontSize = 12;

        foreach (var name in databases)
        {
            var node = new DatabaseNode(name);
            BuildDatabaseItem(node);
            _databases.Add(node);
        }
        _tree.ItemsSource = _databases.Select(d => d.Item).ToList();

        // Saved values (the Drop options always start off).
        _createDatabases.IsChecked = settings.CreateDatabases;
        _createTables.IsChecked = settings.CreateTables;
        _data.SelectedIndex = Math.Clamp(settings.Data, 0, DataModes.Length - 1);
        _maxInsert.Value = Math.Max(0, settings.MaxInsertSizeKb);
        _output.SelectedIndex = Math.Clamp(settings.Output, 0, (targets is null ? FileOutputs.Length : FileOutputs.Length + 1) - 1);
        _addComments.IsChecked = settings.AddComments;
        _wrapTransactions.IsChecked = settings.WrapInTransactions;
        _removeAutoIncrement.IsChecked = settings.RemoveAutoIncrement;
        _removeDefiner.IsChecked = settings.RemoveDefiner;
        _snapshot.IsChecked = settings.ConsistentSnapshot;
        _databaseFilter.Text = settings.DatabaseFilter;
        _tableFilter.Text = settings.TableFilter;
        _path.Text = Recent().FirstOrDefault() ?? string.Empty;

        // Drop implies Create; turning Create off turns Drop off (tabletools.pas L1664-L1686).
        LinkDropCreate(_dropDatabases, _createDatabases);
        LinkDropCreate(_dropTables, _createTables);
        _data.SelectionChanged += (_, _) => UpdateEnabled();
        _output.SelectionChanged += (_, _) =>
        {
            _path.Text = Recent().FirstOrDefault() ?? string.Empty;
            UpdateEnabled();
            _ = LoadTargetDatabasesAsync();
        };
        _databaseFilter.TextChanged += (_, _) => ApplyFilters();
        _tableFilter.TextChanged += (_, _) => ApplyFilters();
        _recent.Flyout = new MenuFlyout();
        _recent.Click += (_, _) => _recent.Flyout = new MenuFlyout { ItemsSource = RecentMenu() };
        var browse = new Button { Content = "...", Padding = new Thickness(6, 1) };
        browse.Click += async (_, _) => await BrowseAsync();
        _path.DoubleTapped += async (_, _) => await BrowseAsync();
        _export.Click += async (_, _) => await ExportOrCancelAsync();
        _close.Click += (_, _) =>
        {
            if (_running is null)
            {
                Close();
            }
        };
        Closing += (_, e) =>
        {
            // Closing the window halfway cancels the job.
            _running?.Cancel();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _running is null)
            {
                Close();
            }
        };

        Avalonia.Automation.AutomationProperties.SetName(_tree, "Databases and objects to export");
        Avalonia.Automation.AutomationProperties.SetName(_results, "Export progress");
        Avalonia.Automation.AutomationProperties.SetName(_maxInsert, "Max INSERT size in KB");
        Avalonia.Automation.AutomationProperties.SetName(_path, "Output file name or directory");
        Avalonia.Automation.AutomationProperties.SetName(_targetDatabase, "Target database");
        Content = Layout(browse);
        ApplyFilters();
        UpdateEnabled();
        _ = LoadTargetDatabasesAsync();
        if (initialDatabase is not null && _databases.FirstOrDefault(d => d.Name == initialDatabase) is { } initial)
        {
            initial.Item.IsExpanded = true;
            _tree.SelectedItem = initial.Item;
            _ = SelectInitialAsync(initial, initialObjects);
        }
    }

    // ---------- For tests ----------

    internal TreeView Tree => _tree;

    internal ComboBox OutputCombo => _output;

    internal ComboBox TargetDatabaseCombo => _targetDatabase;

    /// <summary>Answers the confirmation of an export to a database (tests).</summary>
    internal Func<string, Task<bool>>? ConfirmOverride { get; set; }

    /// <summary>Loading of the target databases in progress (tests wait for it).</summary>
    internal Task TargetLoading { get; private set; } = Task.CompletedTask;

    internal ComboBox DataCombo => _data;

    internal TextBox PathBox => _path;

    internal CheckBox CreateTablesBox => _createTables;

    internal CheckBox DropTablesBox => _dropTables;

    internal Button ExportButton => _export;

    internal TextBlock StatusText => _status;

    internal IReadOnlyList<TextBlock[]> ResultRows => [.. _resultRows.Values];

    /// <summary>Object loads in progress (tests wait for them).</summary>
    internal Task Loading => Task.WhenAll(_databases.Select(d => d.Loading ?? Task.CompletedTask));

    internal void SetDatabaseChecked(string database, bool isChecked)
    {
        var node = _databases.First(d => d.Name == database);
        node.Check.IsChecked = isChecked;
    }

    internal void SetObjectChecked(string database, string name, bool isChecked)
    {
        var node = _databases.First(d => d.Name == database);
        node.Objects!.First(o => o.Info.Ref.Name == name).Check.IsChecked = isChecked;
    }

    // ---------- Tree ----------

    private sealed class DatabaseNode(string name)
    {
        public string Name { get; } = name;

        public TreeViewItem Item { get; set; } = null!;

        public CheckBox Check { get; set; } = null!;

        public List<ObjectNode>? Objects { get; set; }

        public Task? Loading { get; set; }
    }

    private sealed class ObjectNode(DbObjectInfo info)
    {
        public DbObjectInfo Info { get; } = info;

        public TreeViewItem Item { get; set; } = null!;

        public CheckBox Check { get; set; } = null!;
    }

    private void BuildDatabaseItem(DatabaseNode node)
    {
        node.Check = new CheckBox { IsThreeState = true, IsChecked = false, Content = Text(node.Name), Padding = new Thickness(4, 0), MinHeight = 24 };
        node.Item = new TreeViewItem { Header = node.Check, ItemsSource = new[] { new TreeViewItem { Header = "Loading..." } } };
        node.Item.PropertyChanged += (_, e) =>
        {
            if (e.Property == TreeViewItem.IsExpandedProperty && node.Item.IsExpanded)
            {
                _ = EnsureLoadedAsync(node);
            }
        };
        node.Check.IsCheckedChanged += (_, _) =>
        {
            if (_updatingChecks)
            {
                return;
            }
            // Should clicking the "some" state go to "none"? As in HeidiSQL, one click checks all or nothing.
            if (node.Check.IsChecked is null)
            {
                node.Check.IsChecked = false;
                return;
            }
            var check = node.Check.IsChecked == true;
            if (node.Objects is { } objects)
            {
                SetChildren(objects, check);
            }
            else if (check)
            {
                _ = EnsureLoadedAsync(node);
            }
        };
    }

    private Task EnsureLoadedAsync(DatabaseNode node) => node.Loading ??= LoadAsync(node);

    private async Task LoadAsync(DatabaseNode node)
    {
        IReadOnlyList<DbObjectInfo> objects;
        try
        {
            objects = await _loadObjects(node.Name, CancellationToken.None);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            node.Item.ItemsSource = new[] { new TreeViewItem { Header = "Error: " + e.Message } };
            node.Loading = null;
            return;
        }
        var children = new List<ObjectNode>();
        foreach (var info in objects.Where(o => o.Ref.Kind is DbObjectKind.Table or DbObjectKind.View or DbObjectKind.Procedure
            or DbObjectKind.Function or DbObjectKind.Trigger or DbObjectKind.Event))
        {
            var child = new ObjectNode(info);
            child.Check = new CheckBox { Content = Text(info.Ref.Name), Padding = new Thickness(4, 0), MinHeight = 24, IsChecked = node.Check.IsChecked == true };
            var kind = new TextBlock { Text = KindText(info.Ref.Kind), Foreground = Brushes.Gray, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(child.Check);
            header.Children.Add(kind);
            child.Item = new TreeViewItem { Header = header };
            child.Check.IsCheckedChanged += (_, _) =>
            {
                if (!_updatingChecks)
                {
                    UpdateParent(node);
                }
            };
            children.Add(child);
        }
        node.Objects = children;
        node.Item.ItemsSource = children.Select(c => c.Item).ToList();
        ApplyFilters();
    }

    private async Task SelectInitialAsync(DatabaseNode node, IReadOnlyCollection<string>? objects)
    {
        if (objects is null or { Count: 0 })
        {
            node.Check.IsChecked = true;
            await EnsureLoadedAsync(node);
            return;
        }
        await EnsureLoadedAsync(node);
        if (node.Objects is null)
        {
            return;
        }
        _updatingChecks = true;
        foreach (var o in node.Objects)
        {
            o.Check.IsChecked = objects.Contains(o.Info.Ref.Name);
        }
        _updatingChecks = false;
        UpdateParent(node);
    }

    private void SetChildren(List<ObjectNode> objects, bool check)
    {
        _updatingChecks = true;
        foreach (var o in objects)
        {
            o.Check.IsChecked = check;
        }
        _updatingChecks = false;
    }

    private void UpdateParent(DatabaseNode node)
    {
        var objects = node.Objects ?? [];
        var count = objects.Count(o => o.Check.IsChecked == true);
        _updatingChecks = true;
        node.Check.IsChecked = count == 0 ? false : count == objects.Count ? true : null;
        _updatingChecks = false;
    }

    private void ApplyFilters()
    {
        var databases = Pattern(_databaseFilter.Text);
        var tables = Pattern(_tableFilter.Text);
        foreach (var d in _databases)
        {
            d.Item.IsVisible = databases?.IsMatch(d.Name) ?? true;
            foreach (var o in d.Objects ?? [])
            {
                o.Item.IsVisible = tables?.IsMatch(o.Info.Ref.Name) ?? true;
            }
        }
    }

    /// <summary>Case-insensitive regular expression; an invalid or empty one does not filter.</summary>
    private static Regex? Pattern(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        try
        {
            return new Regex(text, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    // ---------- Options ----------

    private void LinkDropCreate(CheckBox drop, CheckBox create)
    {
        drop.IsCheckedChanged += (_, _) =>
        {
            var on = drop.IsChecked == true;
            drop.Content = on ? "Drop!!" : "Drop";
            drop.FontWeight = on ? FontWeight.Bold : FontWeight.Normal;
            if (on)
            {
                create.IsChecked = true;
            }
        };
        create.IsCheckedChanged += (_, _) =>
        {
            if (create.IsChecked != true)
            {
                drop.IsChecked = false;
            }
        };
    }

    private SqlExportTarget Target => _output.SelectedIndex switch
    {
        < 0 => SqlExportTarget.File,
        var i when i < FileOutputs.Length => (SqlExportTarget)i,
        var i when i == FileOutputs.Length => SqlExportTarget.Database,
        _ => SqlExportTarget.Server,
    };

    private bool ToDatabase => Target is SqlExportTarget.Database or SqlExportTarget.Server;

    private SqlExportServer? SelectedServer =>
        Target == SqlExportTarget.Server && _targets is { } targets ? targets.Servers[_output.SelectedIndex - FileOutputs.Length - 1] : null;

    /// <summary>
    /// Drop/Create of the databases for file, ZIP, clipboard and server (tabletools.pas L1648-L1653); Max INSERT size
    /// only with data; the database list instead of the file name for the database and server outputs.
    /// </summary>
    private void UpdateEnabled()
    {
        var databaseBlock = Target is not (SqlExportTarget.Directory or SqlExportTarget.Database);
        _dropDatabases.IsEnabled = databaseBlock;
        _createDatabases.IsEnabled = databaseBlock;
        _maxInsert.IsEnabled = _data.SelectedIndex > 0;
        _path.IsEnabled = Target != SqlExportTarget.Clipboard;
        _recent.IsEnabled = _path.IsEnabled;
        _path.PlaceholderText = Target == SqlExportTarget.Directory ? "Directory" : "File name";
        if (_pathRow is not null)
        {
            _pathRow.IsVisible = !ToDatabase;
        }
        _targetDatabase.IsVisible = ToDatabase;
        _pathLabel.Text = ToDatabase ? "Database:" : "Filename:";
    }

    /// <summary>
    /// Database and server outputs: the databases of the target (a server connects first, as in HeidiSQL), with
    /// "[Same as on source server]" first for a server, and the last target database selected again.
    /// </summary>
    private Task LoadTargetDatabasesAsync()
    {
        _targetLoad?.Cancel();
        if (!ToDatabase || _targets is not { } targets)
        {
            return TargetLoading = Task.CompletedTask;
        }
        var cts = new CancellationTokenSource();
        _targetLoad = cts;
        var server = SelectedServer;
        _targetDatabase.ItemsSource = null;
        _status.Text = server is null ? string.Empty : $"Connecting to {server.Name}...";
        return TargetLoading = LoadAsync();

        async Task LoadAsync()
        {
            try
            {
                var names = await targets.LoadDatabases(server?.Id, cts.Token);
                if (cts.IsCancellationRequested)
                {
                    return;
                }
                List<string> items = server is null ? [.. names] : [SameAsSource, .. names];
                _targetDatabase.ItemsSource = items;
                _targetDatabase.SelectedItem = items.FirstOrDefault(n => string.Equals(n, _settings.TargetDatabase, StringComparison.Ordinal))
                    ?? (server is null ? null : SameAsSource);
                _status.Text = string.Empty;
            }
            catch (OperationCanceledException)
            {
                // Another output chosen meanwhile.
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                if (!cts.IsCancellationRequested)
                {
                    _status.Text = "Error: " + e.Message;
                }
            }
        }
    }

    private List<string> Recent() => Target switch
    {
        SqlExportTarget.Zip => _settings.ZipFilenames,
        SqlExportTarget.Directory => _settings.Directories,
        _ => _settings.Filenames,
    };

    private List<object> RecentMenu()
    {
        var items = new List<object>();
        foreach (var file in Recent())
        {
            var item = new MenuItem { Header = LiteralText.Escaped(file) };
            item.Click += (_, _) => _path.Text = file;
            items.Add(item);
        }
        if (items.Count == 0)
        {
            items.Add(new MenuItem { Header = "(no recent files)", IsEnabled = false });
        }
        return items;
    }

    private async Task BrowseAsync()
    {
        if (Target == SqlExportTarget.Clipboard)
        {
            return;
        }
        if (Target == SqlExportTarget.Directory)
        {
            if (await PathPicker.OpenFolderAsync(this, new FolderPickerOpenOptions { Title = "Export database as SQL", AllowMultiple = false }) is { } folder)
            {
                _path.Text = folder;
            }
            return;
        }
        var zip = Target == SqlExportTarget.Zip;
        var file = await PathPicker.SaveFileAsync(this, new FilePickerSaveOptions
        {
            Title = "Export database as SQL",
            DefaultExtension = zip ? "zip" : "sql",
            SuggestedFileName = string.IsNullOrWhiteSpace(_path.Text) ? (zip ? "export.zip" : "export.sql") : Path.GetFileName(_path.Text),
            FileTypeChoices = [zip ? new FilePickerFileType("ZIP files") { Patterns = ["*.zip"] } : new FilePickerFileType("SQL files") { Patterns = ["*.sql"] }],
            ShowOverwritePrompt = false,
        });
        if (file is { } path)
        {
            _path.Text = path;
        }
    }

    // ---------- Export ----------

    private async Task ExportOrCancelAsync()
    {
        if (_running is { } running)
        {
            await running.CancelAsync();
            _status.Text = "Cancelling...";
            return;
        }
        if (ToDatabase)
        {
            await ExportToDatabaseAsync();
            return;
        }
        var target = Target;
        var path = (_path.Text ?? string.Empty).Trim();
        if (target != SqlExportTarget.Clipboard)
        {
            if (path.Length == 0)
            {
                await MessageDialog.ShowAsync(this, Title ?? string.Empty, target == SqlExportTarget.Directory ? "Please select a directory." : "Please select a file name.", MessageDialogButtons.Ok);
                return;
            }
            if (target != SqlExportTarget.Directory && File.Exists(path)
                && await MessageDialog.ShowAsync(this, "Confirm", $"File already exists: {path}\n\nOverwrite it?", MessageDialogButtons.YesNo) != MessageDialogResult.Yes)
            {
                return;
            }
        }
        var databases = await CheckedAsync();
        if (databases.Count == 0)
        {
            await MessageDialog.ShowAsync(this, Title ?? string.Empty, "Please select at least one database or object.", MessageDialogButtons.Ok);
            return;
        }
        SaveSettings(path);
        var request = BuildRequest(databases, target, path);

        _running = new CancellationTokenSource();
        SetRunning(true);
        _status.Text = "Exporting...";
        _resultRows.Clear();
        ResetResults();
        string? error = null;
        try
        {
            var result = await _run(request, new Progress<SqlExportProgress>(ShowProgress), _running.Token);
            ShowProgress(new SqlExportProgress(result.Items, result.BytesWritten, result.Elapsed));
            var failed = result.Items.Count(i => i.State == SqlExportItemState.Failed);
            var size = FormatBytes(result.BytesWritten);
            _status.Text = result.Cancelled
                ? "Cancelled."
                : string.Create(CultureInfo.CurrentCulture, $"Done: {size} in {result.Elapsed.TotalSeconds:0.0} s")
                    + (failed > 0 ? string.Create(CultureInfo.CurrentCulture, $", {failed} object(s) with errors (see the file).") : ".");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            error = e.Message;
            _status.Text = "Error: " + e.Message;
        }
        finally
        {
            _running.Dispose();
            _running = null;
            SetRunning(false);
        }
        // After restoring the buttons: with the message open (or hidden behind), Close is already enabled.
        if (error is not null)
        {
            await MessageDialog.ShowAsync(this, "Error", error, MessageDialogButtons.Ok);
        }
    }

    private SqlExportRequest BuildRequest(List<SqlExportDatabase> databases, SqlExportTarget target, string path) => new()
    {
        Dump = new SqlDumpOptions
        {
            AddComments = _addComments.IsChecked == true,
            DropDatabases = _dropDatabases.IsEnabled && _dropDatabases.IsChecked == true,
            CreateDatabases = _createDatabases.IsEnabled && _createDatabases.IsChecked == true,
            DropTables = _dropTables.IsChecked == true,
            CreateTables = _createTables.IsChecked == true,
            Data = (DumpDataMode)Math.Max(0, _data.SelectedIndex),
            MaxInsertSizeKb = (int)(_maxInsert.Value ?? 1024),
            WrapInTransactions = _wrapTransactions.IsChecked == true,
            RemoveAutoIncrement = _removeAutoIncrement.IsChecked == true,
            RemoveDefiner = _removeDefiner.IsChecked == true,
        },
        Databases = databases,
        Target = target,
        Path = path,
        ConsistentSnapshot = _snapshot.IsChecked == true,
    };

    /// <summary>
    /// Output "Database" or "Server": checks, an explicit confirmation that says what happens to existing tables and
    /// rows, then the export and the run on the target, with the run's progress in the status line.
    /// </summary>
    private async Task ExportToDatabaseAsync()
    {
        if (_targets is not { } targets)
        {
            return;
        }
        var server = SelectedServer;
        var selected = _targetDatabase.SelectedItem as string;
        var targetDatabase = selected == SameAsSource ? null : selected;
        if (selected is null)
        {
            await MessageDialog.ShowAsync(this, Title ?? string.Empty, "Please select a target database.", MessageDialogButtons.Ok);
            return;
        }
        var databases = await CheckedAsync();
        if (databases.Count == 0)
        {
            await MessageDialog.ShowAsync(this, Title ?? string.Empty, "Please select at least one database or object.", MessageDialogButtons.Ok);
            return;
        }
        var request = BuildRequest(databases, Target, string.Empty) with { TargetSession = server?.Id, TargetDatabase = targetDatabase };
        if (targets.Validate(request) is { } refused)
        {
            await MessageDialog.ShowAsync(this, Title ?? string.Empty, refused, MessageDialogButtons.Ok);
            return;
        }
        var question = ConfirmationText(request, server);
        var confirmed = ConfirmOverride is { } confirm
            ? await confirm(question)
            : await MessageDialog.ShowAsync(this, "Confirm", question, MessageDialogButtons.YesNo) == MessageDialogResult.Yes;
        if (!confirmed)
        {
            return;
        }
        SaveSettings(string.Empty);
        _settings.TargetDatabase = selected;

        _running = new CancellationTokenSource();
        SetRunning(true);
        _status.Text = "Exporting...";
        _resultRows.Clear();
        ResetResults();
        var where = server is null ? $"database \"{targetDatabase}\"" : $"\"{server.Name}\"";
        // Progress<T> posts its reports: one can arrive after the end and must not replace the final status.
        var finished = false;
        var imports = new Progress<SqlFileProgress>(p =>
        {
            if (!finished)
            {
                _status.Text = string.Create(CultureInfo.CurrentCulture,
                    $"Running on {where}: {(p.TotalBytes > 0 ? p.BytesRead * 100 / p.TotalBytes : 0)}% ({p.Statements:N0} queries)...");
            }
        });
        string? error = null;
        try
        {
            var result = await targets.Run(request, new Progress<SqlExportProgress>(ShowProgress), imports, _running.Token);
            finished = true;
            ShowProgress(new SqlExportProgress(result.Export.Items, result.Export.BytesWritten, result.Export.Elapsed));
            _status.Text = ResultText(result, where);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            finished = true;
            error = e.Message;
            _status.Text = "Error: " + e.Message;
        }
        finally
        {
            _running.Dispose();
            _running = null;
            SetRunning(false);
        }
        if (error is not null)
        {
            await MessageDialog.ShowAsync(this, "Error", error, MessageDialogButtons.Ok);
        }
    }

    /// <summary>The confirmation names the target and every destructive option that is on.</summary>
    private static string ConfirmationText(SqlExportRequest request, SqlExportServer? server)
    {
        var sb = new StringBuilder();
        sb.Append(server is null
            ? $"Run the export on database \"{request.TargetDatabase}\" of this session?"
            : request.TargetDatabase is null
                ? $"Run the export on \"{server.Name}\", in databases with the same names as the exported ones?"
                : $"Run the export on database \"{request.TargetDatabase}\" of \"{server.Name}\"?");
        sb.Append("\n\n");
        var dump = request.Dump;
        if (dump.DropDatabases && request.TargetDatabase is null)
        {
            sb.Append("- Existing databases with the same names are DROPPED and created again.\n");
        }
        if (dump.DropTables)
        {
            sb.Append("- Existing tables with the same names are DROPPED and created again.\n");
        }
        sb.Append(dump.Data switch
        {
            DumpDataMode.DeleteInsert => "- All existing rows of the exported tables are DELETED first.\n",
            DumpDataMode.Replace => "- Rows with the same keys are REPLACED.\n",
            DumpDataMode.InsertIgnore => "- Rows with the same keys are kept, the new ones are skipped.\n",
            DumpDataMode.Insert => "- Rows are inserted; a duplicate key stops the run.\n",
            _ => "- No rows are copied.\n",
        });
        sb.Append("- The run stops at the first error. Statements already run are not undone.");
        return sb.ToString();
    }

    private static string ResultText(SqlExportToServerResult result, string where)
    {
        if (result.Export.Cancelled || result.Import is { Cancelled: true })
        {
            return "Cancelled." + (result.Import is { } cancelled ? string.Create(CultureInfo.CurrentCulture, $" {cancelled.Statements:N0} queries ran on {where}.") : string.Empty);
        }
        if (result.NotImported is { } reason)
        {
            return reason;
        }
        var import = result.Import!;
        if (import.StoppedOnError)
        {
            return string.Create(CultureInfo.CurrentCulture, $"Stopped at query #{import.Statements + 1:N0} on {where}: {import.ErrorList.LastOrDefault()?.Message}");
        }
        return string.Create(CultureInfo.CurrentCulture,
            $"Done: {FormatBytes(result.Export.BytesWritten)} exported and {import.Statements:N0} queries run on {where} in {(result.Export.Elapsed + import.Elapsed).TotalSeconds:0.0} s.");
    }

    /// <summary>Checked databases and objects, in tree order; a checked database not yet opened loads now.</summary>
    private async Task<List<SqlExportDatabase>> CheckedAsync()
    {
        var result = new List<SqlExportDatabase>();
        foreach (var d in _databases)
        {
            if (d.Check.IsChecked == false)
            {
                continue;
            }
            await EnsureLoadedAsync(d);
            var objects = (d.Objects ?? []).Where(o => o.Check.IsChecked == true).Select(o => o.Info).ToList();
            if (objects.Count > 0 || d.Check.IsChecked == true)
            {
                result.Add(new SqlExportDatabase(d.Name, objects));
            }
        }
        return result;
    }

    private void SaveSettings(string path)
    {
        _settings.CreateDatabases = _createDatabases.IsChecked == true;
        _settings.CreateTables = _createTables.IsChecked == true;
        _settings.Data = Math.Max(0, _data.SelectedIndex);
        if (_settings.Data > 0)
        {
            _settings.MaxInsertSizeKb = (int)(_maxInsert.Value ?? 1024);
        }
        // Server entries are never preselected the next time (HeidiSQL issue #3411).
        if (Target != SqlExportTarget.Server)
        {
            _settings.Output = Math.Max(0, _output.SelectedIndex);
        }
        _settings.AddComments = _addComments.IsChecked == true;
        _settings.WrapInTransactions = _wrapTransactions.IsChecked == true;
        _settings.RemoveAutoIncrement = _removeAutoIncrement.IsChecked == true;
        _settings.RemoveDefiner = _removeDefiner.IsChecked == true;
        _settings.ConsistentSnapshot = _snapshot.IsChecked == true;
        _settings.DatabaseFilter = _databaseFilter.Text ?? string.Empty;
        _settings.TableFilter = _tableFilter.Text ?? string.Empty;
        if (Target != SqlExportTarget.Clipboard && !ToDatabase && path.Length > 0)
        {
            var recent = Recent();
            recent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            recent.Insert(0, path);
            if (recent.Count > MaxRecent)
            {
                recent.RemoveRange(MaxRecent, recent.Count - MaxRecent);
            }
        }
    }

    private void SetRunning(bool running)
    {
        _export.Content = running ? "Cancel" : "Export";
        _close.IsEnabled = !running;
        foreach (var control in new Control[] { _tree, _databaseFilter, _tableFilter, _dropDatabases, _createDatabases, _dropTables, _createTables, _data, _maxInsert, _output, _path, _recent, _targetDatabase,
            _addComments, _wrapTransactions, _removeAutoIncrement, _removeDefiner, _snapshot })
        {
            control.IsEnabled = !running;
        }
        if (!running)
        {
            UpdateEnabled();
        }
    }

    // ---------- Results ----------

    private void ResetResults()
    {
        _results.Children.Clear();
        _results.RowDefinitions.Clear();
        _results.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        string[] headers = ["Database", "Table", "Rows", "Duration"];
        for (var c = 0; c < headers.Length; c++)
        {
            var header = new TextBlock { Text = headers[c], FontWeight = FontWeight.SemiBold, Margin = new Thickness(4, 2) };
            Grid.SetColumn(header, c);
            _results.Children.Add(header);
        }
    }

    private void ShowProgress(SqlExportProgress progress)
    {
        foreach (var item in progress.Items)
        {
            var key = (item.Database, item.Name, item.Kind);
            if (!_resultRows.TryGetValue(key, out var cells))
            {
                var row = _results.RowDefinitions.Count;
                _results.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                cells = new TextBlock[4];
                for (var c = 0; c < 4; c++)
                {
                    cells[c] = new TextBlock { Margin = new Thickness(4, 1), TextAlignment = c >= 2 ? TextAlignment.Right : TextAlignment.Left };
                    Grid.SetRow(cells[c], row);
                    Grid.SetColumn(cells[c], c);
                    _results.Children.Add(cells[c]);
                }
                _resultRows[key] = cells;
            }
            var c0 = CultureInfo.CurrentCulture;
            cells[0].Text = item.Database;
            cells[1].Text = item.Kind == DbObjectKind.Table ? item.Name : item.Name + " (" + KindText(item.Kind) + ")";
            cells[2].Text = item.State switch
            {
                SqlExportItemState.Waiting => string.Empty,
                SqlExportItemState.Skipped => "skipped",
                SqlExportItemState.Failed => "error: " + item.Error,
                SqlExportItemState.Running when item.Kind != DbObjectKind.Table || item.Rows == 0 => "running...",
                _ when item.Kind != DbObjectKind.Table => item.State == SqlExportItemState.Done ? "done" : string.Empty,
                _ => item.EstimatedRows > 0
                    ? string.Create(c0, $"{item.Rows:N0} / {Math.Min(100, item.Rows * 100 / item.EstimatedRows)}%")
                    : string.Create(c0, $"{item.Rows:N0}"),
            };
            cells[3].Text = item.State == SqlExportItemState.Waiting ? string.Empty : string.Create(c0, $"{item.Duration.TotalSeconds:0.00} s");
            if (item.State == SqlExportItemState.Cancelled)
            {
                cells[3].Text += " (cancelled)";
            }
        }
        if (_running is not null)
        {
            // The bytes only count what is already in the file, in the plan's order: while waiting for one object, the
            // next ones may be finishing on another connection. The count and the oldest running object show that the
            // export is moving on (or which object it is waiting for).
            var finished = progress.Items.Count(i => i.State is SqlExportItemState.Done or SqlExportItemState.Skipped or SqlExportItemState.Failed);
            var text = string.Create(CultureInfo.CurrentCulture, $"Exporting... {FormatBytes(progress.BytesWritten)}, {finished:N0} of {progress.Items.Count:N0} objects");
            if (progress.Items.FirstOrDefault(i => i.State == SqlExportItemState.Running) is { } oldest && oldest.Duration >= SlowObject)
            {
                text += string.Create(CultureInfo.CurrentCulture, $", waiting for {oldest.Name} ({oldest.Duration.TotalSeconds:0} s)");
            }
            _status.Text = text;
        }
    }

    /// <summary>From this point on, the status line says which object the export is waiting for.</summary>
    private static readonly TimeSpan SlowObject = TimeSpan.FromSeconds(5);

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => string.Create(CultureInfo.CurrentCulture, $"{bytes / (1024.0 * 1024 * 1024):0.00} GiB"),
        >= 1024L * 1024 => string.Create(CultureInfo.CurrentCulture, $"{bytes / (1024.0 * 1024):0.0} MiB"),
        >= 1024 => string.Create(CultureInfo.CurrentCulture, $"{bytes / 1024.0:0.0} KiB"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{bytes} B"),
    };

    private static string KindText(DbObjectKind kind) => kind switch
    {
        DbObjectKind.View => "view",
        DbObjectKind.Procedure => "procedure",
        DbObjectKind.Function => "function",
        DbObjectKind.Trigger => "trigger",
        DbObjectKind.Event => "event",
        _ => "table",
    };

    // ---------- Layout ----------

    /// <summary>Text in a TextBlock: in string content, the "_" would be an access key and disappear.</summary>
    private static TextBlock Text(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center };

    private static CheckBox Option(string text) => new() { Content = Text(text), MinHeight = 24 };

    private static TextBox Box(string placeholder) => new TextBox { PlaceholderText = placeholder, MinHeight = 22, Padding = new Thickness(4, 1) }.Named(placeholder);

    private Control Layout(Button browse)
    {
        var treePanel = new DockPanel();
        var filters = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 4) };
        filters.Children.Add(_databaseFilter);
        filters.Children.Add(_tableFilter);
        DockPanel.SetDock(filters, Dock.Top);
        treePanel.Children.Add(filters);
        treePanel.Children.Add(Themed.Bind(new Border { BorderThickness = new Thickness(1), Child = _tree }, Border.BorderBrushProperty, "border"));

        var pathRow = new DockPanel();
        _pathRow = pathRow;
        DockPanel.SetDock(_recent, Dock.Left);
        DockPanel.SetDock(browse, Dock.Right);
        pathRow.Children.Add(_recent);
        pathRow.Children.Add(browse);
        pathRow.Children.Add(_path);
        var destination = new Panel { Margin = new Thickness(0, 3) };
        destination.Children.Add(pathRow);
        destination.Children.Add(_targetDatabase);

        var options = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto") };
        AddRow(options, 0, "Database(s):", Horizontal(_dropDatabases, _createDatabases));
        AddRow(options, 1, "Table(s):", Horizontal(_dropTables, _createTables));
        AddRow(options, 2, "Data:", _data);
        AddRow(options, 3, "Max INSERT size:", Horizontal(_maxInsert, new TextBlock { Text = "KB (0 = Single INSERTs)", VerticalAlignment = VerticalAlignment.Center }));
        AddRow(options, 4, "Output:", _output);
        Grid.SetRow(_pathLabel, 5);
        options.Children.Add(_pathLabel);
        Grid.SetRow(destination, 5);
        Grid.SetColumn(destination, 1);
        options.Children.Add(destination);
        var extra = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"), Margin = new Thickness(0, 6, 0, 0) };
        extra.Children.Add(new TextBlock { Text = "Options", FontWeight = FontWeight.SemiBold });
        Control[] checks = [_addComments, _wrapTransactions, _removeAutoIncrement, _removeDefiner, _snapshot];
        for (var i = 0; i < checks.Length; i++)
        {
            Grid.SetRow(checks[i], 1 + (i / 2));
            Grid.SetColumn(checks[i], i % 2);
            if (i == checks.Length - 1)
            {
                Grid.SetColumnSpan(checks[i], 2);
            }
            extra.Children.Add(checks[i]);
        }
        Grid.SetRow(extra, 6);
        Grid.SetColumnSpan(extra, 2);
        options.Children.Add(extra);

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("280,12,*") };
        top.Children.Add(treePanel);
        Grid.SetColumn(options, 2);
        top.Children.Add(options);

        ResetResults();
        var resultsBox = Themed.Bind(new Border
        {
            BorderThickness = new Thickness(1),
            Child = new ScrollViewer { Content = _results, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
        }, Border.BorderBrushProperty, "border");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(_export);
        buttons.Children.Add(_close);

        var root = new Grid { Margin = new Thickness(12), RowDefinitions = new RowDefinitions("*,8,160,Auto,Auto") };
        root.Children.Add(top);
        Grid.SetRow(resultsBox, 2);
        root.Children.Add(resultsBox);
        Grid.SetRow(_status, 3);
        root.Children.Add(_status);
        Grid.SetRow(buttons, 4);
        root.Children.Add(buttons);
        return root;
    }

    private static StackPanel Horizontal(params Control[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        foreach (var c in children)
        {
            panel.Children.Add(c);
        }
        return panel;
    }

    private static void AddRow(Grid grid, int row, string label, Control control)
    {
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3) };
        control.Named(label);
        Grid.SetRow(text, row);
        grid.Children.Add(text);
        control.Margin = new Thickness(0, 3);
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
    }

    public static Task ShowAsync(
        Window owner,
        SqlExportSettings settings,
        IReadOnlyList<string> databases,
        Func<string, CancellationToken, Task<IReadOnlyList<DbObjectInfo>>> loadObjects,
        SqlExportRunner run,
        string? initialDatabase,
        IReadOnlyCollection<string>? initialObjects,
        SqlExportTargets? targets = null) =>
        new SqlExportDialog(settings, databases, loadObjects, run, initialDatabase, initialObjects, targets).ShowDialog(owner);
}
