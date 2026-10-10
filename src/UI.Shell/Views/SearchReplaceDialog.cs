using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Eunomia.App.Search;
using Eunomia.App.Settings;
using Eunomia.UI.Controls.Common.Accessibility;
using Eunomia.UI.Controls.Common.Brand;
using Eunomia.UI.Controls.Common.Theming;

namespace Eunomia.UI.Shell.Views;

/// <summary>Onde procurar: o editor ou o grid focado; os grids só procuram (a edição chega na Fase 7).</summary>
public sealed record SearchTarget(string Caption, bool CanReplace, bool HasSelection);

/// <summary>O que o utilizador pediu: as opções, o alvo e se é "Replace all".</summary>
public sealed record SearchRequest(SearchOptions Options, int Target, bool ReplaceAll);

/// <summary>
/// "Search and replace text" (anexo C §4.7): Text to find e Replace com recentes e ajudas de regex, Search in, opções
/// (Case sensitive, Whole words, Regular expression, Prompt on replace), Direction, Origin e Scope; OK, Replace all e
/// Cancel. As opções e os recentes ficam nas settings.
/// </summary>
public sealed class SearchReplaceDialog : Window
{
    private readonly ComboBox _find;
    private readonly CheckBox _replaceOn;
    private readonly ComboBox _replace;
    private readonly ComboBox _targets;
    private readonly IReadOnlyList<SearchTarget> _targetList;
    private readonly CheckBox _case = new() { Content = "Case sensitive" };
    private readonly CheckBox _whole = new() { Content = "Whole words" };
    private readonly CheckBox _regex = new() { Content = "Regular expression" };
    private readonly CheckBox _prompt = new() { Content = "Prompt on replace" };
    private readonly RadioButton _forward = new() { Content = "Forward", GroupName = "direction" };
    private readonly RadioButton _backward = new() { Content = "Backward", GroupName = "direction" };
    private readonly RadioButton _fromCursor = new() { Content = "From cursor", GroupName = "origin" };
    private readonly RadioButton _entireScope = new() { Content = "Entire scope", GroupName = "origin" };
    private readonly RadioButton _global = new() { Content = "Global", GroupName = "scope" };
    private readonly RadioButton _selection = new() { Content = "Selection", GroupName = "scope" };
    private readonly Button _ok;
    private readonly Button _replaceAll;
    private readonly SearchSettings _settings;

    public SearchReplaceDialog(string find, bool replace, IReadOnlyList<SearchTarget> targets, int target, SearchSettings settings)
    {
        Icon = BrandAssets.WindowIcon;
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(settings);
        _targetList = targets;
        _settings = settings;
        Title = "Search and replace text";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = UiFonts.Ui;
        FontSize = 12;

        _find = new ComboBox { IsEditable = true, ItemsSource = settings.FindHistory.ToList(), Text = find, HorizontalAlignment = HorizontalAlignment.Stretch }.Named("Text to find");
        _find.AttachedToVisualTree += (_, _) => _find.Focus();
        _replaceOn = new CheckBox { Content = "Replace:", IsChecked = replace, VerticalAlignment = VerticalAlignment.Center };
        _replace = new ComboBox { IsEditable = true, ItemsSource = settings.ReplaceHistory.ToList(), Text = settings.ReplaceHistory.FirstOrDefault() ?? string.Empty, HorizontalAlignment = HorizontalAlignment.Stretch }.Named("Replace");
        _targets = new ComboBox { ItemsSource = targets.Select(t => t.Caption).ToList(), SelectedIndex = Math.Clamp(target, 0, Math.Max(0, targets.Count - 1)), HorizontalAlignment = HorizontalAlignment.Stretch }.Named("Search in");

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"), Margin = new Thickness(16, 16, 16, 0) };
        AddRow(grid, 0, new TextBlock { Text = "Text to find:", VerticalAlignment = VerticalAlignment.Center }, _find,
            Hints(_find, ["^", "$", ".", "\\w", "\\W", "\\d", "\\D", "\\s", "\\S"]));
        AddRow(grid, 1, _replaceOn, _replace, Hints(_replace, ["\\n", "\\t", "$0", "$1", "$2", "$3"]));
        var note = new TextBlock { Text = "Replacement can have \\n for new lines and \\t for tabs", Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 6) };
        Grid.SetRow(note, 2);
        Grid.SetColumn(note, 1);
        grid.Children.Add(note);
        AddRow(grid, 3, new TextBlock { Text = "Search in:", VerticalAlignment = VerticalAlignment.Center }, _targets, null);

        _case.IsChecked = settings.CaseSensitive;
        _whole.IsChecked = settings.WholeWords;
        _regex.IsChecked = settings.RegularExpression;
        _prompt.IsChecked = settings.PromptOnReplace;
        _forward.IsChecked = !settings.Backward;
        _backward.IsChecked = settings.Backward;
        _entireScope.IsChecked = settings.EntireScope;
        _fromCursor.IsChecked = !settings.EntireScope;
        _global.IsChecked = true;

        var groups = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), RowDefinitions = new RowDefinitions("Auto,Auto"), Margin = new Thickness(16, 8, 16, 0) };
        AddGroup(groups, 0, 0, "Options", _case, _whole, _regex, _prompt);
        AddGroup(groups, 0, 1, "Direction", _forward, _backward);
        AddGroup(groups, 1, 0, "Origin", _fromCursor, _entireScope);
        AddGroup(groups, 1, 1, "Scope", _global, _selection);

        _ok = new Button { Content = "OK", MinWidth = 75, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        _replaceAll = new Button { Content = "Replace all", MinWidth = 75, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Cancel", MinWidth = 75, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        _ok.Click += (_, _) => Finish(replaceAll: false);
        _replaceAll.Click += (_, _) => Finish(replaceAll: true);
        cancel.Click += (_, _) => Close(null);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(16, 12, 16, 16) };
        buttons.Children.Add(_ok);
        buttons.Children.Add(_replaceAll);
        buttons.Children.Add(cancel);

        var panel = new StackPanel();
        panel.Children.Add(grid);
        panel.Children.Add(groups);
        panel.Children.Add(buttons);
        Content = panel;

        _replaceOn.IsCheckedChanged += (_, _) => Update();
        _targets.SelectionChanged += (_, _) => Update();
        _find.PropertyChanged += (_, e) =>
        {
            if (e.Property == ComboBox.TextProperty)
            {
                Update();
            }
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close(null);
            }
        };
        Update();
    }

    private static void AddRow(Grid grid, int row, Control label, Control field, Control? trailing)
    {
        label.Margin = new Thickness(0, 4, 12, 4);
        Grid.SetRow(label, row);
        Grid.SetRow(field, row);
        Grid.SetColumn(field, 1);
        field.Margin = new Thickness(0, 4, 0, 4);
        grid.Children.Add(label);
        grid.Children.Add(field);
        if (trailing is not null)
        {
            Grid.SetRow(trailing, row);
            Grid.SetColumn(trailing, 2);
            trailing.Margin = new Thickness(4, 4, 0, 4);
            grid.Children.Add(trailing);
        }
    }

    /// <summary>Botão com as ajudas (regex ou substituição): cada item acrescenta o texto ao campo.</summary>
    private static Button Hints(ComboBox target, IReadOnlyList<string> hints)
    {
        var flyout = new MenuFlyout();
        foreach (var hint in hints)
        {
            var item = new MenuItem { Header = hint };
            item.Click += (_, _) => target.Text = (target.Text ?? string.Empty) + hint;
            flyout.Items.Add(item);
        }
        return new Button { Content = "…", Padding = new Thickness(6, 2), Flyout = flyout, VerticalAlignment = VerticalAlignment.Center };
    }

    private static void AddGroup(Grid grid, int row, int column, string title, params Control[] items)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 4, 0, 2) });
        foreach (var item in items)
        {
            panel.Children.Add(item);
        }
        Grid.SetRow(panel, row);
        Grid.SetColumn(panel, column);
        grid.Children.Add(panel);
    }

    private SearchTarget? Target => _targets.SelectedIndex >= 0 && _targets.SelectedIndex < _targetList.Count ? _targetList[_targets.SelectedIndex] : null;

    private void Update()
    {
        var target = Target;
        var canReplace = target?.CanReplace == true;
        _replaceOn.IsEnabled = canReplace;
        _replace.IsEnabled = canReplace && _replaceOn.IsChecked == true;
        _prompt.IsEnabled = _replace.IsEnabled;
        _replaceAll.IsEnabled = _replace.IsEnabled;
        _selection.IsEnabled = target?.HasSelection == true;
        if (!_selection.IsEnabled)
        {
            _global.IsChecked = true;
        }
        _ok.IsEnabled = !string.IsNullOrEmpty(_find.Text);
    }

    private void Finish(bool replaceAll)
    {
        var find = _find.Text ?? string.Empty;
        var replace = _replace.IsEnabled ? _replace.Text ?? string.Empty : null;
        _settings.CaseSensitive = _case.IsChecked == true;
        _settings.WholeWords = _whole.IsChecked == true;
        _settings.RegularExpression = _regex.IsChecked == true;
        _settings.PromptOnReplace = _prompt.IsChecked == true;
        _settings.Backward = _backward.IsChecked == true;
        _settings.EntireScope = _entireScope.IsChecked == true;
        TextSearch.AddRecent(_settings.FindHistory, find);
        if (replace is not null)
        {
            TextSearch.AddRecent(_settings.ReplaceHistory, replace);
        }
        var options = new SearchOptions
        {
            Find = find,
            Replace = replace,
            CaseSensitive = _settings.CaseSensitive,
            WholeWords = _settings.WholeWords,
            RegularExpression = _settings.RegularExpression,
            PromptOnReplace = _settings.PromptOnReplace,
            Backward = _settings.Backward,
            EntireScope = _settings.EntireScope,
            SelectionOnly = _selection.IsChecked == true,
        };
        Close(new SearchRequest(options, _targets.SelectedIndex, replaceAll));
    }

    public static Task<SearchRequest?> ShowAsync(Window owner, string find, bool replace, IReadOnlyList<SearchTarget> targets, int target, SearchSettings settings) =>
        new SearchReplaceDialog(find, replace, targets, target, settings).ShowDialog<SearchRequest?>(owner);
}
