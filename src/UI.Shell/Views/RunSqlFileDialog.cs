using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Eunomia.UI.Controls.Common.Brand;
using Eunomia.UI.Controls.Common.Input;
using Eunomia.UI.Controls.Common.Theming;

namespace Eunomia.UI.Shell.Views;

/// <summary>Where Run SQL file runs: the database (or none) and whether the file's own <c>USE</c> is followed.</summary>
/// <param name="StayInDatabase">The file's <c>USE</c> and <c>CREATE</c>/<c>DROP</c>/<c>ALTER DATABASE</c> are skipped.</param>
public sealed record RunSqlFileTarget(string? Database, bool StayInDatabase);

/// <summary>
/// Before Run SQL file: the server and the database the files run in (the session's current database by default, any
/// other can be chosen, with a filter), and, when a file selects its own database with <c>USE</c> (a dump that creates
/// its database), whether its statements follow that <c>USE</c> or all run in the chosen database. The run itself does
/// not change the session's current database.
/// </summary>
public sealed class RunSqlFileDialog : Window
{
    private const int MaxFilesShown = 5;

    private readonly SearchableSelect _database = new() { PlaceholderText = "(none)", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly RadioButton _asFile = new() { GroupName = "target", IsChecked = true };
    private readonly RadioButton _stay = new() { GroupName = "target" };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray };
    private readonly Button _run = new() { Content = "Run", MinWidth = 75, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
    private readonly IReadOnlyList<string> _fileDatabases;

    public RunSqlFileDialog(string server, IReadOnlyList<string> files, IReadOnlyList<string> databases, string? currentDatabase, IReadOnlyList<string> fileDatabases)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(databases);
        ArgumentNullException.ThrowIfNull(fileDatabases);
        Icon = BrandAssets.WindowIcon;
        Title = "Run SQL file";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = UiFonts.Ui;
        FontSize = 12;
        _fileDatabases = fileDatabases;
        Server = server;
        Files = files.Count <= MaxFilesShown
            ? string.Join("\n", files.Select(Path.GetFileName))
            : string.Join("\n", files.Take(MaxFilesShown).Select(Path.GetFileName)) + $"\n... and {files.Count - MaxFilesShown} more";

        _database.Items = databases;
        _database.SelectedItem = currentDatabase;
        Avalonia.Automation.AutomationProperties.SetName(_database.Box, "Database");
        _database.SelectionChanged += (_, _) => Update();
        var used = string.Join(", ", fileDatabases.Select(d => "\"" + d + "\""));
        _asFile.Content = Wrapped($"As in the file: the statements after its USE run in {used}");
        _stay.Content = Wrapped("Everything in the database above: the file's USE and CREATE / DROP / ALTER DATABASE are skipped");
        Avalonia.Automation.AutomationProperties.SetName(_asFile, "Run as in the file");
        Avalonia.Automation.AutomationProperties.SetName(_stay, "Run everything in the chosen database");

        var cancel = new Button { Content = "Cancel", MinWidth = 75, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        _run.Click += (_, _) => Close(Result);
        cancel.Click += (_, _) => Close(null);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(_run);
        buttons.Children.Add(cancel);

        var form = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), RowSpacing = 8, ColumnSpacing = 10 };
        AddRow(form, 0, "File(s):", new TextBlock { Text = Files, TextWrapping = TextWrapping.Wrap });
        AddRow(form, 1, "Server:", new TextBlock { Text = server, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold });
        AddRow(form, 2, "Database:", _database);

        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 8 };
        panel.Children.Add(form);
        if (fileDatabases.Count > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"The file selects its own database with USE ({used}).",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
            });
            panel.Children.Add(_asFile);
            panel.Children.Add(_stay);
        }
        panel.Children.Add(_hint);
        panel.Children.Add(buttons);
        Content = panel;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close(null);
            }
        };
        Update();
    }

    public string Server { get; }

    internal string Files { get; }

    internal SearchableSelect DatabaseSelect => _database;

    internal RadioButton AsFileOption => _asFile;

    internal RadioButton StayOption => _stay;

    internal string Hint => _hint.Text ?? string.Empty;

    /// <summary>What Run returns with the current choices.</summary>
    internal RunSqlFileTarget Result => new(_database.SelectedItem, _stay.IsChecked == true && _database.SelectedItem is not null && _fileDatabases.Count > 0);

    public static Task<RunSqlFileTarget?> ShowAsync(Window owner, RunSqlFileDialog dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        return dialog.ShowDialog<RunSqlFileTarget?>(owner);
    }

    private void Update()
    {
        var database = _database.SelectedItem;
        _stay.IsEnabled = database is not null;
        if (database is null && _stay.IsChecked == true)
        {
            _asFile.IsChecked = true;
        }
        _hint.Text = (database, _fileDatabases.Count) switch
        {
            (null, 0) => "No database: statements that do not name their database fail.",
            (null, _) => "Choose a database to run everything in it.",
            _ => "The session's current database does not change.",
        };
    }

    private static TextBlock Wrapped(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    private static void AddRow(Grid grid, int row, string label, Control value)
    {
        var caption = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(caption, row);
        Grid.SetRow(value, row);
        Grid.SetColumn(value, 1);
        grid.Children.Add(caption);
        grid.Children.Add(value);
    }
}
