using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Eunomia.UI.Controls.Common.Input;

/// <summary>
/// A select for long lists (the databases of a server): typing filters the list (contains, ignoring case), the arrow
/// shows it all. Only an item of the list can be chosen: text that matches none goes back to the chosen item when the
/// box loses the focus; empty text means no item.
/// </summary>
public sealed class SearchableSelect : UserControl
{
    private IReadOnlyList<string> _items = [];
    private string? _selected;
    private bool _setting;

    public SearchableSelect()
    {
        Box = new AutoCompleteBox
        {
            FilterMode = AutoCompleteFilterMode.Custom,
            ItemFilter = Matches,
            MinimumPrefixLength = 0,
            MinimumPopulateDelay = TimeSpan.Zero,
            MaxDropDownHeight = 320,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var arrow = new Button
        {
            Content = "▾",
            Padding = new Thickness(8, 0),
            Margin = new Thickness(2, 0, 0, 0),
            Focusable = false,
            VerticalAlignment = VerticalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        Avalonia.Automation.AutomationProperties.SetName(arrow, "Show all");
        arrow.Click += (_, _) => Open();
        Box.SelectionChanged += (_, _) =>
        {
            if (!_setting && Box.SelectedItem is string item)
            {
                Commit(item);
            }
        };
        Box.LostFocus += (_, _) => CommitText();
        // Typing replaces the chosen item instead of adding to it; after the click that gave the focus (it puts the caret).
        Box.GotFocus += (_, _) => Dispatcher.UIThread.Post(SelectText, DispatcherPriority.Input);
        Box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !Box.IsDropDownOpen)
            {
                CommitText();
            }
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(Box);
        Grid.SetColumn(arrow, 1);
        grid.Children.Add(arrow);
        Content = grid;
    }

    /// <summary>The text box with the list (tests type into it).</summary>
    public AutoCompleteBox Box { get; }

    public IReadOnlyList<string> Items
    {
        get => _items;
        set
        {
            _items = value ?? [];
            Box.ItemsSource = _items;
            SelectedItem = _selected is not null && _items.Contains(_selected, StringComparer.Ordinal) ? _selected : null;
        }
    }

    /// <summary>The chosen item, or <c>null</c>.</summary>
    public string? SelectedItem
    {
        get => _selected;
        set
        {
            var item = value is not null && _items.Contains(value, StringComparer.Ordinal) ? value : null;
            var changed = !string.Equals(item, _selected, StringComparison.Ordinal);
            _selected = item;
            _setting = true;
            try
            {
                Box.Text = item ?? string.Empty;
            }
            finally
            {
                _setting = false;
            }
            if (changed)
            {
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public string? PlaceholderText
    {
        get => Box.PlaceholderText;
        set => Box.PlaceholderText = value;
    }

    public event EventHandler? SelectionChanged;

    /// <summary>Opens the list with every item (the arrow).</summary>
    public void Open()
    {
        Box.Focus();
        Box.IsDropDownOpen = true;
    }

    /// <summary>Items the text keeps in the list: all of them while the text is the chosen item (nothing typed yet).</summary>
    private bool Matches(string? search, object? item) =>
        item is string text
        && (string.IsNullOrEmpty(search)
            || string.Equals(search, _selected, StringComparison.Ordinal)
            || text.Contains(search, StringComparison.OrdinalIgnoreCase));

    private void SelectText()
    {
        if (Box.IsKeyboardFocusWithin && Box.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() is { } text)
        {
            text.SelectAll();
        }
    }

    private void Commit(string? item)
    {
        if (!string.Equals(item, _selected, StringComparison.Ordinal) || !string.Equals(Box.Text, item ?? string.Empty, StringComparison.Ordinal))
        {
            SelectedItem = item;
        }
    }

    /// <summary>Typed text: the item it names (ignoring case), or the only item that contains it; otherwise the chosen item stays.</summary>
    private void CommitText()
    {
        var text = Box.Text?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            Commit(null);
            return;
        }
        var exact = _items.FirstOrDefault(i => string.Equals(i, text, StringComparison.Ordinal))
            ?? _items.FirstOrDefault(i => string.Equals(i, text, StringComparison.OrdinalIgnoreCase));
        if (exact is null)
        {
            var matches = _items.Where(i => i.Contains(text, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
            exact = matches.Count == 1 ? matches[0] : _selected;
        }
        Commit(exact);
    }
}
