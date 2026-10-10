using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Eunomia.App.Search;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Results;
using Eunomia.UI.Controls.Common.Accessibility;
using Eunomia.UI.Controls.Common.Brand;
using Eunomia.UI.Controls.Common.Text;
using Eunomia.UI.Controls.Common.Theming;
using Eunomia.UI.Controls.Common.TreeList;

namespace Eunomia.UI.Shell.Views;

/// <summary>O que o diálogo Find text on server precisa da sessão.</summary>
/// <param name="LoadTables">Tabelas da base com colunas e, se conhecido, o número de linhas.</param>
/// <param name="Count">Corre um <c>SELECT COUNT(*)</c> numa ligação de jobs.</param>
/// <param name="SeeResults">Abre uma query tab com o SQL e corre-o.</param>
public sealed record FindTextServices(
    IDialect Dialect,
    IReadOnlyList<string> Databases,
    string? Database,
    string? FocusedTable,
    Func<string, CancellationToken, Task<IReadOnlyList<(TableDefinition Table, long? Rows)>>> LoadTables,
    Func<string, CancellationToken, Task<long>> Count,
    Action<string> SeeResults);

/// <summary>
/// Find text on server (Ctrl+Shift+F; anexo B §2.4, o tab "Find text" do Table tools): base e tabelas com caixas à
/// esquerda; texto (simples ou predicado SQL), tipos de coluna, match e maiúsculas à direita; em baixo, uma linha por
/// tabela com as linhas encontradas e a relevância. "See results" abre uma query tab com os SELECT das tabelas com
/// resultados e corre-a.
/// </summary>
public sealed class FindTextDialog : Window
{
    private static readonly string[] MatchCaptions = ["Left and right wildcard", "Exact match", "Left wildcard", "Right wildcard", "Regular expression"];
    private static readonly string[] CategoryCaptions = ["All data types", "Integer", "Real", "Text", "Binary", "Temporal (time)", "Spatial (geometry)", "Other"];
    private static ServerTextSearchOptions _last = new(string.Empty, false, null, ServerTextMatch.Contains, false);

    private readonly FindTextServices _services;
    private readonly ComboBox _database = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Name = "findDatabase" }.Named("Database");
    private readonly StackPanel _tables = new() { Spacing = 0 };
    private readonly TextBox _text = new() { Name = "findText", AcceptsReturn = false };
    private readonly RadioButton _simple = new() { Content = "Simple text", GroupName = "findMode", IsChecked = true };
    private readonly RadioButton _sql = new() { Content = "SQL", GroupName = "findMode" };
    private readonly ComboBox _category = new() { ItemsSource = CategoryCaptions, SelectedIndex = 0, Width = 220 };
    private readonly ComboBox _match = new() { ItemsSource = MatchCaptions, SelectedIndex = 0, Width = 220 };
    private readonly CheckBox _caseSensitive = new() { Content = "Case sensitive" };
    private readonly TreeListView _results = new();
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _find = new() { Content = "Find", IsDefault = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center, Name = "btnFind" };
    private readonly Button _seeResults = new() { Content = "See results", IsEnabled = false, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center, Name = "btnSeeResults" };
    private readonly Dictionary<string, TableDefinition> _definitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long?> _rows = new(StringComparer.Ordinal);
    private readonly List<(TableDefinition Table, long Found)> _hits = [];
    private CancellationTokenSource? _run;

    public FindTextDialog(FindTextServices services)
    {
        Icon = BrandAssets.WindowIcon;
        _services = services ?? throw new ArgumentNullException(nameof(services));
        Title = "Find text on server";
        Width = 820;
        Height = 560;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        _database.ItemsSource = services.Databases;
        _database.SelectionChanged += (_, _) => _ = LoadTablesAsync();
        var checkAll = new Button { Content = "All" };
        checkAll.Click += (_, _) => SetAll(true);
        var checkNone = new Button { Content = "None" };
        checkNone.Click += (_, _) => SetAll(false);
        var tableButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 6, 0, 0) };
        tableButtons.Children.Add(checkAll);
        tableButtons.Children.Add(checkNone);
        var left = new DockPanel();
        DockPanel.SetDock(_database, Dock.Top);
        DockPanel.SetDock(tableButtons, Dock.Bottom);
        left.Children.Add(_database);
        left.Children.Add(tableButtons);
        left.Children.Add(Bordered(new ScrollViewer { Content = _tables, Margin = new Thickness(0, 6, 0, 0) }));

        _text.Text = _last.Text;
        _simple.IsChecked = !_last.SqlPredicate;
        _sql.IsChecked = _last.SqlPredicate;
        _category.SelectedIndex = _last.Category is { } c ? (int)c + 1 : 0;
        _match.SelectedIndex = (int)_last.Match;
        _caseSensitive.IsChecked = _last.CaseSensitive;
        _sql.IsCheckedChanged += (_, _) =>
        {
            _match.IsEnabled = _sql.IsChecked != true;
            _caseSensitive.IsEnabled = _sql.IsChecked != true;
            if (_sql.IsChecked == true && string.IsNullOrWhiteSpace(_text.Text))
            {
                _text.Text = "> NOW()";
            }
        };
        var mode = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        mode.Children.Add(_simple);
        mode.Children.Add(_sql);
        var form = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto"), Margin = new Thickness(12, 0, 0, 0) };
        AddRow(form, 0, "Text to find:", _text);
        AddRow(form, 1, string.Empty, mode);
        AddRow(form, 2, "Search in column types:", _category);
        AddRow(form, 3, "Match type:", _match);
        AddRow(form, 4, string.Empty, _caseSensitive);

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("260,*"), Height = 230 };
        Grid.SetColumn(form, 1);
        top.Children.Add(left);
        top.Children.Add(form);

        var list = _results.List;
        list.Columns.Add(new TreeListColumn("Database", 160));
        list.Columns.Add(new TreeListColumn("Table", 220));
        list.Columns.Add(new TreeListColumn("Found rows", 100, TextAlignment.Right));
        list.Columns.Add(new TreeListColumn("Relevance", 100, TextAlignment.Right));
        list.Columns.Add(new TreeListColumn("Duration", 90, TextAlignment.Right));

        _find.Click += (_, _) => _ = FindAsync();
        _seeResults.Click += (_, _) => SeeResults();
        var close = new Button { Content = "Close", IsCancel = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        close.Click += (_, _) => Close();
        var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(_seeResults);
        buttons.Children.Add(_find);
        buttons.Children.Add(close);
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

        _database.SelectedItem = services.Database is { } db && services.Databases.Contains(db) ? db : services.Databases.FirstOrDefault();
        Closed += (_, _) => _run?.Cancel();
    }

    /// <summary>Tabelas da base com a caixa (testes).</summary>
    public IReadOnlyList<CheckBox> TableBoxes => _tables.Children.OfType<CheckBox>().ToList();

    public TextBox FindTextBox => _text;

    public ComboBox MatchBox => _match;

    public CheckBox CaseSensitiveBox => _caseSensitive;

    public TreeListControl Results => _results.List;

    public string? StatusText => _status.Text;

    public bool CanSeeResults => _seeResults.IsEnabled;

    /// <summary>Tabelas da base escolhida (a focada na árvore fica escolhida sozinha; senão, todas).</summary>
    public async Task LoadTablesAsync()
    {
        _tables.Children.Clear();
        _definitions.Clear();
        _rows.Clear();
        if (_database.SelectedItem is not string db)
        {
            return;
        }
        _status.Text = "Loading tables ...";
        try
        {
            var tables = await _services.LoadTables(db, CancellationToken.None);
            if (!Equals(_database.SelectedItem, db))
            {
                return;
            }
            var focused = string.Equals(db, _services.Database, StringComparison.Ordinal) ? _services.FocusedTable : null;
            var onlyFocused = focused is not null && tables.Any(t => t.Table.Ref.Name == focused);
            foreach (var (table, rows) in tables)
            {
                _definitions[table.Ref.Name] = table;
                _rows[table.Ref.Name] = rows;
                var box = new CheckBox { Content = LiteralText.Of(table.Ref.Name), Tag = table.Ref.Name, IsChecked = !onlyFocused || table.Ref.Name == focused, MinHeight = 0, Padding = new Thickness(4, 1) };
                _tables.Children.Add(box.Named(table.Ref.Name));
            }
            _status.Text = string.Create(CultureInfo.CurrentCulture, $"{tables.Count} table(s) in {db}.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _status.Text = ex.Message;
        }
    }

    private void SetAll(bool value)
    {
        foreach (var box in TableBoxes)
        {
            box.IsChecked = value;
        }
    }

    private ServerTextSearchOptions ReadOptions() => new(
        _text.Text ?? string.Empty,
        _sql.IsChecked == true,
        _category.SelectedIndex > 0 ? (TypeCategory)(_category.SelectedIndex - 1) : null,
        (ServerTextMatch)Math.Max(0, _match.SelectedIndex),
        _caseSensitive.IsChecked == true);

    /// <summary>Find: uma contagem por tabela escolhida, por ordem; Close ou outro Find cancelam a pesquisa em curso.</summary>
    public async Task FindAsync()
    {
        _run?.Cancel();
        var cts = new CancellationTokenSource();
        _run = cts;
        var options = ReadOptions();
        _last = options;
        _hits.Clear();
        _seeResults.IsEnabled = false;
        var nodes = new List<TreeListNode>();
        _results.List.SetRoots(nodes);
        if (string.IsNullOrEmpty(options.Text))
        {
            _status.Text = "Please enter a text to find.";
            return;
        }
        // O predicado SQL vai para um WHERE numa ligação que aceita várias instruções: um ";" correria o resto.
        if (options.SqlPredicate && Eunomia.Engine.Sql.Splitting.SqlSplitter.Split("SELECT 1 FROM t WHERE c " + options.Text).Count > 1)
        {
            _status.Text = "The SQL predicate must be a single condition, without \";\".";
            return;
        }
        var db = _database.SelectedItem as string ?? string.Empty;
        var chosen = TableBoxes.Where(b => b.IsChecked == true).Select(b => (string)b.Tag!).ToList();
        long total = 0;
        for (var i = 0; i < chosen.Count && !cts.IsCancellationRequested; i++)
        {
            var name = chosen[i];
            _status.Text = string.Create(CultureInfo.CurrentCulture, $"Searching table {name} ({i + 1} of {chosen.Count}) ...");
            var table = _definitions[name];
            var sql = ServerTextSearch.CountQuery(_services.Dialect, table, options);
            string found;
            string relevance = string.Empty;
            string duration = string.Empty;
            if (sql is null)
            {
                found = "(no matching columns)";
            }
            else
            {
                var watch = Stopwatch.StartNew();
                try
                {
                    var count = await _services.Count(sql, cts.Token);
                    var hit = new ServerTextSearchHit(table.Ref, count, _rows.GetValueOrDefault(name));
                    found = count.ToString("N0", CultureInfo.CurrentCulture);
                    relevance = hit.Relevance is { } r ? r.ToString("0.0", CultureInfo.CurrentCulture) + " %" : string.Empty;
                    if (count > 0)
                    {
                        _hits.Add((table, count));
                        total += count;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    found = ex.Message;
                }
                duration = watch.Elapsed.TotalSeconds.ToString("0.000", CultureInfo.CurrentCulture) + " s";
            }
            nodes.Add(new TreeListNode(db, name, found, relevance, duration) { Bold = _hits.Count > 0 && ReferenceEquals(_hits[^1].Table, table) });
            _results.List.SetRoots([.. nodes]);
        }
        if (ReferenceEquals(_run, cts))
        {
            _status.Text = cts.IsCancellationRequested
                ? "Search cancelled."
                : string.Create(CultureInfo.CurrentCulture, $"{total:N0} matching row(s) in {_hits.Count} of {chosen.Count} table(s).");
            _seeResults.IsEnabled = _hits.Count > 0;
        }
    }

    /// <summary>See results: os SELECT das tabelas com resultados numa query tab nova, a correr.</summary>
    public void SeeResults()
    {
        var options = _last;
        var sql = string.Join(Environment.NewLine, _hits.Select(h => ServerTextSearch.SelectQuery(_services.Dialect, h.Table, options)).Where(s => s is not null));
        if (sql.Length > 0)
        {
            _services.SeeResults(sql);
            Close();
        }
    }

    private static void AddRow(Grid grid, int row, string label, Control control)
    {
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 10, 4) };
        if (label.Length > 0)
        {
            control.Named(label);
        }
        Grid.SetRow(text, row);
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        control.Margin = new Thickness(0, 4);
        if (control is not TextBox)
        {
            control.HorizontalAlignment = HorizontalAlignment.Left;
        }
        grid.Children.Add(text);
        grid.Children.Add(control);
    }

    private static Border Bordered(Control child, Thickness margin = default) =>
        Themed.Bind(new Border { Child = child, BorderThickness = new Thickness(1), Margin = margin }, Border.BorderBrushProperty, "border");
}
