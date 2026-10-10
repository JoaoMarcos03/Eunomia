using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Eunomia.Drivers.Abstractions;
using Eunomia.Presentation.Objects;
using Eunomia.UI.Controls.Common.Accessibility;
using Eunomia.UI.Controls.Common.TreeList;
using Eunomia.UI.Controls.SqlEditor;

namespace Eunomia.UI.Features.HostTab;

/// <summary>
/// Host tab (section 4.9; appendix C §1.8): Databases sub-tab (with bars and "Fetch database items"), Variables (session and
/// global, with the session value highlighted when it differs), Status (with per-hour and per-second averages), Processes
/// (Kill and the "Process SQL:" panel) and Command-Statistics (<c>Com_*</c> counters with percentage bars). Read-only:
/// editing variables comes later. Each sub-tab's caption becomes "Name (n)" after loading.
/// </summary>
public sealed class HostTabView : TabControl
{
    private static readonly CellFormat[] DatabaseFormats =
        [CellFormat.Text, CellFormat.Size, CellFormat.Number, CellFormat.DateTime, .. Enumerable.Repeat(CellFormat.Number, 6), CellFormat.Text];

    private static readonly CellFormat[] ProcessFormats =
        [CellFormat.Text, CellFormat.Text, CellFormat.Text, CellFormat.Text, CellFormat.Text, CellFormat.Number, CellFormat.Text, CellFormat.Text];

    private static readonly CellFormat[] StatusFormats = [CellFormat.Text, CellFormat.Text, CellFormat.Decimal, CellFormat.Decimal];

    private static readonly CellFormat[] CommandFormats = [CellFormat.Text, CellFormat.Number, CellFormat.Decimal, CellFormat.Decimal, CellFormat.Percent];

    private readonly TreeListView _databases = new();
    private readonly TreeListView _variables = new();
    private readonly TreeListView _status = new();
    private readonly TreeListView _processes = new();
    private readonly TreeListView _commands = new();

    public HostTabView()
    {
        this.Named("Host");
        Padding = new Thickness(0);
        // Same size as the main tabs (the shell's style does not reach code that uses the view on its own).
        Styles.Add(new Style(x => x.OfType<TabItem>())
        {
            Setters =
            {
                new Setter(FontSizeProperty, 12.0),
                new Setter(MinHeightProperty, 24.0),
                new Setter(PaddingProperty, new Thickness(8, 2)),
            },
        });
        DatabaseList.MultiSelect = true;
        DatabaseList.GroupsFirst = false;
        AddColumns(DatabaseList, HostDatabases.Columns);
        DatabaseList.NodeActivated += (_, node) =>
        {
            if (node.Tag is string db)
            {
                DatabaseActivated?.Invoke(this, db);
            }
        };
        DatabaseList.HeaderContextRequested += (_, _) => TreeListHeaderMenu.Show(DatabaseList, () => { });

        foreach (var (list, columns) in new[] { (VariableList, HostVariables.Columns), (StatusList, HostStatus.Columns), (CommandList, CommandStatistics.Columns) })
        {
            list.MultiSelect = true;
            list.GroupsFirst = false;
            AddColumns(list, columns);
            list.HeaderContextRequested += (_, _) => TreeListHeaderMenu.Show(list, () => { });
        }
        CommandList.SortBy(1, descending: true);

        ProcessList.MultiSelect = true;
        ProcessList.GroupsFirst = false;
        AddColumns(ProcessList, Presentation.Objects.ProcessList.Columns);
        ProcessList.SortBy(0, descending: true);
        ProcessList.HeaderContextRequested += (_, _) => TreeListHeaderMenu.Show(ProcessList, () => { });
        ProcessSql = new SqlEditorControl { MinHeight = 40 };
        ProcessSql.Editor.IsReadOnly = true;
        ProcessSql.ShowLineNumbers = false;
        ProcessList.FocusedNodeChanged += (_, _) => ProcessSql.Text = (ProcessList.FocusedNode?.Tag as ServerProcess)?.Info ?? string.Empty;

        var processPanel = new Grid { RowDefinitions = new RowDefinitions("*,4,Auto,69") };
        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Rows };
        var label = new TextBlock { Text = "Process SQL:", Margin = new Thickness(4, 2) };
        Grid.SetRow(splitter, 1);
        Grid.SetRow(label, 2);
        Grid.SetRow(ProcessSql, 3);
        processPanel.Children.Add(_processes);
        processPanel.Children.Add(splitter);
        processPanel.Children.Add(label);
        processPanel.Children.Add(ProcessSql);

        DatabasesTab = Tab("Databases", "database", _databases);
        VariablesTab = Tab("Variables", "variables", _variables);
        StatusTab = Tab("Status", "status", _status);
        ProcessesTab = Tab("Processes", "processes", processPanel);
        CommandStatisticsTab = Tab("Command-Statistics", "statistics", _commands);
        Items.Add(DatabasesTab);
        Items.Add(VariablesTab);
        Items.Add(StatusTab);
        Items.Add(ProcessesTab);
        Items.Add(CommandStatisticsTab);
        SelectedItem = DatabasesTab;
    }

    protected override Type StyleKeyOverride => typeof(TabControl);

    public TabItem DatabasesTab { get; }

    public TabItem VariablesTab { get; }

    public TabItem StatusTab { get; }

    public TabItem ProcessesTab { get; }

    public TabItem CommandStatisticsTab { get; }

    public TreeListControl DatabaseList => _databases.List;

    public TreeListControl ProcessList => _processes.List;

    public TreeListControl VariableList => _variables.List;

    public TreeListControl StatusList => _status.List;

    public TreeListControl CommandList => _commands.List;

    /// <summary>"Process SQL:" panel (read-only) with the Info of the focused process.</summary>
    public SqlEditorControl ProcessSql { get; }

    public IReadOnlyList<string> SelectedDatabases => DatabaseList.SelectedNodes.Select(n => (string)n.Tag!).ToList();

    public IReadOnlyList<ServerProcess> SelectedProcesses => ProcessList.SelectedNodes.Select(n => (ServerProcess)n.Tag!).ToList();

    /// <summary>Double click or Enter on a database: opens it.</summary>
    public event EventHandler<string>? DatabaseActivated;

    public void ShowDatabases(IReadOnlyList<HostDatabaseRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var nodes = rows.Select(r => TreeListRows.Create(
            [
                r.Name, r.Size, r.Items is { } items ? (long)items : null, r.LastModified,
                .. HostDatabases.CountKinds.Select(k => r.CountOf(k) is { } n ? (object)(long)n : null),
                r.Collation,
            ],
            DatabaseFormats,
            tag: r.Name)).ToList();
        foreach (var node in nodes)
        {
            node.Icon = "database";
        }
        TreeListRows.ApplyBars(nodes, [1, 2, 4, 5, 6, 7, 8, 9]);
        DatabaseList.SetRoots(nodes);
        SetCaption(DatabasesTab, "Databases", rows.Count);
    }

    public void ShowVariables(IReadOnlyList<HostVariableRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var nodes = rows.Select(r => new TreeListNode(r.Name, r.Session ?? string.Empty, r.Global ?? string.Empty)
        {
            Tag = r,
            Highlighted = r.Differs ? [1] : null,
        }).ToList();
        VariableList.SetRoots(nodes);
        SetCaption(VariablesTab, "Variables", rows.Count);
    }

    public void ShowStatus(IReadOnlyList<HostStatusRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var nodes = rows.Select(r => TreeListRows.Create([r.Name, r.Value, r.PerHour, r.PerSecond], StatusFormats, tag: r)).ToList();
        StatusList.SetRoots(nodes);
        SetCaption(StatusTab, "Status", rows.Count);
    }

    public void ShowCommandStatistics(IReadOnlyList<CommandStatisticsRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var nodes = rows.Select(r => TreeListRows.Create([r.Command, r.Count, r.PerHour, r.PerSecond, r.Percentage], CommandFormats, tag: r)).ToList();
        // Percentage bars over 100 (not over the largest value), like HeidiSQL.
        foreach (var node in nodes)
        {
            if (node.Tag is CommandStatisticsRow { Percentage: > 0 } row)
            {
                node.Bars = new Dictionary<int, double> { [4] = row.Percentage / 100 };
            }
        }
        CommandList.SetRoots(nodes);
        SetCaption(CommandStatisticsTab, "Command-Statistics", rows.Count);
    }

    public void ShowProcesses(IReadOnlyList<ServerProcess> processes)
    {
        ArgumentNullException.ThrowIfNull(processes);
        var nodes = processes.Select(p => TreeListRows.Create(
            [p.Id, p.User, p.Host, p.Database, p.Command, (long)p.Time.TotalSeconds, p.State, p.Info],
            ProcessFormats,
            tag: p)).ToList();
        TreeListRows.ApplyBars(nodes, [5]);
        ProcessList.SetRoots(nodes);
        ProcessSql.Text = (ProcessList.FocusedNode?.Tag as ServerProcess)?.Info ?? string.Empty;
        SetCaption(ProcessesTab, "Processes", processes.Count);
    }

    private static void AddColumns(TreeListControl list, IReadOnlyList<ListColumn> columns)
    {
        foreach (var column in columns)
        {
            list.Columns.Add(new TreeListColumn(column.Header, column.Width, column.Numeric ? TextAlignment.Right : TextAlignment.Left)
            {
                Visible = column.VisibleByDefault,
            });
        }
    }

    private static void SetCaption(TabItem tab, string name, int count)
    {
        if (tab.Header is StackPanel panel && panel.Children.OfType<TextBlock>().FirstOrDefault() is { } text)
        {
            text.Text = $"{name} ({count.ToString(CultureInfo.CurrentCulture)})";
        }
    }

    private static TabItem Tab(string caption, string icon, Control content)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        if (Controls.Common.Icons.IconFactory.CreateImage(icon) is { } image)
        {
            header.Children.Add(image);
        }
        header.Children.Add(new TextBlock { Text = caption, VerticalAlignment = VerticalAlignment.Center });
        return new TabItem { Header = header, Content = content, Padding = new Thickness(8, 2), Name = "host" + caption.Replace("-", string.Empty, StringComparison.Ordinal) };
    }
}
