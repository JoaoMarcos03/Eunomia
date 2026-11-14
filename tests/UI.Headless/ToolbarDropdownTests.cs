using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Eunomia.App.Sessions;
using Eunomia.UI.Shell;
using static Eunomia.UI.Headless.Tests.RealServer;

namespace Eunomia.UI.Headless.Tests;

/// <summary>
/// Toolbar arrows (user request, 12 Nov 2026): the Session manager arrow lists the saved sessions and the Run arrow the
/// ways to run, with the items really in the menu that opens; the Run tooltip gives the shortcut of each way.
/// </summary>
public sealed class ToolbarDropdownTests
{
    /// <summary>Clicks the button's arrow with the mouse, as the user does, and returns the visible items of the menu that opened.</summary>
    private static List<string> ClickArrow(MainWindow window, string id)
    {
        var pair = (StackPanel)window.Toolbar.GetLogicalDescendants().OfType<Control>().First(c => c.Name == "tb_" + id);
        var arrow = (Button)pair.Children[1];
        Assert.True(arrow.IsEffectivelyEnabled);
        var center = arrow.TranslatePoint(new Point(arrow.Bounds.Width / 2, arrow.Bounds.Height / 2), window)!.Value;
        window.MouseMove(center, RawInputModifiers.None);
        window.MouseDown(center, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(center, MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        var menu = window.CommandUi.OpenDropdown;
        Assert.NotNull(menu);
        Assert.True(menu.IsOpen);
        // Counts what the menu shows (the presenter inside the popup), not the flyout's ItemsSource: that is where the items got lost.
        var popup = (Avalonia.Controls.Primitives.Popup)typeof(Avalonia.Controls.Primitives.PopupFlyoutBase)
            .GetProperty("Popup", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!.GetValue(menu)!;
        var presenter = Assert.IsType<MenuFlyoutPresenter>(popup.Child);
        return [.. presenter.Items.OfType<MenuItem>().Where(i => i.IsVisible).Select(i => i.Header as string ?? string.Empty)];
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public void SessionManagerArrowListsSavedSessions()
    {
        var shell = CreateShell("Server=127.0.0.1;Port=1;User ID=u;Password=p", string.Empty);
        shell.Context.Sessions!.Catalog.Sessions.Add(new SessionDefinition { Name = "staging", Host = "127.0.0.1" });
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["it", "staging"], ClickArrow(shell.Window, "session.manager"));

        shell.Window.CommandUi.OpenDropdown!.Hide();
        Dispatcher.UIThread.RunJobs();
        Assert.Null(shell.Window.CommandUi.OpenDropdown);
        shell.Window.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task RunArrowShowsTheWaysToRunAndTooltipGivesShortcuts()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "No test server.");
        var (window, _, session, _, _) = CreateShell(cs, string.Empty);
        Assert.True(await window.ConnectAsync(window, session, null));
        window.Tree.Tree.FocusedNode = await WaitAsync(() => window.Tree.Tree.Model!.Roots.FirstOrDefault());
        window.Tabs.SelectedItem = window.Tabs.QueryTabs[0].Item;
        window.SqlEditor.Text = "SELECT 1; SELECT 2;";
        await WaitAsync(() =>
        {
            window.CommandUi.RefreshToolbar();
            return window.Toolbar.GetLogicalDescendants().OfType<Control>().First(c => c.Name == "tb_query.run").IsEnabled ? "ok" : null;
        });

        var run = window.Toolbar.GetLogicalDescendants().OfType<Control>().First(c => c.Name == "tb_query.run");
        var tip = Assert.IsType<string>(ToolTip.GetTip(run));
        Assert.StartsWith("Run (F9)", tip, StringComparison.Ordinal);
        Assert.Contains("Ctrl+F9: Run Selection", tip, StringComparison.Ordinal);
        Assert.Contains("Ctrl+Shift+F9: Run current query", tip, StringComparison.Ordinal);
        Assert.EndsWith("Click the arrow for more options.", tip, StringComparison.Ordinal);

        Assert.Equal(["Run", "Run Selection", "Run current query"], ClickArrow(window, "query.run").Take(3));
        window.Close();
    }
}
