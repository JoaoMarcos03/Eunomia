using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Eunomia.Drivers.Abstractions;
using Eunomia.UI.Controls.Common.Accessibility;
using Eunomia.UI.Controls.Common.Brand;
using Eunomia.UI.Controls.Common.Theming;
using Eunomia.UI.Controls.SqlEditor;

namespace Eunomia.UI.Shell.Views;

/// <summary>
/// SQL help (F1; anexo C, actSQLhelp): a ajuda do próprio servidor (<c>HELP 'termo'</c>). À esquerda, a lista de
/// categorias e tópicos (duplo clique ou Enter numa categoria abre-a); à direita, a descrição e o exemplo do tópico.
/// "Keyword" procura um termo; "Contents" volta às categorias. Não é modal, como no HeidiSQL.
/// </summary>
public sealed class SqlHelpWindow : Window
{
    private readonly Func<string, CancellationToken, Task<SqlHelp>> _help;
    private readonly TextBox _keyword = new TextBox { PlaceholderText = "Keyword", Name = "helpKeyword" }.Named("Keyword");
    private readonly ListBox _entries = new ListBox { Name = "helpEntries" }.Named("Topics");
    private readonly TextBlock _topic = new() { FontWeight = FontWeight.SemiBold, FontSize = 14, Margin = new Thickness(0, 0, 0, 6), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBox _description = new TextBox
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        FontFamily = UiFonts.Monospace,
        Name = "helpDescription",
    }.Named("Description");
    private readonly SqlEditorControl _example = new() { Name = "helpExample" };
    private CancellationTokenSource? _load;

    public SqlHelpWindow(Func<string, CancellationToken, Task<SqlHelp>> help, string? keyword)
    {
        Icon = BrandAssets.WindowIcon;
        _help = help ?? throw new ArgumentNullException(nameof(help));
        Title = "SQL Help";
        Width = 860;
        Height = 560;
        MinWidth = 500;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        _example.Editor.IsReadOnly = true;
        _example.ShowLineNumbers = false;

        _keyword.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(_keyword.Text))
            {
                e.Handled = true;
                _ = ShowAsync(_keyword.Text.Trim());
            }
        };
        var contents = new Button { Content = "Contents" };
        contents.Click += (_, _) => _ = ShowAsync("contents");
        var search = new Button { Content = "Search" };
        search.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_keyword.Text))
            {
                _ = ShowAsync(_keyword.Text.Trim());
            }
        };
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var topButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(6, 0, 0, 0) };
        topButtons.Children.Add(search);
        topButtons.Children.Add(contents);
        DockPanel.SetDock(topButtons, Dock.Right);
        top.Children.Add(topButtons);
        top.Children.Add(_keyword);

        _entries.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<SqlHelpEntry>((entry, _) => new TextBlock
        {
            Text = entry is null ? string.Empty : (entry.IsCategory ? "▸ " : "   ") + entry.Name,
            FontWeight = entry?.IsCategory == true ? FontWeight.SemiBold : FontWeight.Normal,
        });
        _entries.DoubleTapped += (_, _) => Open(_entries.SelectedItem as SqlHelpEntry);
        _entries.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Open(_entries.SelectedItem as SqlHelpEntry);
            }
        };
        _entries.SelectionChanged += (_, _) =>
        {
            if (_entries.SelectedItem is SqlHelpEntry { IsCategory: false } entry)
            {
                _ = ShowTopicAsync(entry.Name);
            }
        };

        var right = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,2*,Auto,*") };
        var descriptionLabel = new TextBlock { Text = "Description:", Margin = new Thickness(0, 0, 0, 2) };
        var exampleLabel = new TextBlock { Text = "Example:", Margin = new Thickness(0, 8, 0, 2) };
        Grid.SetRow(descriptionLabel, 1);
        Grid.SetRow(_description, 2);
        Grid.SetRow(exampleLabel, 3);
        var example = Bordered(_example);
        Grid.SetRow(example, 4);
        right.Children.Add(_topic);
        right.Children.Add(descriptionLabel);
        right.Children.Add(_description);
        right.Children.Add(exampleLabel);
        right.Children.Add(example);

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("240,8,*") };
        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Background = Brushes.Transparent };
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(right, 2);
        body.Children.Add(Bordered(_entries));
        body.Children.Add(splitter);
        body.Children.Add(right);

        var close = new Button { Content = "Close", IsCancel = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        close.Click += (_, _) => Close();
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        bottom.Children.Add(close);

        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(body);
        Content = root;

        _keyword.Text = keyword;
        Opened += (_, _) => _ = ShowAsync(string.IsNullOrWhiteSpace(keyword) ? "contents" : keyword.Trim());
        Closed += (_, _) => _load?.Cancel();
    }

    /// <summary>Entradas da lista (testes).</summary>
    public IReadOnlyList<SqlHelpEntry> Entries => _entries.ItemsSource as IReadOnlyList<SqlHelpEntry> ?? [];

    /// <summary>Tópico mostrado (testes).</summary>
    public string? TopicName => _topic.Text;

    public string? DescriptionText => _description.Text;

    public string ExampleText => _example.Text;

    /// <summary>Procura um termo: um tópico mostra-se logo; uma categoria ou vários tópicos enchem a lista.</summary>
    public async Task ShowAsync(string term)
    {
        if (await LoadAsync(term) is not { } help)
        {
            return;
        }
        if (help.IsTopic)
        {
            if (Entries.All(e => !string.Equals(e.Name, help.Name, StringComparison.OrdinalIgnoreCase)))
            {
                _entries.ItemsSource = new List<SqlHelpEntry> { new(help.Name!, false) };
            }
            ShowTopic(help);
            return;
        }
        _entries.ItemsSource = help.Entries.ToList();
        ShowTopic(null);
        if (help.Entries.Count == 0)
        {
            _topic.Text = $"No help found for \"{term}\".";
        }
    }

    private void Open(SqlHelpEntry? entry)
    {
        if (entry is not null)
        {
            _ = entry.IsCategory ? ShowAsync(entry.Name) : ShowTopicAsync(entry.Name);
        }
    }

    private async Task ShowTopicAsync(string name)
    {
        if (await LoadAsync(name) is { IsTopic: true } help)
        {
            ShowTopic(help);
        }
    }

    private void ShowTopic(SqlHelp? help)
    {
        _topic.Text = help?.Name ?? string.Empty;
        _description.Text = help?.Description?.Trim() ?? string.Empty;
        _example.Text = help?.Example?.Trim() ?? string.Empty;
    }

    private async Task<SqlHelp?> LoadAsync(string term)
    {
        _load?.Cancel();
        var cts = new CancellationTokenSource();
        _load = cts;
        try
        {
            var help = await _help(term, cts.Token);
            return ReferenceEquals(_load, cts) ? help : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _topic.Text = ex.Message;
            return null;
        }
    }

    private static Border Bordered(Control child) =>
        UI.Controls.Common.Theming.Themed.Bind(new Border { Child = child, BorderThickness = new Thickness(1) }, Border.BorderBrushProperty, "border");
}
