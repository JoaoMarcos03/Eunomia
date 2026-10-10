using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Eunomia.App.Objects;
using Eunomia.Drivers.Abstractions;
using Eunomia.UI.Controls.Common.Accessibility;
using Eunomia.UI.Controls.Common.Text;
using Eunomia.UI.Controls.Common.Theming;

namespace Eunomia.UI.Features.TableEditor;

/// <summary>Listas dos drop-downs do table editor: collations, engines e tabelas da base (referências das FKs).</summary>
public sealed record TableEditorLists(IReadOnlyList<string> Collations, IReadOnlyList<string> Engines, IReadOnlyList<string> Tables);

/// <summary>
/// Table editor (anexo C §5; FAM-E01): em cima, as tabs Basic, Options, Indexes (n), Foreign keys (n), Check
/// constraints (n), CREATE code e, nas tabelas que já existem, ALTER code (Partitions fica para depois); ao meio, a grelha das colunas com
/// as 15 colunas do HeidiSQL (#, Name, Datatype, Length/Set, Unsigned, Allow NULL, Zerofill, Default, Comment,
/// Collation, Expression, Virtuality, SRID, Invisible, Compressed) e Add/Remove/Up/Down; em baixo, Discard e Save.
/// Edita o <see cref="TableDesign"/> diretamente; o código vem de quem cria a vista.
/// </summary>
public sealed class TableEditorView : DockPanel
{
    private static readonly string[] Headers = ["#", "Name", "Datatype", "Length/Set", "Unsigned", "Allow NULL", "Zerofill", "Default", "Comment", "Collation", "Expression", "Virtuality", "SRID", "Invisible", "Compressed"];
    private static readonly double[] Widths = [30, 140, 120, 90, 66, 78, 64, 170, 160, 160, 130, 90, 50, 66, 84];
    private static readonly string[] Actions = ["RESTRICT", "CASCADE", "SET NULL", "NO ACTION", "SET DEFAULT"];

    private readonly TableEditorLists _lists;
    private readonly Func<TableDesign, string> _createCode;
    private readonly Func<TableDesign, string>? _alterCode;
    private readonly StackPanel _columnRows = new();
    private readonly StackPanel _indexRows = new() { Spacing = 2 };
    private readonly StackPanel _fkRows = new() { Spacing = 2 };
    private readonly StackPanel _checkRows = new() { Spacing = 2 };
    private readonly TabItem _indexesTab = new();
    private readonly TabItem _fksTab = new();
    private readonly TabItem _checksTab = new();
    private readonly TextBox _createCodeBox = Code().Named("CREATE code");
    private readonly TextBox _alterCodeBox = Code().Named("ALTER code");
    private readonly Button _save = new() { Content = "Save", MinWidth = 75, HorizontalContentAlignment = HorizontalAlignment.Center };
    private readonly Button _discard = new() { Content = "Discard", MinWidth = 75, HorizontalContentAlignment = HorizontalAlignment.Center };
    private int _selected = -1;

    public TableEditorView(TableDesign design, TableEditorLists lists, Func<TableDesign, string> createCode, Func<TableDesign, string>? alterCode)
    {
        ArgumentNullException.ThrowIfNull(design);
        Design = design;
        _lists = lists ?? throw new ArgumentNullException(nameof(lists));
        _createCode = createCode ?? throw new ArgumentNullException(nameof(createCode));
        _alterCode = alterCode;

        var tabs = new TabControl { Height = 190, Padding = new Thickness(0, 4, 0, 0) }.Named("Table properties");
        tabs.Styles.Add(new Avalonia.Styling.Style(x => x.OfType<TabItem>())
        {
            Setters =
            {
                new Avalonia.Styling.Setter(TemplatedControl.FontSizeProperty, 12.0),
                new Avalonia.Styling.Setter(MinHeightProperty, 24.0),
                new Avalonia.Styling.Setter(TemplatedControl.PaddingProperty, new Thickness(8, 2)),
            },
        });
        tabs.Items.Add(new TabItem { Header = "Basic", Content = BuildBasic() });
        tabs.Items.Add(new TabItem { Header = "Options", Content = BuildOptions() });
        _indexesTab.Content = ListPage(_indexRows, AddIndex, ["Name", "Type", "Columns", "Algorithm"], () => { Design.Indexes.Clear(); Changed(); });
        _fksTab.Content = ListPage(_fkRows, AddForeignKey, ["Key name", "Columns", "Reference table", "Foreign columns", "On UPDATE", "On DELETE"], () => { Design.ForeignKeys.Clear(); Changed(); });
        _checksTab.Content = ListPage(_checkRows, AddCheck, ["Name", "Check clause"], () => { Design.Checks.Clear(); Changed(); });
        tabs.Items.Add(_indexesTab);
        tabs.Items.Add(_fksTab);
        tabs.Items.Add(_checksTab);
        tabs.Items.Add(new TabItem { Header = "CREATE code", Content = _createCodeBox });
        if (alterCode is not null)
        {
            tabs.Items.Add(new TabItem { Header = "ALTER code", Content = _alterCodeBox });
        }
        tabs.SelectionChanged += (_, e) =>
        {
            e.Handled = true;
            UpdateCode();
        };

        var columnBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 6, 0, 4) };
        columnBar.Children.Add(new TextBlock { Text = "Columns:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        columnBar.Children.Add(ToolButton("Add", AddColumn));
        columnBar.Children.Add(ToolButton("Remove", RemoveColumn));
        columnBar.Children.Add(ToolButton("Up", () => MoveColumn(-1)));
        columnBar.Children.Add(ToolButton("Down", () => MoveColumn(1)));

        var header = new Grid { ColumnDefinitions = ColumnsLayout() };
        for (var i = 0; i < Headers.Length; i++)
        {
            var text = new TextBlock { Text = Headers[i], FontWeight = FontWeight.SemiBold, Margin = new Thickness(2) };
            Grid.SetColumn(text, i);
            header.Children.Add(text);
        }
        var grid = new StackPanel();
        grid.Children.Add(header);
        grid.Children.Add(_columnRows);
        var columns = new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        bottom.Children.Add(_discard);
        bottom.Children.Add(_save);
        _save.Click += (_, _) => SaveRequested?.Invoke(this, EventArgs.Empty);
        _discard.Click += (_, _) => DiscardRequested?.Invoke(this, EventArgs.Empty);

        Margin = new Thickness(4);
        SetDock(tabs, Dock.Top);
        SetDock(columnBar, Dock.Top);
        SetDock(bottom, Dock.Bottom);
        Children.Add(tabs);
        Children.Add(columnBar);
        Children.Add(bottom);
        Children.Add(columns);
        RebuildAll();
        _savedCode = Safe(() => _createCode(Design));
    }

    public TableDesign Design { get; }

    /// <summary>
    /// O desenho mudou desde que a vista abriu (ou desde <see cref="MarkSaved"/>): compara o CREATE gerado com o de
    /// então. Assim, eventos que os controlos levantam ao aparecer (seleção das listas, texto) não contam como
    /// alterações, e desfazer uma alteração à mão volta a "sem alterações".
    /// </summary>
    public bool IsModified => !string.Equals(Safe(() => _createCode(Design)), _savedCode, StringComparison.Ordinal);

    private string _savedCode = string.Empty;

    public event EventHandler? SaveRequested;

    public event EventHandler? DiscardRequested;

    /// <summary>Alguma coisa mudou no desenho (contagens, código, botões).</summary>
    public event EventHandler? DesignChanged;

    /// <summary>Linha de colunas escolhida (Add insere a seguir; Remove, Up e Down atuam nela).</summary>
    public int SelectedColumn
    {
        get => _selected;
        set
        {
            _selected = Math.Clamp(value, -1, Design.Columns.Count - 1);
            HighlightSelection();
        }
    }

    public string CreateCodeText => _createCodeBox.Text ?? string.Empty;

    /// <summary>Tab "Indexes (n)" (testes).</summary>
    public TabItem IndexesTab => _indexesTab;

    /// <summary>Volta a mostrar o desenho depois de o mudar por código.</summary>
    public void Refresh()
    {
        RebuildAll();
    }

    public string AlterCodeText => _alterCodeBox.Text ?? string.Empty;

    /// <summary>Depois de gravar: sem alterações por gravar.</summary>
    public void MarkSaved()
    {
        _savedCode = Safe(() => _createCode(Design));
        UpdateCode();
    }

    private void Changed()
    {
        RebuildLists();
        UpdateCode();
        DesignChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Alteração num campo de texto: não reconstrói as linhas (o foco fica no campo).</summary>
    private void Edited()
    {
        UpdateCounts();
        UpdateCode();
        DesignChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateCode()
    {
        _createCodeBox.Text = Safe(() => _createCode(Design));
        if (_alterCode is { } alter)
        {
            _alterCodeBox.Text = Safe(() => alter(Design));
        }
    }

    private static string Safe(Func<string> code)
    {
        try
        {
            return code();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return "-- " + ex.Message;
        }
    }

    private static TextBox Code() => new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        FontFamily = UiFonts.Monospace,
        VerticalContentAlignment = VerticalAlignment.Top,
    };

    private static Button ToolButton(string text, Action click)
    {
        var button = new Button { Content = text, Padding = new Thickness(8, 2), MinHeight = 0 };
        button.Click += (_, _) => click();
        return button;
    }

    private Control BuildBasic()
    {
        var name = new TextBox { Text = Design.Name, PlaceholderText = "Enter table name", Name = "tableName" };
        name.TextChanged += (_, _) =>
        {
            Design.Name = name.Text ?? string.Empty;
            Edited();
        };
        var comment = new TextBox { Text = Design.Comment, AcceptsReturn = true, Height = 60, TextWrapping = TextWrapping.Wrap };
        comment.TextChanged += (_, _) =>
        {
            Design.Comment = comment.Text ?? string.Empty;
            Edited();
        };
        var grid = Form();
        AddField(grid, 0, "Name:", name);
        AddField(grid, 1, "Comment:", comment);
        return grid;
    }

    private Control BuildOptions()
    {
        var grid = Form();
        grid.ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,*");
        var row = 0;
        var column = 0;
        void Field(string label, Control control)
        {
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(column == 0 ? 0 : 12, 2, 6, 2) };
            control.Named(label);
            Grid.SetRow(text, row);
            Grid.SetColumn(text, column);
            Grid.SetRow(control, row);
            Grid.SetColumn(control, column + 1);
            control.Margin = new Thickness(0, 2);
            grid.Children.Add(text);
            grid.Children.Add(control);
            column += 2;
            if (column == 4)
            {
                column = 0;
                row++;
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            }
        }
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Field("Auto increment:", Text(Design.AutoIncrement, v => Design.AutoIncrement = v));
        var collation = Choice(_lists.Collations, Design.Collation, v => Design.Collation = v, allowEmpty: true).Named("Default collation");
        var convert = new CheckBox { Content = "Convert data", IsChecked = Design.ConvertData };
        convert.IsCheckedChanged += (_, _) =>
        {
            Design.ConvertData = convert.IsChecked == true;
            Edited();
        };
        var collationRow = new DockPanel();
        DockPanel.SetDock(convert, Dock.Right);
        collationRow.Children.Add(convert);
        collationRow.Children.Add(collation);
        Field("Default collation:", collationRow);
        Field("Engine:", Choice(["", .. _lists.Engines], Design.Engine, v => Design.Engine = v, allowEmpty: true));
        Field("Row format:", Choice(["", "DEFAULT", "DYNAMIC", "FIXED", "COMPRESSED", "REDUNDANT", "COMPACT"], Design.RowFormat.ToUpperInvariant(), v => Design.RowFormat = v, allowEmpty: true));
        Field("Checksum for rows:", Choice(["", "0", "1"], Design.Checksum, v => Design.Checksum = v, allowEmpty: true));
        Field("Average row length:", Text(Design.AvgRowLength, v => Design.AvgRowLength = v));
        Field("Maximum row count:", Text(Design.MaxRows, v => Design.MaxRows = v));
        return new ScrollViewer { Content = grid };
    }

    private static Grid Form() => new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(8) };

    private static void AddField(Grid grid, int row, string label, Control control)
    {
        while (grid.RowDefinitions.Count <= row)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 8, 2) };
        control.Named(label);
        Grid.SetRow(text, row);
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        control.Margin = new Thickness(0, 2);
        grid.Children.Add(text);
        grid.Children.Add(control);
    }

    private TextBox Text(string value, Action<string> set, double width = double.NaN)
    {
        var box = new TextBox { Text = value, Width = width, MinHeight = 0, Padding = new Thickness(2, 1) };
        box.TextChanged += (_, _) =>
        {
            set(box.Text ?? string.Empty);
            Edited();
        };
        return box;
    }

    private ComboBox Choice(IReadOnlyList<string> items, string value, Action<string> set, bool allowEmpty = false, bool editable = false)
    {
        var list = items.ToList();
        if (value.Length > 0 && !list.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(value);
        }
        if (allowEmpty && !list.Contains(string.Empty))
        {
            list.Insert(0, string.Empty);
        }
        var combo = new ComboBox
        {
            ItemsSource = list,
            SelectedItem = list.FirstOrDefault(i => string.Equals(i, value, StringComparison.OrdinalIgnoreCase)),
            IsEditable = editable,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinHeight = 0,
            MaxDropDownHeight = 300,
        };
        if (editable)
        {
            combo.Text = value;
            combo.PropertyChanged += (_, e) =>
            {
                if (e.Property == ComboBox.TextProperty)
                {
                    set(combo.Text ?? string.Empty);
                    Edited();
                }
            };
        }
        combo.SelectionChanged += (_, e) =>
        {
            e.Handled = true;
            if (combo.SelectedItem is string selected)
            {
                set(selected);
                Edited();
            }
        };
        return combo;
    }

    private Control ListPage(StackPanel rows, Action add, string[] headers, Action clear)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 0, 0, 4) };
        bar.Children.Add(ToolButton("Add", add));
        bar.Children.Add(ToolButton("Clear", clear));
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var h in headers)
        {
            header.Children.Add(new TextBlock { Text = h, Width = 140, FontWeight = FontWeight.SemiBold });
        }
        var page = new DockPanel { Margin = new Thickness(4) };
        DockPanel.SetDock(bar, Dock.Top);
        DockPanel.SetDock(header, Dock.Top);
        page.Children.Add(bar);
        page.Children.Add(header);
        page.Children.Add(new ScrollViewer { Content = rows, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });
        return page;
    }

    private static ColumnDefinitions ColumnsLayout() =>
        new(string.Join(",", Widths.Select(w => w.ToString(CultureInfo.InvariantCulture))));

    private void RebuildAll()
    {
        RebuildColumns();
        RebuildLists();
        UpdateCode();
    }

    private void UpdateCounts()
    {
        _indexesTab.Header = $"Indexes ({Design.Indexes.Count})";
        _fksTab.Header = $"Foreign keys ({Design.ForeignKeys.Count})";
        _checksTab.Header = $"Check constraints ({Design.Checks.Count})";
    }

    private HashSet<string> PrimaryColumns() =>
        Design.Indexes.Where(i => i.Kind == IndexKind.Primary).SelectMany(i => TableDesign.SplitList(i.Columns)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>As linhas das colunas, uma grelha por coluna da tabela.</summary>
    private void RebuildColumns()
    {
        _columnRows.Children.Clear();
        var primary = PrimaryColumns();
        for (var i = 0; i < Design.Columns.Count; i++)
        {
            _columnRows.Children.Add(ColumnRow(i, Design.Columns[i], primary.Contains(Design.Columns[i].Name)));
        }
        HighlightSelection();
    }

    private void HighlightSelection()
    {
        for (var i = 0; i < _columnRows.Children.Count; i++)
        {
            ((Grid)_columnRows.Children[i]).Background = i == _selected ? new SolidColorBrush(Color.FromArgb(0x40, 0x33, 0x99, 0xFF)) : null;
        }
    }

    private Grid ColumnRow(int index, ColumnDesign c, bool isPrimary)
    {
        var row = new Grid { ColumnDefinitions = ColumnsLayout() };
        row.GotFocus += (_, _) =>
        {
            _selected = Design.Columns.IndexOf(c);
            HighlightSelection();
        };
        row.PointerPressed += (_, _) =>
        {
            _selected = Design.Columns.IndexOf(c);
            HighlightSelection();
        };
        // Menu da coluna como no HeidiSQL: Create new index ▸ e Add to index ▸ (em vez de arrastar para os índices).
        var menu = new ContextMenu();
        menu.Opening += (_, _) => menu.ItemsSource = ColumnMenuItems(c);
        row.ContextMenu = menu;
        row.Background = Avalonia.Media.Brushes.Transparent;
        void Put(int column, Control control)
        {
            control.Named(Headers[column]);
            control.Margin = new Thickness(1);
            control.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control, column);
            row.Children.Add(control);
        }
        CheckBox Check(bool value, bool enabled, Action<bool> set)
        {
            var box = new CheckBox { IsChecked = value, IsEnabled = enabled, HorizontalAlignment = HorizontalAlignment.Center, MinHeight = 0 };
            box.IsCheckedChanged += (_, _) =>
            {
                set(box.IsChecked == true);
                Edited();
            };
            return box;
        }
        var numeric = TableDesign.IsNumeric(c.DataType);
        Put(0, new TextBlock { Text = (index + 1).ToString(CultureInfo.InvariantCulture), Margin = new Thickness(4, 0) });
        Put(1, Text(c.Name, v => c.Name = v));
        var types = new List<object>();
        foreach (var (category, names) in TableDesign.DataTypes)
        {
            types.Add(new ComboBoxItem { Content = category, IsEnabled = false, FontWeight = FontWeight.SemiBold });
            types.AddRange(names.Select(n => (object)new ComboBoxItem { Content = n, Padding = new Thickness(16, 2, 4, 2) }));
        }
        var typeBox = new ComboBox { ItemsSource = types, MaxDropDownHeight = 250, HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 0 };
        typeBox.SelectedItem = types.OfType<ComboBoxItem>().FirstOrDefault(t => t.IsEnabled && string.Equals((string)t.Content!, c.DataType, StringComparison.OrdinalIgnoreCase));
        typeBox.SelectionChanged += (_, e) =>
        {
            e.Handled = true;
            if (typeBox.SelectedItem is ComboBoxItem { Content: string type } && type != c.DataType)
            {
                c.DataType = type;
                if (!TableDesign.IsNumeric(type))
                {
                    c.Unsigned = false;
                    c.Zerofill = false;
                }
                Changed();
            }
        };
        Put(2, typeBox);
        Put(3, Text(c.LengthSet, v => c.LengthSet = v));
        Put(4, Check(c.Unsigned, numeric, v => c.Unsigned = v));
        Put(5, Check(c.AllowNull && !isPrimary, !isPrimary, v => c.AllowNull = v));
        Put(6, Check(c.Zerofill, numeric, v => c.Zerofill = v));
        Put(7, DefaultButton(c));
        Put(8, Text(c.Comment, v => c.Comment = v));
        Put(9, Choice(_lists.Collations, c.Collation, v => c.Collation = v, allowEmpty: true));
        Put(10, Text(c.Expression, v => c.Expression = v));
        Put(11, Choice(["", "VIRTUAL", "STORED"], c.Virtuality, v => c.Virtuality = v, allowEmpty: true));
        Put(12, new TextBox { IsEnabled = false, MinHeight = 0 });
        Put(13, Check(c.Invisible, true, v => c.Invisible = v));
        Put(14, Check(false, false, _ => { }));
        return row;
    }

    /// <summary>Célula Default: o texto atual e, ao clicar, o painel do editor de defaults (anexo C §5.2).</summary>
    private Button DefaultButton(ColumnDesign c)
    {
        // TextBlock: num Button, o "_" de AUTO_INCREMENT seria a tecla de acesso.
        var button = new Button { Content = new TextBlock { Text = c.DefaultCaption }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, MinHeight = 0, Padding = new Thickness(4, 1) }.Named("Default");
        var group = "default" + Guid.NewGuid().ToString("N");
        var none = new RadioButton { Content = "No default value", GroupName = group, IsChecked = c.DefaultKind == DefaultKind.None };
        var custom = new RadioButton { Content = "Custom text:", GroupName = group, IsChecked = c.DefaultKind == DefaultKind.Text };
        var text = new TextBox { Text = c.DefaultKind == DefaultKind.Text ? c.DefaultText : string.Empty, MinWidth = 200 };
        var nullValue = new RadioButton { Content = "NULL", GroupName = group, IsChecked = c.DefaultKind == DefaultKind.Null };
        var expression = new RadioButton { Content = "Expression:", GroupName = group, IsChecked = c.DefaultKind == DefaultKind.Expression };
        var expressionText = new ComboBox
        {
            ItemsSource = new[] { "CURRENT_TIMESTAMP", "CURRENT_TIMESTAMP()", "NOW()", "UUID()", "CURDATE()" },
            IsEditable = true,
            Text = c.DefaultKind == DefaultKind.Expression ? c.DefaultText : string.Empty,
            MinWidth = 200,
        };
        var onUpdate = new ComboBox { ItemsSource = new[] { string.Empty, "CURRENT_TIMESTAMP" }, IsEditable = true, Text = c.OnUpdate, MinWidth = 200 };
        var auto = new RadioButton { Content = "AUTO_INCREMENT", GroupName = group, IsChecked = c.DefaultKind == DefaultKind.AutoIncrement, IsEnabled = TableDesign.IsInteger(c.DataType) };
        var ok = new Button { Content = "OK", MinWidth = 70 };
        var cancel = new Button { Content = "Cancel", MinWidth = 70 };
        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(6) };
        panel.Children.Add(none);
        panel.Children.Add(custom);
        panel.Children.Add(text);
        panel.Children.Add(nullValue);
        panel.Children.Add(expression);
        panel.Children.Add(expressionText);
        panel.Children.Add(new TextBlock { Text = "On update:" });
        panel.Children.Add(onUpdate);
        panel.Children.Add(auto);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        var flyout = new Flyout { Content = panel, Placement = PlacementMode.Bottom };
        button.Flyout = flyout;
        text.TextChanged += (_, _) => custom.IsChecked = true;
        ok.Click += (_, _) =>
        {
            (c.DefaultKind, c.DefaultText) = none.IsChecked == true ? (DefaultKind.None, string.Empty)
                : custom.IsChecked == true ? (DefaultKind.Text, text.Text ?? string.Empty)
                : nullValue.IsChecked == true ? (DefaultKind.Null, string.Empty)
                : expression.IsChecked == true ? (DefaultKind.Expression, expressionText.Text ?? string.Empty)
                : (DefaultKind.AutoIncrement, string.Empty);
            c.OnUpdate = onUpdate.Text ?? string.Empty;
            if (c.DefaultKind == DefaultKind.Null)
            {
                c.AllowNull = true;
            }
            flyout.Hide();
            Changed();
        };
        cancel.Click += (_, _) => flyout.Hide();
        return button;
    }

    private void AddColumn()
    {
        // Add a seguir à focada, copiando tipo, tamanho, unsigned e NULL (anexo C §5.2).
        var after = _selected >= 0 && _selected < Design.Columns.Count ? Design.Columns[_selected] : null;
        var column = new ColumnDesign
        {
            Name = "Column " + (Design.Columns.Count + 1).ToString(CultureInfo.InvariantCulture),
            DataType = after?.DataType ?? "INT",
            LengthSet = after?.LengthSet ?? string.Empty,
            Unsigned = after?.Unsigned ?? false,
            AllowNull = after?.AllowNull ?? true,
            DefaultKind = after?.DefaultKind == DefaultKind.AutoIncrement ? DefaultKind.Text : DefaultKind.None,
            DefaultText = after?.DefaultKind == DefaultKind.AutoIncrement ? "0" : string.Empty,
        };
        var at = _selected >= 0 ? _selected + 1 : Design.Columns.Count;
        Design.Columns.Insert(at, column);
        _selected = at;
        RebuildColumns();
        Changed();
    }

    private void RemoveColumn()
    {
        if (_selected < 0 || _selected >= Design.Columns.Count)
        {
            return;
        }
        Design.Columns.RemoveAt(_selected);
        _selected = Math.Min(_selected, Design.Columns.Count - 1);
        RebuildColumns();
        Changed();
    }

    private void MoveColumn(int delta)
    {
        var to = _selected + delta;
        if (_selected < 0 || to < 0 || to >= Design.Columns.Count)
        {
            return;
        }
        (Design.Columns[_selected], Design.Columns[to]) = (Design.Columns[to], Design.Columns[_selected]);
        _selected = to;
        RebuildColumns();
        Changed();
    }

    private static readonly (IndexKind Kind, string Caption)[] IndexKinds =
        [(IndexKind.Primary, "PRIMARY"), (IndexKind.Key, "KEY"), (IndexKind.Unique, "UNIQUE"), (IndexKind.FullText, "FULLTEXT"), (IndexKind.Spatial, "SPATIAL")];

    private List<object> ColumnMenuItems(ColumnDesign column)
    {
        static MenuItem Item(string header, Action click, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => click();
            return item;
        }
        var create = new MenuItem
        {
            Header = "Create new index",
            ItemsSource = IndexKinds.Select(k => (object)Item(k.Caption, () => CreateIndex(column, k.Kind))).ToList(),
        };
        var add = new MenuItem
        {
            Header = "Add to index",
            IsEnabled = Design.Indexes.Count > 0,
            ItemsSource = Design.Indexes
                .Select(ix => (object)Item($"{LiteralText.Escaped(ix.Name)} ({IndexKinds.First(k => k.Kind == ix.Kind).Caption})", () => AddToIndex(ix, column), !IndexHasColumn(ix, column)))
                .ToList(),
        };
        return [Item("Add column", AddColumn), Item("Remove column", RemoveColumn), new Separator(), create, add];
    }

    /// <summary>Create new index ▸ tipo: um índice novo só com esta coluna (PRIMARY junta-se à chave primária, se já houver).</summary>
    internal void CreateIndex(ColumnDesign column, IndexKind kind)
    {
        if (kind == IndexKind.Primary && Design.Indexes.FirstOrDefault(i => i.Kind == IndexKind.Primary) is { } primary)
        {
            AddToIndex(primary, column);
            return;
        }
        var name = kind == IndexKind.Primary ? "PRIMARY" : column.Name;
        var candidate = name;
        for (var n = 2; Design.Indexes.Any(i => string.Equals(i.Name, candidate, StringComparison.OrdinalIgnoreCase)); n++)
        {
            candidate = name + "_" + n.ToString(CultureInfo.InvariantCulture);
        }
        Design.Indexes.Add(new IndexDesign { Name = candidate, Kind = kind, Columns = column.Name });
        Changed();
    }

    /// <summary>Add to index ▸ nome: junta a coluna ao fim das colunas do índice.</summary>
    internal void AddToIndex(IndexDesign index, ColumnDesign column)
    {
        if (IndexHasColumn(index, column))
        {
            return;
        }
        index.Columns = string.IsNullOrWhiteSpace(index.Columns) ? TableDesign.FormatName(column.Name) : index.Columns.TrimEnd() + ", " + TableDesign.FormatName(column.Name);
        Changed();
    }

    private static bool IndexHasColumn(IndexDesign index, ColumnDesign column) =>
        index.Columns.Split(',').Select(p => p.Trim()).Select(p => p.IndexOf('(', StringComparison.Ordinal) is var i and > 0 ? p[..i].Trim() : p)
            .Any(p => string.Equals(p.Trim('`'), column.Name, StringComparison.OrdinalIgnoreCase));

    private void AddIndex()
    {
        var focused = _selected >= 0 && _selected < Design.Columns.Count ? Design.Columns[_selected].Name : string.Empty;
        Design.Indexes.Add(new IndexDesign { Name = "Index " + (Design.Indexes.Count + 1).ToString(CultureInfo.InvariantCulture), Columns = focused });
        Changed();
    }

    private void AddForeignKey()
    {
        Design.ForeignKeys.Add(new ForeignKeyDesign { Name = "FK" + (Design.ForeignKeys.Count + 1).ToString(CultureInfo.InvariantCulture) });
        Changed();
    }

    private void AddCheck()
    {
        Design.Checks.Add(new CheckDesign { Name = "CC" + (Design.Checks.Count + 1).ToString(CultureInfo.InvariantCulture) });
        Changed();
    }

    /// <summary>Índices, chaves estrangeiras e checks: uma linha por item, com o botão de remover.</summary>
    private void RebuildLists()
    {
        UpdateCounts();
        _indexRows.Children.Clear();
        foreach (var index in Design.Indexes)
        {
            var row = Row();
            var kinds = new Dictionary<string, IndexKind> { ["PRIMARY"] = IndexKind.Primary, ["KEY"] = IndexKind.Key, ["UNIQUE"] = IndexKind.Unique, ["FULLTEXT"] = IndexKind.FullText, ["SPATIAL"] = IndexKind.Spatial };
            row.Children.Add(Sized(Text(index.Name, v => index.Name = v).Named("Index name")));
            row.Children.Add(Sized(Choice([.. kinds.Keys], kinds.First(k => k.Value == index.Kind).Key, v =>
            {
                index.Kind = kinds[v];
                if (index.Kind == IndexKind.Primary)
                {
                    index.Name = "PRIMARY";
                }
                Dispatch(RebuildColumns);
            }).Named("Index type")));
            row.Children.Add(Sized(Text(index.Columns, v =>
            {
                index.Columns = v;
                Dispatch(RebuildColumns);
            }).Named("Index columns")));
            row.Children.Add(Sized(Choice(["", "BTREE", "HASH", "RTREE"], index.Algorithm, v => index.Algorithm = v, allowEmpty: true).Named("Index algorithm")));
            row.Children.Add(RemoveButton(() => Design.Indexes.Remove(index)));
            _indexRows.Children.Add(row);
        }
        _fkRows.Children.Clear();
        foreach (var fk in Design.ForeignKeys)
        {
            var row = Row();
            row.Children.Add(Sized(Text(fk.Name, v => fk.Name = v).Named("Key name")));
            row.Children.Add(Sized(Text(fk.Columns, v => fk.Columns = v).Named("Columns")));
            row.Children.Add(Sized(Choice(_lists.Tables, fk.ReferenceTable, v => fk.ReferenceTable = v, editable: true).Named("Reference table")));
            row.Children.Add(Sized(Text(fk.ForeignColumns, v => fk.ForeignColumns = v).Named("Foreign columns")));
            row.Children.Add(Sized(Choice(Actions, ActionText(fk.OnUpdate), v => fk.OnUpdate = ActionOf(v)).Named("On UPDATE")));
            row.Children.Add(Sized(Choice(Actions, ActionText(fk.OnDelete), v => fk.OnDelete = ActionOf(v)).Named("On DELETE")));
            row.Children.Add(RemoveButton(() => Design.ForeignKeys.Remove(fk)));
            _fkRows.Children.Add(row);
        }
        _checkRows.Children.Clear();
        foreach (var check in Design.Checks)
        {
            var row = Row();
            row.Children.Add(Sized(Text(check.Name, v => check.Name = v).Named("Check name")));
            row.Children.Add(Sized(Text(check.Clause, v => check.Clause = v).Named("Check clause"), 420));
            row.Children.Add(RemoveButton(() => Design.Checks.Remove(check)));
            _checkRows.Children.Add(row);
        }
    }

    private static void Dispatch(Action action) => Avalonia.Threading.Dispatcher.UIThread.Post(action);

    private static StackPanel Row() => new() { Orientation = Orientation.Horizontal, Spacing = 2 };

    private static Control Sized(Control control, double width = 140)
    {
        control.Width = width;
        return control;
    }

    private Button RemoveButton(Func<bool> remove)
    {
        var button = new Button { Content = "×", Padding = new Thickness(6, 0), MinHeight = 0 }.Named("Remove");
        button.Click += (_, _) =>
        {
            remove();
            Changed();
        };
        return button;
    }

    private static string ActionText(ForeignKeyAction action) => Actions[(int)action];

    private static ForeignKeyAction ActionOf(string text) => (ForeignKeyAction)Math.Max(0, Array.IndexOf(Actions, text));
}
