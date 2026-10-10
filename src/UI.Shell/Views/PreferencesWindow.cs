using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Eunomia.App.Commands;
using Eunomia.App.Settings;
using Eunomia.UI.Controls.Common.Accessibility;
using Eunomia.UI.Controls.Common.Brand;
using Eunomia.UI.Controls.Common.Dialogs;
using Eunomia.UI.Controls.Common.Icons;
using Eunomia.UI.Controls.Common.Theming;
using Eunomia.UI.Shell.Input;

namespace Eunomia.UI.Shell.Views;

/// <summary>
/// Preferences (anexo C §7): tabs General, Logging, SQL, Grid formatting, Data editors, Shortcuts e Files and tabs,
/// com OK · Cancel · Apply · Restore defaults. As alterações ficam pendentes até OK ou Apply. Só aparecem as opções que
/// têm efeito (sem atualizações, estatísticas de uso, línguas nem packs de ícones).
/// </summary>
public sealed class PreferencesWindow : Window
{
    public const string GeneralPage = "General";
    public const string LoggingPage = "Logging";
    public const string DataPage = "Grid formatting";
    public const string DataEditorsPage = "Data editors";

    /// <summary>Fontes propostas para o grid (a caixa aceita outra escrita à mão).</summary>
    public static readonly IReadOnlyList<string> GridFonts =
        OperatingSystem.IsWindows() ? ["Tahoma", "Segoe UI", "Verdana", "Arial", "Calibri", "Consolas", "Courier New", "Cascadia Mono"]
        : OperatingSystem.IsMacOS() ? ["Helvetica Neue", "Helvetica", "Arial", "Verdana", "Menlo", "SF Mono", "Monaco"]
        : ["DejaVu Sans", "Noto Sans", "Liberation Sans", "Ubuntu", "Cantarell", "DejaVu Sans Mono", "Liberation Mono"];
    public const string ShortcutsPage = "Shortcuts";
    public const string SqlPage = "SQL";

    /// <summary>Fontes de largura fixa propostas para o editor (Preferences › SQL só aceita estas).</summary>
    public static readonly IReadOnlyList<string> EditorFonts =
        OperatingSystem.IsWindows() ? ["Consolas", "Courier New", "Cascadia Mono", "Cascadia Code", "Lucida Console"]
        : OperatingSystem.IsMacOS() ? ["Menlo", "SF Mono", "Monaco", "Courier New"]
        : ["DejaVu Sans Mono", "Liberation Mono", "Noto Sans Mono", "Ubuntu Mono", "Fira Code"];

    private readonly ShellContext _context;
    private readonly TabControl _pages = new TabControl().Named("Preferences");

    // Cópias de trabalho (aplicadas em OK/Apply).
    private readonly LogOptions _log;
    private readonly TabOptions _tabs;
    private readonly QueryOptions _query;
    private readonly DataOptions _data;
    private bool _bars;
    private bool _autoReconnect;
    private bool _restoreLastDatabase;
    private string _theme;
    private readonly CommandRegistry _shortcuts;

    public PreferencesWindow(ShellContext context, string? page = null)
    {
        Icon = BrandAssets.WindowIcon;
        _context = context;
        _log = Copy(context.Settings.Log);
        _tabs = new TabOptions { CloseOnDoubleClick = context.Settings.Tabs.CloseOnDoubleClick, CloseOnMiddleClick = context.Settings.Tabs.CloseOnMiddleClick, MultipleLines = context.Settings.Tabs.MultipleLines };
        _theme = context.Settings.Theme;
        var q = context.Settings.Query;
        _query = new QueryOptions
        {
            EditorFont = q.EditorFont,
            EditorFontSize = q.EditorFontSize,
            TabWidth = q.TabWidth,
            TabsToSpaces = q.TabsToSpaces,
            MaxResults = q.MaxResults,
            CompletionEnabled = q.CompletionEnabled,
            CompletionDelayMs = q.CompletionDelayMs,
            AutoUppercase = q.AutoUppercase,
            PromptSaveOnClose = q.PromptSaveOnClose,
            RestoreTabs = q.RestoreTabs,
            HistoryEnabled = q.HistoryEnabled,
            SnippetsDirectory = q.SnippetsDirectory,
        };
        var d = context.Settings.Data;
        _data = new DataOptions
        {
            BlobAsText = d.BlobAsText,
            ForeignDropDown = d.ForeignDropDown,
            RowsPerStep = d.RowsPerStep,
            MaximumRows = d.MaximumRows,
            ResultMemoryMb = d.ResultMemoryMb,
            SpillQuotaMb = d.SpillQuotaMb,
            GlobalResultMemoryMb = d.GlobalResultMemoryMb,
            MaxColumnWidth = d.MaxColumnWidth,
            FontName = d.FontName,
            FontSize = d.FontSize,
        };
        _bars = context.Settings.Display.Bars;
        _autoReconnect = context.Settings.SessionManager.AutoReconnect;
        _restoreLastDatabase = context.Settings.SessionManager.RestoreLastUsedDatabase;
        _shortcuts = HeidiSqlCatalog.CreateRegistry();
        _shortcuts.ImportShortcutOverrides(context.Commands.ExportShortcutOverrides());

        Title = "Preferences";
        Width = 740;
        Height = 500;
        MinWidth = 600;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = UiFonts.Ui;
        FontSize = 12;

        _pages.Items.Add(Page(GeneralPage, BuildGeneral()));
        _pages.Items.Add(Page(LoggingPage, BuildLogging()));
        _pages.Items.Add(Page(SqlPage, BuildSql()));
        _pages.Items.Add(Page(DataPage, BuildGrid()));
        _pages.Items.Add(Page(DataEditorsPage, BuildDataEditors()));
        _pages.Items.Add(Page(ShortcutsPage, BuildShortcuts()));
        _pages.Items.Add(Page("Files and tabs", BuildFilesAndTabs()));
        if (page is not null)
        {
            _pages.SelectedItem = _pages.Items.OfType<TabItem>().FirstOrDefault(t => (string?)t.Header == page);
        }

        var ok = new Button { Content = "OK", MinWidth = 80, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center, Name = "btnOk" };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        var apply = new Button { Content = "Apply", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center, Name = "btnApply" };
        var defaults = new Button { Content = "Restore defaults", HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Click += (_, _) =>
        {
            Apply();
            Close(true);
        };
        cancel.Click += (_, _) => Close(false);
        apply.Click += (_, _) => Apply();
        defaults.Click += async (_, _) => await RestoreDefaultsAsync();
        var buttons = new DockPanel { Margin = new Thickness(8), LastChildFill = false };
        DockPanel.SetDock(defaults, Dock.Left);
        buttons.Children.Add(defaults);
        foreach (var b in new[] { apply, cancel, ok })
        {
            DockPanel.SetDock(b, Dock.Right);
            b.Margin = new Thickness(6, 0, 0, 0);
            buttons.Children.Add(b);
        }
        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(_pages);
        Content = root;
    }

    /// <summary>Registo de trabalho do Shortcuts (testes).</summary>
    internal CommandRegistry PendingShortcuts => _shortcuts;

    private static TabItem Page(string header, Control content) => new()
    {
        Header = header,
        Content = new ScrollViewer { Content = content, Padding = new Thickness(12) },
        FontSize = 12,
    };

    private static TextBlock Note(string text) => new() { Text = text, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap };

    private Control BuildGeneral()
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Check("Automatically reconnect to previously opened sessions on startup", _autoReconnect, v => _autoReconnect = v));
        panel.Children.Add(Check("Restore last used database on startup", _restoreLastDatabase, v => _restoreLastDatabase = v));
        panel.Children.Add(Check("Display bars in various list columns", _bars, v => _bars = v));
        var snippets = new TextBox
        {
            Text = _query.SnippetsDirectory ?? string.Empty,
            PlaceholderText = "(default: Documents/Eunomia/Snippets)",
            Width = 360,
        };
        snippets.TextChanged += (_, _) => _query.SnippetsDirectory = string.IsNullOrWhiteSpace(snippets.Text) ? null : snippets.Text.Trim();
        panel.Children.Add(Labeled("Custom snippets directory:", snippets));
        var theme = new ComboBox { ItemsSource = new[] { "Light", "Dark", "System" }, Width = 160 };
        theme.SelectedIndex = _theme switch { ThemeNames.Dark => 1, ThemeNames.System => 2, _ => 0 };
        theme.SelectionChanged += (_, _) => _theme = theme.SelectedIndex switch { 1 => ThemeNames.Dark, 2 => ThemeNames.System, _ => ThemeNames.Light };
        panel.Children.Add(Labeled("Theme:", theme));
        return panel;
    }

    private Control BuildGrid()
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Labeled("Maximum column width:", Number(_data.MaxColumnWidth, 20, 2000, v => _data.MaxColumnWidth = v)));
        var font = new ComboBox
        {
            ItemsSource = GridFonts.Contains(_data.FontName, StringComparer.OrdinalIgnoreCase) ? GridFonts : [_data.FontName, .. GridFonts],
            SelectedItem = GridFonts.FirstOrDefault(f => string.Equals(f, _data.FontName, StringComparison.OrdinalIgnoreCase)) ?? _data.FontName,
            Width = 180,
        };
        font.SelectionChanged += (_, _) =>
        {
            if (font.SelectedItem is string name)
            {
                _data.FontName = name;
            }
        };
        var fontRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        fontRow.Children.Add(Labeled("Font:", font));
        fontRow.Children.Add(Number(_data.FontSize, 6, 30, v => _data.FontSize = v).Named("Font size"));
        fontRow.Children.Add(new TextBlock { Text = "pt", VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(fontRow);
        var rows = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        rows.Children.Add(Labeled("Rows per page:", Number(_data.RowsPerStep, 1, 1_000_000, v => _data.RowsPerStep = v)));
        rows.Children.Add(Labeled("Maximum:", Number(_data.MaximumRows, 1, 100_000_000, v => _data.MaximumRows = v)));
        panel.Children.Add(rows);
        panel.Children.Add(new TextBlock { Text = "Memory", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(Labeled("Memory per result (MB):", Number(_data.ResultMemoryMb, 16, 65_536, v => _data.ResultMemoryMb = v)));
        panel.Children.Add(Labeled("Memory for all results (MB):", Number(_data.GlobalResultMemoryMb, 64, 262_144, v => _data.GlobalResultMemoryMb = v)));
        panel.Children.Add(Labeled("Disk space for large results (MB, 0 = none):", Number(_data.SpillQuotaMb, 0, 1_048_576, v => _data.SpillQuotaMb = v)));
        panel.Children.Add(Note("Results above the memory limits go to temporary files on disk instead of being cut."));
        return panel;
    }

    private Control BuildDataEditors()
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Check("Show values in foreign key columns", _data.ForeignDropDown, v => _data.ForeignDropDown = v));
        panel.Children.Add(Check("View binary data as text (instead of HEX)", _data.BlobAsText, v => _data.BlobAsText = v));
        return panel;
    }

    private Control BuildLogging()
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(Labeled("Log last [n] lines in SQL log:", Number(_log.MaxLines, 1, 1_000_000, v => _log.MaxLines = v)));
        panel.Children.Add(Labeled("Snip SQL log lines to [n] characters (0 = no snipping):", Number(_log.MaxLineLength, 0, 1_000_000, v => _log.MaxLineLength = v)));
        panel.Children.Add(new TextBlock { Text = "Log events:", Margin = new Thickness(0, 6, 0, 0) });
        panel.Children.Add(Check("Errors", _log.Errors, v => _log.Errors = v));
        panel.Children.Add(Check("User-generated SQL queries", _log.UserSql, v => _log.UserSql = v));
        panel.Children.Add(Check("Internal SQL queries", _log.InternalSql, v => _log.InternalSql = v));
        panel.Children.Add(Check("Import/script queries", _log.Script, v => _log.Script = v));
        panel.Children.Add(Check("Information messages", _log.Info, v => _log.Info = v));
        panel.Children.Add(Check("Debug messages", _log.Debug, v => _log.Debug = v));
        panel.Children.Add(Check("Horizontal scrollbar", _log.HorizontalScrollbar, v => _log.HorizontalScrollbar = v));
        panel.Children.Add(Check("Add timestamp to all log messages", _log.Timestamps, v => _log.Timestamps = v));
        return panel;
    }

    /// <summary>
    /// Preferences › SQL (anexo C §6): fonte do editor e tamanho, tab width, tabs to spaces, máximo de result sets, auto
    /// completion (ligado e atraso depois de <c>.</c>) e auto uppercase. Os esquemas de cores ficam para depois.
    /// </summary>
    private Control BuildSql()
    {
        var panel = new StackPanel { Spacing = 6 };
        var font = new ComboBox { ItemsSource = EditorFonts, SelectedItem = EditorFonts.Contains(_query.EditorFont) ? _query.EditorFont : EditorFonts[0], Width = 180, Name = "editorFont" };
        font.SelectionChanged += (_, _) => _query.EditorFont = font.SelectedItem as string ?? EditorFonts[0];
        var fontRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        fontRow.Children.Add(Labeled("Editor font:", font));
        fontRow.Children.Add(Number(_query.EditorFontSize, 6, 40, v => _query.EditorFontSize = v).Named("Editor font size"));
        fontRow.Children.Add(new TextBlock { Text = "pt", VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(fontRow);
        panel.Children.Add(Labeled("Tab width:", Number(_query.TabWidth, 1, 16, v => _query.TabWidth = v)));
        panel.Children.Add(Check("Tabs to spaces", _query.TabsToSpaces, v => _query.TabsToSpaces = v));
        panel.Children.Add(Labeled("Maximum result sets:", Number(_query.MaxResults, 1, 100, v => _query.MaxResults = v)));
        var completion = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        completion.Children.Add(Check("Auto completion proposal, delay (ms):", _query.CompletionEnabled, v => _query.CompletionEnabled = v));
        completion.Children.Add(Number(_query.CompletionDelayMs, 0, 5000, v => _query.CompletionDelayMs = v).Named("Auto completion delay (ms)"));
        panel.Children.Add(completion);
        panel.Children.Add(Check("Auto uppercase reserved words and functions", _query.AutoUppercase, v => _query.AutoUppercase = v));
        panel.Children.Add(Check("Enable query history", _query.HistoryEnabled, v => _query.HistoryEnabled = v));
        return panel;
    }

    private Control BuildFilesAndTabs()
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(Check("Reopen previously used SQL files and unsaved content in tabs *", _query.RestoreTabs, v => _query.RestoreTabs = v));
        panel.Children.Add(Check("Prompt to save modified files on tab close", _query.PromptSaveOnClose, v => _query.PromptSaveOnClose = v));
        panel.Children.Add(Check("Close tab on doubleclick", _tabs.CloseOnDoubleClick, v => _tabs.CloseOnDoubleClick = v));
        panel.Children.Add(Check("Close tab on middleclick", _tabs.CloseOnMiddleClick, v => _tabs.CloseOnMiddleClick = v));
        panel.Children.Add(Check("Tabs in multiple lines *", _tabs.MultipleLines, v => _tabs.MultipleLines = v));
        panel.Children.Add(new TextBlock { Text = "* needs a restart", Foreground = Brushes.Gray });
        return panel;
    }

    /// <summary>
    /// Preferences › Shortcuts (anexo C §2.5): árvore por categoria pela ordem do HeidiSQL, filtro, hint da action,
    /// caixa de captura do atalho com "Remove", e aviso de conflito com Yes / No / Ignore.
    /// </summary>
    private Control BuildShortcuts()
    {
        var filter = new TextBox { PlaceholderText = "Filter", Margin = new Thickness(0, 0, 0, 6) }.Named("Filter commands");
        var tree = new TreeView { Name = "shortcutTree" }.Named("Commands");
        var hint = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, MinHeight = 34 };
        var capture = new ShortcutBox { Width = 200, Name = "shortcutBox" }.Named("Shortcut");
        var remove = new Button { Content = "Remove" };
        var reset = new Button { Content = "Default" };
        string? selected = null;

        void Fill()
        {
            var text = filter.Text ?? string.Empty;
            var roots = new List<TreeViewItem>();
            foreach (var category in HeidiSqlCatalog.Categories)
            {
                var children = _shortcuts.All
                    .Where(c => c.Category == category && !c.Hidden)
                    .Where(c => text.Length == 0 || c.Caption.Contains(text, StringComparison.OrdinalIgnoreCase))
                    .Select(c =>
                    {
                        var shortcut = _shortcuts.ShortcutOf(c.Id);
                        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                        if (IconFactory.CreateImage(c.Icon) is { } icon)
                        {
                            header.Children.Add(icon);
                        }
                        header.Children.Add(new TextBlock { Text = c.Caption.Replace("&", string.Empty, StringComparison.Ordinal) + (shortcut is { } s ? "   (" + s + ")" : string.Empty) });
                        return new TreeViewItem { Header = header, Tag = c.Id };
                    })
                    .ToList();
                if (children.Count > 0)
                {
                    roots.Add(new TreeViewItem { Header = category, ItemsSource = children, IsExpanded = text.Length > 0 });
                }
            }
            tree.ItemsSource = roots;
        }

        filter.TextChanged += (_, _) => Fill();
        tree.SelectionChanged += (_, _) =>
        {
            selected = (tree.SelectedItem as TreeViewItem)?.Tag as string;
            var command = selected is null ? null : _shortcuts.Get(selected);
            hint.Text = command?.Hint ?? command?.Caption ?? string.Empty;
            capture.Chord = selected is null ? null : _shortcuts.ShortcutOf(selected);
            capture.IsEnabled = remove.IsEnabled = reset.IsEnabled = selected is not null;
        };
        capture.IsEnabled = remove.IsEnabled = reset.IsEnabled = false;
        capture.ChordCaptured += async (_, chord) =>
        {
            if (selected is not null)
            {
                await AssignAsync(selected, chord);
                Fill();
                capture.Chord = _shortcuts.ShortcutOf(selected);
            }
        };
        remove.Click += (_, _) =>
        {
            if (selected is not null)
            {
                _shortcuts.SetShortcut(selected, null);
                capture.Chord = null;
                Fill();
            }
        };
        reset.Click += (_, _) =>
        {
            if (selected is not null)
            {
                _shortcuts.ResetShortcut(selected);
                capture.Chord = _shortcuts.ShortcutOf(selected);
                Fill();
            }
        };
        Fill();

        var editor = new StackPanel { Spacing = 8, Margin = new Thickness(12, 0, 0, 0), Width = 300 };
        editor.Children.Add(hint);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(capture);
        row.Children.Add(remove);
        row.Children.Add(reset);
        editor.Children.Add(new TextBlock { Text = "Shortcut:" });
        editor.Children.Add(row);
        var left = new DockPanel();
        DockPanel.SetDock(filter, Dock.Top);
        left.Children.Add(filter);
        left.Children.Add(tree);
        var layout = new DockPanel();
        DockPanel.SetDock(editor, Dock.Right);
        layout.Children.Add(editor);
        layout.Children.Add(left);
        return layout;
    }

    /// <summary>Atribui um atalho; se já estiver em uso no mesmo contexto, pergunta como no HeidiSQL.</summary>
    internal async Task AssignAsync(string id, KeyChord chord, Func<string, Task<MessageDialogResult>>? ask = null)
    {
        var conflicts = _shortcuts.ConflictsFor(id, chord);
        if (conflicts.Count > 0)
        {
            var other = conflicts[0];
            var message = $"Keyboard shortcut [{chord}] is already assigned to \"{other.Caption}\". Remove it there and assign to \"{_shortcuts.Get(id).Caption}\" instead? Press ignore to keep both and ignore all conflicts.";
            var answer = ask is not null ? await ask(message) : await MessageDialog.ShowAsync(this, "Shortcut conflict", message, MessageDialogButtons.Yes | MessageDialogButtons.No | MessageDialogButtons.Ignore);
            switch (answer)
            {
                case MessageDialogResult.Yes:
                    foreach (var c in conflicts)
                    {
                        _shortcuts.SetShortcut(c.Id, null);
                    }
                    break;
                case MessageDialogResult.Ignore:
                    break;
                default:
                    return;
            }
        }
        _shortcuts.SetShortcut(id, chord);
    }

    /// <summary>Aplica as alterações pendentes às settings e ao registo, e grava.</summary>
    internal void Apply()
    {
        var settings = _context.Settings;
        settings.Log = Copy(_log);
        _context.Log.Options.MaxLines = _log.MaxLines;
        _context.Log.Options.MaxLineLength = _log.MaxLineLength;
        _context.Log.Options.Errors = _log.Errors;
        _context.Log.Options.UserSql = _log.UserSql;
        _context.Log.Options.InternalSql = _log.InternalSql;
        _context.Log.Options.Script = _log.Script;
        _context.Log.Options.Info = _log.Info;
        _context.Log.Options.Debug = _log.Debug;
        _context.Log.Options.Timestamps = _log.Timestamps;
        _context.Log.Options.HorizontalScrollbar = _log.HorizontalScrollbar;
        settings.Tabs.CloseOnDoubleClick = _tabs.CloseOnDoubleClick;
        settings.Tabs.CloseOnMiddleClick = _tabs.CloseOnMiddleClick;
        settings.Tabs.MultipleLines = _tabs.MultipleLines;
        var query = settings.Query;
        query.EditorFont = _query.EditorFont;
        query.EditorFontSize = _query.EditorFontSize;
        query.TabWidth = _query.TabWidth;
        query.TabsToSpaces = _query.TabsToSpaces;
        query.MaxResults = _query.MaxResults;
        query.CompletionEnabled = _query.CompletionEnabled;
        query.CompletionDelayMs = _query.CompletionDelayMs;
        query.AutoUppercase = _query.AutoUppercase;
        query.PromptSaveOnClose = _query.PromptSaveOnClose;
        query.RestoreTabs = _query.RestoreTabs;
        query.HistoryEnabled = _query.HistoryEnabled;
        query.SnippetsDirectory = _query.SnippetsDirectory;
        var data = settings.Data;
        data.ForeignDropDown = _data.ForeignDropDown;
        data.BlobAsText = _data.BlobAsText;
        data.RowsPerStep = _data.RowsPerStep;
        data.MaximumRows = Math.Max(_data.MaximumRows, _data.RowsPerStep);
        data.ResultMemoryMb = _data.ResultMemoryMb;
        data.GlobalResultMemoryMb = _data.GlobalResultMemoryMb;
        data.SpillQuotaMb = _data.SpillQuotaMb;
        data.MaxColumnWidth = _data.MaxColumnWidth;
        data.FontName = _data.FontName;
        data.FontSize = _data.FontSize;
        settings.Display.Bars = _bars;
        settings.SessionManager.AutoReconnect = _autoReconnect;
        settings.SessionManager.RestoreLastUsedDatabase = _restoreLastDatabase;
        settings.Theme = _theme;
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = ThemeOf(_theme);
        }
        _context.Commands.ImportShortcutOverrides(_shortcuts.ExportShortcutOverrides());
        _context.SaveSettings();
    }

    public static ThemeVariant ThemeOf(string theme) => theme switch
    {
        ThemeNames.Dark => ThemeVariant.Dark,
        ThemeNames.System => ThemeVariant.Default,
        _ => ThemeVariant.Light,
    };

    private async Task RestoreDefaultsAsync()
    {
        var answer = await MessageDialog.ShowAsync(this, "Restore defaults", "Restore the default values of all options and shortcuts?", MessageDialogButtons.YesNo);
        if (answer != MessageDialogResult.Yes)
        {
            return;
        }
        _shortcuts.ImportShortcutOverrides(new Dictionary<string, string>());
        var defaults = new LogOptions();
        _context.Settings.Log = defaults;
        _context.Settings.Tabs = new TabOptions();
        _context.Settings.Theme = ThemeNames.Light;
        var data = new DataOptions();
        var current = _context.Settings.Data;
        current.ForeignDropDown = data.ForeignDropDown;
        current.BlobAsText = data.BlobAsText;
        current.RowsPerStep = data.RowsPerStep;
        current.MaximumRows = data.MaximumRows;
        current.ResultMemoryMb = data.ResultMemoryMb;
        current.GlobalResultMemoryMb = data.GlobalResultMemoryMb;
        current.SpillQuotaMb = data.SpillQuotaMb;
        current.MaxColumnWidth = data.MaxColumnWidth;
        current.FontName = data.FontName;
        current.FontSize = data.FontSize;
        _context.Settings.Display.Bars = true;
        _context.Settings.SessionManager.AutoReconnect = false;
        _context.Settings.SessionManager.RestoreLastUsedDatabase = true;
        _context.Commands.ImportShortcutOverrides(new Dictionary<string, string>());
        _context.SaveSettings();
        Close(true);
    }

    private static LogOptions Copy(LogOptions o) => new()
    {
        MaxLines = o.MaxLines,
        MaxLineLength = o.MaxLineLength,
        Timestamps = o.Timestamps,
        HorizontalScrollbar = o.HorizontalScrollbar,
        Errors = o.Errors,
        UserSql = o.UserSql,
        InternalSql = o.InternalSql,
        Script = o.Script,
        Info = o.Info,
        Debug = o.Debug,
    };

    private static Control Labeled(string label, Control control)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(control.Named(label));
        return row;
    }

    private static CheckBox Check(string text, bool value, Action<bool> set)
    {
        var box = new CheckBox { Content = text, IsChecked = value };
        box.IsCheckedChanged += (_, _) => set(box.IsChecked == true);
        return box;
    }

    private static NumericUpDown Number(int value, int min, int max, Action<int> set)
    {
        var box = new NumericUpDown { Value = value, Minimum = min, Maximum = max, Increment = 1, FormatString = "0", Width = 120 };
        box.ValueChanged += (_, e) =>
        {
            if (e.NewValue is { } v)
            {
                set((int)Math.Round(v, MidpointRounding.AwayFromZero));
            }
        };
        return box;
    }
}

/// <summary>Caixa que captura um atalho (tecla + modificadores) e o mostra como texto.</summary>
public sealed class ShortcutBox : TextBox
{
    private KeyChord? _chord;

    public ShortcutBox()
    {
        IsReadOnly = true;
        PlaceholderText = "(none)";
    }

    protected override Type StyleKeyOverride => typeof(TextBox);

    public KeyChord? Chord
    {
        get => _chord;
        set
        {
            _chord = value;
            Text = value?.ToString() ?? string.Empty;
        }
    }

    public event EventHandler<KeyChord>? ChordCaptured;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            return;
        }
        if (KeyChordMapper.FromKey(e.Key, e.KeyModifiers) is { } chord)
        {
            Chord = chord;
            ChordCaptured?.Invoke(this, chord);
        }
    }

    internal static string Describe(KeyChord? chord) => chord?.ToString() ?? string.Empty;

    internal static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);
}
