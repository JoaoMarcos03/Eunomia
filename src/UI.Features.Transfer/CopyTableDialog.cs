using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Eunomia.App.Objects;
using Eunomia.Drivers.Abstractions;
using Eunomia.UI.Controls.Common.Accessibility;
using Eunomia.UI.Controls.Common.Brand;
using Eunomia.UI.Controls.Common.Text;
using Eunomia.UI.Controls.Common.Theming;

namespace Eunomia.UI.Features.Transfer;

/// <summary>O que o diálogo Copy table precisa da sessão.</summary>
/// <param name="Rows">Estimativa das linhas da origem, se conhecida (só para a legenda do "Data").</param>
/// <param name="TargetExists">A tabela já existe na base (o OK recusa o nome).</param>
public sealed record CopyTableServices(
    TableDefinition Source,
    long? Rows,
    IReadOnlyList<string> Databases,
    Func<string, string, CancellationToken, Task<bool>> TargetExists);

/// <summary>
/// Copy table (Create new ▸ Table copy; anexo B §5.1): base e nome do destino (<c>&lt;nome&gt;_copy</c>), os elementos a
/// criar (colunas, índices e chaves estrangeiras, cada grupo com a contagem escolhida/total) e os dados, com um filtro
/// WHERE opcional. Devolve o <see cref="TableCopyRequest"/>; quem chama corre-o. Um nome que já existe é recusado aqui.
/// </summary>
public sealed class CopyTableDialog : Window
{
    private readonly CopyTableServices _services;
    private readonly ComboBox _database;
    private readonly TextBox _name = new TextBox { Name = "copyName", MinWidth = 220 }.Named("New table name");
    private readonly Group _columns;
    private readonly Group _indexes;
    private readonly Group _foreignKeys;
    private readonly CheckBox _data;
    private readonly TextBox _where = new TextBox { AcceptsReturn = true, Height = 60, TextWrapping = TextWrapping.Wrap, FontFamily = UiFonts.Monospace, Name = "copyWhere" }
        .Named("WHERE clause for data copying");
    private readonly TextBlock _status = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

    public CopyTableDialog(CopyTableServices services)
    {
        Icon = BrandAssets.WindowIcon;
        _services = services ?? throw new ArgumentNullException(nameof(services));
        var source = services.Source;
        Title = "Copy Table...";
        Width = 460;
        Height = 560;
        MinWidth = 380;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = UiFonts.Ui;
        FontSize = 12;

        _database = new ComboBox { ItemsSource = services.Databases, MinWidth = 150, Name = "copyDatabase" }.Named("Target database");
        _database.SelectedItem = services.Databases.Contains(source.Ref.Schema) ? source.Ref.Schema : services.Databases.FirstOrDefault();
        _name.Text = TableCopy.DefaultName(source.Ref.Name);
        var target = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        DockPanel.SetDock(_database, Dock.Left);
        target.Children.Add(_database);
        target.Children.Add(new TextBlock { Text = ".", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0) });
        target.Children.Add(_name);

        _columns = new Group("Columns", source.Columns.Select(c => c.Name));
        _indexes = new Group("Indexes", source.Indexes.Select(i => i.Name));
        _foreignKeys = new Group("Foreign keys", source.ForeignKeys.Select(f => f.Name));
        // A contagem da árvore é uma estimativa (no InnoDB pode ser 0 com linhas): só indica, nunca desliga os dados.
        var rows = services.Rows is { } n ? string.Create(CultureInfo.CurrentCulture, $" (~{n:N0} rows)") : string.Empty;
        _data = new CheckBox { Content = LiteralText.Of("Data" + rows), IsChecked = true }.Named("Data");
        _data.IsCheckedChanged += (_, _) => _where.IsEnabled = _data.IsChecked == true;
        _where.IsEnabled = _data.IsChecked == true;
        var elements = new StackPanel { Spacing = 2, Margin = new Thickness(4) };
        elements.Children.Add(_columns.Root);
        elements.Children.Add(_indexes.Root);
        elements.Children.Add(_foreignKeys.Root);
        elements.Children.Add(_data);

        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center, Name = "btnOk" };
        ok.Click += async (_, _) => await AcceptAsync();
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        cancel.Click += (_, _) => Close(null);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var top = new StackPanel { Spacing = 2 };
        top.Children.Add(new TextBlock { Text = $"Copy \"{source.Ref.Name}\" to new db.table:" });
        top.Children.Add(target);
        top.Children.Add(new TextBlock { Text = "Elements to create in new table:", Margin = new Thickness(0, 10, 0, 2) });
        var bottom = new StackPanel { Spacing = 2 };
        bottom.Children.Add(new TextBlock { Text = "WHERE clause for data copying:", Margin = new Thickness(0, 8, 0, 2) });
        bottom.Children.Add(_where);
        bottom.Children.Add(_status);
        bottom.Children.Add(buttons);

        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(Themed.Bind(new Border { Child = new ScrollViewer { Content = elements }, BorderThickness = new Thickness(1) }, Border.BorderBrushProperty, "border"));
        Content = root;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close(null);
            }
        };
    }

    public TextBox NameBox => _name;

    public ComboBox DatabaseBox => _database;

    public CheckBox DataBox => _data;

    public TextBox WhereBox => _where;

    public string? StatusText => _status.Text;

    /// <summary>Caixa de um grupo ("Columns", "Indexes", "Foreign keys") e as dos elementos (testes).</summary>
    public (CheckBox Group, IReadOnlyList<CheckBox> Items) GroupBoxes(string group) => group switch
    {
        "Columns" => (_columns.Box, _columns.Items),
        "Indexes" => (_indexes.Box, _indexes.Items),
        "Foreign keys" => (_foreignKeys.Box, _foreignKeys.Items),
        _ => throw new ArgumentOutOfRangeException(nameof(group)),
    };

    /// <summary>O pedido com o que está escolhido, ou <c>null</c> com a mensagem do que falta.</summary>
    public TableCopyRequest? Request()
    {
        var name = (_name.Text ?? string.Empty).Trim();
        if (name.Length == 0 || _database.SelectedItem is not string database)
        {
            _status.Text = "Enter a name for the new table.";
            return null;
        }
        if (_columns.Chosen.Count == 0)
        {
            _status.Text = "Select at least one column.";
            return null;
        }
        return new TableCopyRequest(database, name, _columns.Chosen, _indexes.Chosen, _foreignKeys.Chosen, _data.IsChecked == true, _data.IsChecked == true ? _where.Text : null);
    }

    /// <summary>OK: valida e fecha com o pedido; um destino que já existe fica recusado (não se apaga nada).</summary>
    public async Task AcceptAsync()
    {
        if (Request() is not { } request)
        {
            return;
        }
        try
        {
            if (await _services.TargetExists(request.TargetDatabase, request.TargetName, CancellationToken.None))
            {
                _status.Text = $"Table \"{request.TargetDatabase}.{request.TargetName}\" already exists. Choose another name, or drop it first.";
                return;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _status.Text = ex.Message;
            return;
        }
        Close(request);
    }

    /// <summary>Grupo com caixa de três estados e os elementos por baixo; a legenda leva "(escolhidos/total)".</summary>
    private sealed class Group
    {
        private readonly string _caption;
        private bool _syncing;
        private bool? _state;

        public Group(string caption, IEnumerable<string> names)
        {
            _caption = caption;
            Items = [.. names.Select(n => new CheckBox { Content = LiteralText.Of(n), Tag = n, IsChecked = true, MinHeight = 0, Margin = new Thickness(22, 0, 0, 0) }.Named(n))];
            Box = new CheckBox { IsThreeState = true, IsChecked = Items.Count > 0, IsEnabled = Items.Count > 0, MinHeight = 0 }.Named(caption);
            Box.IsCheckedChanged += (_, _) =>
            {
                if (_syncing)
                {
                    return;
                }
                _syncing = true;
                // Um clique no grupo: tudo marcado passa a nada; parcial ou nada passa a tudo (o estado indeterminado
                // só vem dos elementos).
                var value = _state != true;
                foreach (var item in Items)
                {
                    item.IsChecked = value;
                }
                _syncing = false;
                Update();
            };
            foreach (var item in Items)
            {
                item.IsCheckedChanged += (_, _) =>
                {
                    if (!_syncing)
                    {
                        Update();
                    }
                };
            }
            Root = new StackPanel();
            Root.Children.Add(Box);
            foreach (var item in Items)
            {
                Root.Children.Add(item);
            }
            Update();
        }

        public CheckBox Box { get; }

        public IReadOnlyList<CheckBox> Items { get; }

        public StackPanel Root { get; }

        public IReadOnlyList<string> Chosen => [.. Items.Where(i => i.IsChecked == true).Select(i => (string)i.Tag!)];

        private void Update()
        {
            var chosen = Items.Count(i => i.IsChecked == true);
            _syncing = true;
            _state = Items.Count == 0 ? false : chosen == Items.Count ? true : chosen == 0 ? false : null;
            Box.IsChecked = _state;
            _syncing = false;
            Box.Content = LiteralText.Of(string.Create(CultureInfo.CurrentCulture, $"{_caption} ({chosen}/{Items.Count})"));
        }
    }
}
