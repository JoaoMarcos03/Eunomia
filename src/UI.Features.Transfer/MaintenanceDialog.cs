using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Eunomia.App.Objects;
using Eunomia.Drivers.Abstractions;
using Eunomia.UI.Controls.Common.Accessibility;
using Eunomia.UI.Controls.Common.Brand;
using Eunomia.UI.Controls.Common.Formatting;
using Eunomia.UI.Controls.Common.Text;
using Eunomia.UI.Controls.Common.Theming;
using Eunomia.UI.Controls.Common.TreeList;

namespace Eunomia.UI.Features.Transfer;

/// <summary>What the Maintenance dialog needs from the session.</summary>
/// <param name="Selected">Objects already chosen in the tree or the list (the others stay unchecked); empty: all of them.</param>
/// <param name="LoadObjects">Tables and views of the database, with their sizes if already known.</param>
/// <param name="OptionsOf">Options each operation accepts (the enabled boxes).</param>
/// <param name="Confirm">Question (OK/Cancel) owned by the dialog, before OPTIMIZE and REPAIR.</param>
/// <param name="Run">Runs the operation on an object, on a jobs connection.</param>
public sealed record MaintenanceServices(
    IReadOnlyList<string> Databases,
    string? Database,
    IReadOnlyList<string> Selected,
    Func<string, CancellationToken, Task<IReadOnlyList<DbObjectInfo>>> LoadObjects,
    Func<MaintenanceOperation, MaintenanceOptions> OptionsOf,
    Func<Window, MaintenanceOperation, int, MaintenanceOptions, Task<bool>> Confirm,
    Func<MaintenanceOperation, DbObjectRef, MaintenanceOptions, CancellationToken, Task<IReadOnlyList<MaintenanceMessage>>> Run);

/// <summary>
/// Maintenance (Tools ▸ Maintenance and popupDB; appendix B §2.2, the "Maintenance" tab of Table tools): database and objects
/// with check boxes on the left, operation and options on the right, and at the bottom the server's rows per object (errors
/// and notes in the SQL log colours) and "N selected objects, size: X". While it runs, Close becomes Cancel and stops after the
/// object in progress.
/// </summary>
public sealed class MaintenanceDialog : Window
{
    private static readonly (MaintenanceOptions Flag, string Caption)[] OptionCaptions =
    [
        (MaintenanceOptions.Quick, "Quick"),
        (MaintenanceOptions.Fast, "Fast"),
        (MaintenanceOptions.Medium, "Medium"),
        (MaintenanceOptions.Extended, "Extended"),
        (MaintenanceOptions.Changed, "Changed"),
        (MaintenanceOptions.UseFrm, "Use FRM file"),
        (MaintenanceOptions.ForUpgrade, "For Upgrade"),
    ];

    private readonly MaintenanceServices _services;
    private readonly ComboBox _database = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Name = "maintenanceDatabase" }.Named("Database");
    private readonly StackPanel _objects = new() { Spacing = 0 };
    private readonly ComboBox _operation = new ComboBox
    {
        ItemsSource = Enum.GetValues<MaintenanceOperation>().Select(TableMaintenance.Caption).ToList(),
        SelectedIndex = 0,
        Width = 200,
        Name = "maintenanceOperation",
    }.Named("Operation");
    private readonly Dictionary<MaintenanceOptions, CheckBox> _options = [];
    private readonly TreeListView _results = new();
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _selection = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button _execute = new() { Content = "Execute", IsDefault = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center, Name = "btnExecute" };
    private readonly Button _close = new() { Content = "Close", IsCancel = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center, Name = "btnClose" };
    private readonly Dictionary<string, DbObjectInfo> _infos = new(StringComparer.Ordinal);
    private readonly List<TreeListNode> _rows = [];
    private CancellationTokenSource? _run;

    public MaintenanceDialog(MaintenanceServices services)
    {
        Icon = BrandAssets.WindowIcon;
        _services = services ?? throw new ArgumentNullException(nameof(services));
        Title = "Maintenance";
        Width = 820;
        Height = 560;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = UiFonts.Ui;
        FontSize = 12;

        _database.ItemsSource = services.Databases;
        _database.SelectionChanged += (_, _) => _ = LoadObjectsAsync();
        var checkAll = new Button { Content = "All" };
        checkAll.Click += (_, _) => SetAll(true);
        var checkNone = new Button { Content = "None" };
        checkNone.Click += (_, _) => SetAll(false);
        var objectButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 6, 0, 0) };
        objectButtons.Children.Add(checkAll);
        objectButtons.Children.Add(checkNone);
        var left = new DockPanel();
        DockPanel.SetDock(_database, Dock.Top);
        DockPanel.SetDock(_selection, Dock.Bottom);
        DockPanel.SetDock(objectButtons, Dock.Bottom);
        left.Children.Add(_database);
        left.Children.Add(_selection);
        left.Children.Add(objectButtons);
        left.Children.Add(Bordered(new ScrollViewer { Content = _objects, Margin = new Thickness(0, 6, 0, 0) }));

        var options = new WrapPanel { Orientation = Orientation.Horizontal, ItemWidth = 130 };
        foreach (var (flag, caption) in OptionCaptions)
        {
            var box = new CheckBox { Content = caption, MinHeight = 0, Margin = new Thickness(0, 2) };
            _options[flag] = box;
            options.Children.Add(box);
        }
        _operation.SelectionChanged += (_, _) => UpdateOptions();
        var form = new StackPanel { Spacing = 6, Margin = new Thickness(12, 0, 0, 0) };
        form.Children.Add(new TextBlock { Text = "Operation:" });
        form.Children.Add(_operation);
        form.Children.Add(new TextBlock { Text = "Options:", Margin = new Thickness(0, 6, 0, 0) });
        form.Children.Add(options);

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("260,*"), Height = 230 };
        Grid.SetColumn(form, 1);
        top.Children.Add(left);
        top.Children.Add(form);

        var list = _results.List;
        list.Columns.Add(new TreeListColumn("Table", 220));
        list.Columns.Add(new TreeListColumn("Op", 80));
        list.Columns.Add(new TreeListColumn("Msg_type", 80));
        list.Columns.Add(new TreeListColumn("Msg_text", 360));
        // In the server's order (the rows of a table follow each other); a click on the header sorts.
        list.SortBy(-1, descending: false);
        _results.Named("Results");

        _execute.Click += (_, _) => _ = ExecuteAsync();
        _close.Click += (_, _) =>
        {
            if (_run is { } running)
            {
                running.Cancel();
                _status.Text = "Processing cancelled by user, waiting for current object to finish ...";
            }
            else
            {
                Close();
            }
        };
        var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(_execute);
        buttons.Children.Add(_close);
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);
        bottom.Children.Add(_status);

        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(Bordered(_results, new Thickness(0, 10, 0, 0)));
        Content = root;

        UpdateOptions();
        _database.SelectedItem = services.Database is { } db && services.Databases.Contains(db) ? db : services.Databases.FirstOrDefault();
        Closing += (_, _) =>
        {
            // Closing halfway stops after the object in progress (the server finishes the statement it already received).
            _run?.Cancel();
        };
    }

    /// <summary>The database's objects with their check boxes (tests).</summary>
    public IReadOnlyList<CheckBox> ObjectBoxes => _objects.Children.OfType<CheckBox>().ToList();

    public ComboBox OperationBox => _operation;

    /// <summary>Check box of an option (tests).</summary>
    public CheckBox OptionBox(MaintenanceOptions option) => _options[option];

    public TreeListControl Results => _results.List;

    public string? StatusText => _status.Text;

    public string? SelectionText => _selection.Text;

    public bool IsRunning => _run is not null;

    private MaintenanceOperation Operation => (MaintenanceOperation)Math.Max(0, _operation.SelectedIndex);

    /// <summary>Tables and views of the chosen database; those already chosen in the tree come checked (otherwise, all of them).</summary>
    public async Task LoadObjectsAsync()
    {
        _objects.Children.Clear();
        _infos.Clear();
        UpdateSelection();
        if (_database.SelectedItem is not string db)
        {
            return;
        }
        _status.Text = "Loading objects ...";
        try
        {
            var objects = await _services.LoadObjects(db, CancellationToken.None);
            if (!Equals(_database.SelectedItem, db))
            {
                return;
            }
            var preselected = string.Equals(db, _services.Database, StringComparison.Ordinal) && _services.Selected.Count > 0
                ? _services.Selected.ToHashSet(StringComparer.Ordinal)
                : null;
            foreach (var info in objects.Where(o => o.Ref.Kind is DbObjectKind.Table or DbObjectKind.View))
            {
                _infos[info.Ref.Name] = info;
                var box = new CheckBox
                {
                    Content = LiteralText.Of(info.Ref.Name),
                    Tag = info.Ref.Name,
                    IsChecked = preselected?.Contains(info.Ref.Name) ?? true,
                    MinHeight = 0,
                    Padding = new Thickness(4, 1),
                };
                box.IsCheckedChanged += (_, _) => UpdateSelection();
                _objects.Children.Add(box.Named(info.Ref.Name));
            }
            _status.Text = string.Create(CultureInfo.CurrentCulture, $"{_infos.Count} table(s) and view(s) in {db}.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _status.Text = ex.Message;
        }
        UpdateSelection();
    }

    /// <summary>Execute: the operation on each chosen object, in order; Close (Cancel) stops after the object in progress.</summary>
    public async Task ExecuteAsync()
    {
        if (_run is not null || _database.SelectedItem is not string db)
        {
            return;
        }
        var chosen = ObjectBoxes.Where(b => b.IsChecked == true).Select(b => _infos[(string)b.Tag!].Ref).ToList();
        if (chosen.Count == 0)
        {
            _status.Text = "Please select objects first.";
            return;
        }
        var operation = Operation;
        var options = ReadOptions();
        if (TableMaintenance.Rebuilds(operation) && !await _services.Confirm(this, operation, chosen.Count, options))
        {
            return;
        }
        var cts = new CancellationTokenSource();
        _run = cts;
        SetRunning(true);
        _rows.Clear();
        _results.List.SetRoots([]);
        var done = 0;
        try
        {
            foreach (var table in chosen)
            {
                if (cts.IsCancellationRequested)
                {
                    break;
                }
                _status.Text = string.Create(CultureInfo.CurrentCulture, $"{TableMaintenance.Caption(operation)} {table.Name} ({done + 1} of {chosen.Count}) ...");
                IReadOnlyList<MaintenanceMessage> messages;
                try
                {
                    messages = await _services.Run(operation, table, options, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // An error on one object does not stop the others (appendix B §2.1); it shows as a red row.
                    messages = [new MaintenanceMessage(db + "." + table.Name, TableMaintenance.Caption(operation).ToLowerInvariant(), "error", ex.Message)];
                }
                foreach (var message in messages)
                {
                    _rows.Add(Row(message));
                }
                _results.List.SetRoots([.. _rows]);
                done++;
            }
        }
        finally
        {
            _run = null;
            SetRunning(false);
        }
        _status.Text = cts.IsCancellationRequested
            ? string.Create(CultureInfo.CurrentCulture, $"Processing cancelled: {done} of {chosen.Count} object(s) done.")
            : string.Create(CultureInfo.CurrentCulture, $"{done} object(s) processed.");
    }

    private TreeListNode Row(MaintenanceMessage message)
    {
        var theme = ThemeTokens.Of(ActualThemeVariant);
        return new TreeListNode(message.Table, message.Operation, message.Type, message.Text)
        {
            Background = message.IsError ? theme.Argb("log.error") : message.IsNote ? theme.Argb("log.note") : null,
        };
    }

    private MaintenanceOptions ReadOptions() =>
        _options.Where(o => o.Value.IsEnabled && o.Value.IsChecked == true).Aggregate(MaintenanceOptions.None, (all, o) => all | o.Key);

    /// <summary>The boxes the operation accepts are enabled; the others are greyed out (appendix B §2.2).</summary>
    private void UpdateOptions()
    {
        var allowed = _services.OptionsOf(Operation);
        foreach (var (flag, box) in _options)
        {
            box.IsEnabled = _run is null && allowed.HasFlag(flag);
        }
    }

    private void UpdateSelection()
    {
        var chosen = ObjectBoxes.Where(b => b.IsChecked == true).Select(b => _infos.GetValueOrDefault((string)b.Tag!)).OfType<DbObjectInfo>().ToList();
        var known = chosen.Where(o => o.DataBytes is not null || o.IndexBytes is not null).ToList();
        var size = known.Sum(o => (o.DataBytes ?? 0) + (o.IndexBytes ?? 0));
        _selection.Text = known.Count == 0
            ? string.Create(CultureInfo.CurrentCulture, $"{chosen.Count} selected objects")
            : string.Create(CultureInfo.CurrentCulture, $"{chosen.Count} selected objects, size: {SizeFormatter.Format(size, CultureInfo.CurrentCulture)}");
    }

    private void SetAll(bool value)
    {
        foreach (var box in ObjectBoxes)
        {
            box.IsChecked = value;
        }
    }

    private void SetRunning(bool running)
    {
        _execute.IsEnabled = !running;
        _database.IsEnabled = !running;
        _operation.IsEnabled = !running;
        _objects.IsEnabled = !running;
        _close.Content = running ? "Cancel" : "Close";
        UpdateOptions();
    }

    private static Border Bordered(Control child, Thickness margin = default) =>
        Themed.Bind(new Border { Child = child, BorderThickness = new Thickness(1), Margin = margin }, Border.BorderBrushProperty, "border");
}
