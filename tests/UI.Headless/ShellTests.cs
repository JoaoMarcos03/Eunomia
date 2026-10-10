using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Eunomia.App.Commands;
using Eunomia.App.Settings;
using Eunomia.Presentation.Logging;
using Eunomia.UI.Controls.Common.Dialogs;
using Eunomia.UI.Controls.SqlEditor;
using Eunomia.UI.Shell;
using Eunomia.UI.Shell.Views;

namespace Eunomia.UI.Headless.Tests;

/// <summary>Phase 2 shell: layout (FAM-L01), menus and toolbar (L02, L03), keymap (K01), panels (L07), log and status bar.</summary>
public sealed class ShellTests
{
    private static MainWindow Open(AppSettings? settings = null, ThemeVariant? theme = null)
    {
        var window = new MainWindow(TestShell.CreateContext(settings));
        if (theme is not null)
        {
            window.RequestedThemeVariant = theme;
        }
        window.DataGrid.Culture = CultureInfo.InvariantCulture;
        window.Tree.Tree.Culture = CultureInfo.InvariantCulture;
        window.Show();
        window.CaptureRenderedFrame();
        return window;
    }

    [AvaloniaTheory(Timeout = UiTimeouts.Default)]
    [InlineData("shell-light", "light")]
    [InlineData("shell-dark", "dark")]
    public async Task JanelaPorOmissao(string name, string theme)
    {
        using var culture = new CultureScope(CultureInfo.InvariantCulture);
        var window = Open(theme: theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light);
        var model = window.Tree.Tree.Model!;
        await model.ExpandAsync(model.Roots[0]);
        window.Context.Log.Clear();
        window.Context.Log.Append(LogCategory.InternalSql, "SHOW DATABASES");
        window.Context.Log.Append(LogCategory.Info, "Warning: example warning line");
        window.Context.Log.Append(LogCategory.Error, "SQL Error (1064): example error line");
        window.Context.Status.Info = "elmwood: 179 tables";
        window.Context.Status.ShowConnection(TimeSpan.FromMinutes(10) + TimeSpan.FromDays(4), TimeSpan.FromDays(10), null);
        window.Context.Status.Server = "MariaDB 10.4.32";
        await window.DataGrid.PendingAutoSize;
        window.DataGrid.Selection.Focus(3, 2);
        window.CaptureRenderedFrame();

        Snapshot.Match(window.CaptureRenderedFrame(), name);
        window.Close();
    }

    [AvaloniaFact]
    public void MenusPrincipaisPelaOrdemDoHeidiSql()
    {
        var window = Open();
        var menu = window.GetLogicalDescendants().OfType<Menu>().First();

        Assert.Equal(["_File", "_Edit", "_Display", "_Search", "_Query", "_Tools", "_Go to", "_Help"], menu.Items.OfType<MenuItem>().Select(m => (string)m.Header!));
        window.Close();
    }

    [AvaloniaFact]
    public void ToolbarEMenusSoMostramComandosComHandler()
    {
        var window = Open();
        window.CommandUi.Refresh();
        var registry = window.Context.Commands;
        var buttons = window.Toolbar.GetLogicalChildren().OfType<Control>().Where(c => c.Name?.StartsWith("tb_", StringComparison.Ordinal) == true).ToList();

        Assert.Equal(MainMenuLayout.Toolbar.Count(t => !t.IsSeparator), buttons.Count);
        Assert.All(buttons, b => Assert.Equal(registry.StateOf(b.Name!["tb_".Length..]).IsImplemented, b.IsVisible));

        // Menus: every visible item has a handler; no visible separator at the start, at the end or repeated.
        var menu = window.GetLogicalDescendants().OfType<Menu>().First();
        foreach (var top in menu.Items.OfType<MenuItem>())
        {
            top.Open();
            AssertArranged(top.Items.OfType<Control>().ToList());
            top.Close();
        }
        var tools = menu.Items.OfType<MenuItem>().Single(m => (string)m.Header! == "_Tools");
        Assert.False(tools.Items.OfType<MenuItem>().Single(m => (string)m.Header! == "Flush").IsVisible);
        window.Close();

        void AssertArranged(IReadOnlyList<Control> items)
        {
            var visible = items.Where(i => i.IsVisible).ToList();
            Assert.NotEmpty(visible);
            Assert.IsNotType<Separator>(visible[0]);
            Assert.IsNotType<Separator>(visible[^1]);
            for (var i = 1; i < visible.Count; i++)
            {
                Assert.False(visible[i] is Separator && visible[i - 1] is Separator);
            }
            foreach (var item in visible.OfType<MenuItem>())
            {
                if (item.Items.Count > 0 && item.Items[0] is MenuItem { Header: not "(none)" })
                {
                    AssertArranged(item.Items.OfType<Control>().ToList());
                }
            }
        }
    }

    [AvaloniaFact]
    public void CtrlTAbreECtrlF4FechaQueryTabs()
    {
        var window = Open();
        var before = window.Tabs.QueryTabs.Count;

        window.KeyPress(Key.T, RawInputModifiers.Control, PhysicalKey.T, "t");
        Assert.Equal(before + 1, window.Tabs.QueryTabs.Count);
        Assert.Same(window.Tabs.QueryTabs[^1].Item, window.Tabs.SelectedItem);
        Assert.Equal("Query #2", window.Tabs.QueryTabs[^1].Name);

        window.KeyPress(Key.F4, RawInputModifiers.Control, PhysicalKey.F4, null);
        Assert.Equal(before, window.Tabs.QueryTabs.Count);
        window.Close();
    }

    [AvaloniaFact]
    public void PrimeiraQueryTabNaoFecha()
    {
        var window = Open();
        window.Tabs.SelectedItem = window.Tabs.QueryTabs[0].Item;

        Assert.False(window.Context.Commands.StateOf("query.closeTab").CanExecute);
        window.Close();
    }

    [AvaloniaFact]
    public void CtrlNumeroEscolheATabVisivel()
    {
        var window = Open();

        window.KeyPress(Key.D1, RawInputModifiers.Control, PhysicalKey.Digit1, "1");
        Assert.Same(window.Tabs.HostTab, window.Tabs.SelectedItem);
        window.KeyPress(Key.D4, RawInputModifiers.Control, PhysicalKey.Digit4, "4");
        Assert.Same(window.Tabs.QueryTabs[0].Item, window.Tabs.SelectedItem);
        window.KeyPress(Key.Tab, RawInputModifiers.Control, PhysicalKey.Tab, null);
        Assert.Same(window.Tabs.HostTab, window.Tabs.SelectedItem);
        window.Close();
    }

    [AvaloniaFact]
    public void CtrlEFocaOTableFilterECtrlDAArvore()
    {
        var window = Open();

        window.KeyPress(Key.E, RawInputModifiers.Control, PhysicalKey.E, "e");
        Assert.True(window.Tree.TableFilterBox.IsFocused);
        Assert.Equal([CommandContexts.TreeFilter], window.FocusContexts);

        window.KeyPress(Key.D, RawInputModifiers.Control, PhysicalKey.D, "d");
        Assert.True(window.Tree.Tree.IsFocused);
        window.Close();
    }

    [AvaloniaFact]
    public void CtrlAltFAlternaOFilterPanelEPersiste()
    {
        var window = Open();
        Assert.True(window.FilterPanel.IsVisible);

        window.KeyPress(Key.F, RawInputModifiers.Control | RawInputModifiers.Alt, PhysicalKey.F, null);

        Assert.False(window.FilterPanel.IsVisible);
        Assert.False(window.Context.Settings.Display.FilterPanel);
        Assert.False(window.Context.Commands.StateOf("display.filterPanel").IsChecked);
        window.Close();
    }

    [AvaloniaFact]
    public void AtalhoDeComandoSemHandlerSegueParaOControlo()
    {
        // Ins (Insert row) is not implemented yet: the grid gets the key and the keymap does not consume it.
        var window = Open();
        window.ShowDataTab();
        window.DataGrid.Focus();

        Assert.False(window.TryExecuteShortcut("Ins"));
        Assert.True(window.TryExecuteShortcut("Ctrl+A"));
        window.Close();
    }

    [AvaloniaFact]
    public void AtalhoRedefinidoNasSettingsEhUsado()
    {
        var settings = new AppSettings();
        settings.Shortcuts["query.newTab"] = "Ctrl+Shift+T";
        settings.Shortcuts["nao.existe"] = "F2";
        var window = Open(settings);
        var before = window.Tabs.QueryTabs.Count;

        window.KeyPress(Key.T, RawInputModifiers.Control, PhysicalKey.T, "t");
        Assert.Equal(before, window.Tabs.QueryTabs.Count);
        window.KeyPress(Key.T, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.T, "T");
        Assert.Equal(before + 1, window.Tabs.QueryTabs.Count);
        Assert.Equal(["nao.existe"], window.Context.IgnoredShortcuts);
        window.Close();
    }

    [AvaloniaFact]
    public void ResetPanelDimensionsEDimensoesPersistidas()
    {
        var settings = new AppSettings();
        settings.Panels.TreeWidth = 565;
        settings.Panels.LogHeight = 150;
        var window = new MainWindow(TestShell.CreateContext(settings, out var store));
        window.Show();
        window.CaptureRenderedFrame();
        Assert.Equal(565, window.PanelSizes.TreeWidth, 0.5);
        Assert.Equal(150, window.PanelSizes.LogHeight, 0.5);

        window.Context.Commands.ExecuteAsync("display.resetPanelDimensions").AsTask().Wait();
        window.CaptureRenderedFrame();
        Assert.Equal(270, window.PanelSizes.TreeWidth, 0.5);
        Assert.Equal(80, window.PanelSizes.LogHeight, 0.5);

        window.Close();
        Assert.Equal(1, store.Saves);
        Assert.Equal(270, store.Saved.Panels.TreeWidth);
        Assert.Equal(80, store.Saved.Panels.LogHeight);
    }

    [AvaloniaFact]
    public void LogPanelEscondidoNaoOcupaEspaco()
    {
        var settings = new AppSettings();
        settings.Display.LogPanel = false;
        var window = Open(settings);

        Assert.False(window.LogView.IsVisible);
        Assert.Equal(0, window.PanelSizes.LogHeight, 0.5);
        window.Context.Commands.ExecuteAsync("display.logPanel").AsTask().Wait();
        window.CaptureRenderedFrame();
        Assert.True(window.LogView.IsVisible);
        Assert.Equal(80, window.PanelSizes.LogHeight, 0.5);
        window.Close();
    }

    [AvaloniaFact]
    public void SqlLogMostraAsUltimasLinhasComNumeracaoContinua()
    {
        var settings = new AppSettings();
        settings.Log.MaxLines = 5;
        var window = Open(settings);
        window.Context.Log.Clear();

        for (var i = 1; i <= 12; i++)
        {
            window.Context.Log.Append(LogCategory.UserSql, $"SELECT {i}");
        }

        Assert.Equal(5, window.LogView.LineCount);
        Assert.Equal(window.Context.Log.FirstLineNumber, window.LogView.FirstLineNumber);
        Assert.EndsWith("SELECT 12;", window.LogView.Text, StringComparison.Ordinal);
        Assert.StartsWith("SELECT 8;", window.LogView.Text, StringComparison.Ordinal);

        window.KeyPress(Key.Q, RawInputModifiers.Control, PhysicalKey.Q, "q");
        Assert.Equal(0, window.LogView.Text.Length);
        window.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task PreferencesShortcutsComConflito()
    {
        var window = Open();
        var prefs = new PreferencesWindow(window.Context, PreferencesWindow.ShortcutsPage);

        // Ctrl+D belongs to "Database tree": "No" keeps everything; "Yes" takes it from there.
        await prefs.AssignAsync("query.newTab", KeyChord.Parse("Ctrl+D"), _ => Task.FromResult(MessageDialogResult.No));
        Assert.Equal(KeyChord.Parse("Ctrl+T"), prefs.PendingShortcuts.ShortcutOf("query.newTab"));

        await prefs.AssignAsync("query.newTab", KeyChord.Parse("Ctrl+D"), _ => Task.FromResult(MessageDialogResult.Yes));
        Assert.Equal(KeyChord.Parse("Ctrl+D"), prefs.PendingShortcuts.ShortcutOf("query.newTab"));
        Assert.Null(prefs.PendingShortcuts.ShortcutOf("goto.tree"));
        Assert.Equal(KeyChord.Parse("Ctrl+D"), window.Context.Commands.ShortcutOf("goto.tree"));

        prefs.Apply();
        Assert.Equal(KeyChord.Parse("Ctrl+D"), window.Context.Commands.ShortcutOf("query.newTab"));
        Assert.Null(window.Context.Commands.ShortcutOf("goto.tree"));
        Assert.Equal("Ctrl+D", window.Context.Settings.Shortcuts["query.newTab"]);
        window.Close();
    }

    [AvaloniaFact]
    public void BarraDeTabsFechaAsDaDireitaETemOpcoesPersistidas()
    {
        var window = Open();
        var first = window.Tabs.QueryTabs[0];
        window.Tabs.AddQueryTab();
        window.Tabs.AddQueryTab();
        var second = window.Tabs.QueryTabs[1];
        window.Tabs.AddQueryTab();

        window.Tabs.ContextTab = second;
        Assert.True(window.Context.Commands.ExecuteAsync("tabs.closeToRight").AsTask().Result);
        window.Tabs.ContextTab = null;
        Assert.Equal([first, second], window.Tabs.QueryTabs);

        Assert.True(window.Context.Commands.StateOf("tabs.multipleLines").IsChecked);
        window.Context.Commands.ExecuteAsync("tabs.multipleLines").AsTask().Wait();
        Assert.False(window.Context.Settings.Tabs.MultipleLines);
        window.Close();
    }

    [AvaloniaFact]
    public void StatusBarTemSetePaineis()
    {
        var window = Open();
        var status = window.GetLogicalDescendants().OfType<StatusBarView>().Single();
        window.Context.Status.Busy = "Executing query #1 of 3 ...";
        window.Context.Status.Position = "r1 : c52 (53 B)";

        Assert.Equal("r1 : c52 (53 B)", status.PanelText(1));
        Assert.Equal("Executing query #1 of 3 ...", status.PanelText(6));
        window.Context.Status.SetIdle();
        Assert.Equal("Idle.", status.PanelText(6));
        window.Close();
    }
}
