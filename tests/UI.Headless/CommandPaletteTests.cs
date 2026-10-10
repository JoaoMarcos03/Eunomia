using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Eunomia.App.Commands;
using Eunomia.UI.Shell;
using Eunomia.UI.Shell.Views;
using static Eunomia.UI.Headless.Tests.RealServer;

namespace Eunomia.UI.Headless.Tests;

/// <summary>Command palette (Tools ▸ Command palette, Ctrl+Shift+P).</summary>
public sealed class CommandPaletteTests
{
    internal static IReadOnlyList<CommandMatch> Sample { get; } =
    [
        new("query.newTab", "New query tab", "SQL", "Ctrl+T", false),
        new("query.run", "Run", "SQL", "F9", false),
        new("tools.preferences", "Preferences", "Tools", null, false),
        new("query.wordWrap", "Wrap long lines", "SQL", null, true),
        new("tools.maintenance", "Maintenance", "Tools", null, false),
        new("tools.findTextOnServer", "Find text on server", "Tools", "Ctrl+Shift+F", false),
    ];

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task ShortcutOpensThePaletteAndTheChosenCommandRuns()
    {
        var window = new MainWindow(TestShell.CreateContext());
        window.Show();
        var tabs = window.Tabs.QueryTabs.Count;

        // The test drives the modal dialog.
        UnexpectedDialogGuardAttribute.Allow(typeof(CommandPaletteWindow));
        Assert.True(window.TryExecuteShortcut("Ctrl+Shift+P"));
        var palette = await WaitAsync(() => window.CommandPalette);
        Assert.Contains("query.newTab", palette.Shown);
        Assert.DoesNotContain("tools.commandPalette", palette.Shown);
        palette.SearchBox.Text = "new query";
        Assert.Equal("query.newTab", palette.Shown[0]);
        palette.Accept();

        await WaitAsync(() => window.Tabs.QueryTabs.Count == tabs + 1 ? "ok" : null);
        Assert.Null(window.CommandPalette);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Snapshot(string theme)
    {
        var palette = new CommandPaletteWindow(Sample) { RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light };
        palette.Show();
        palette.SearchBox.Text = "t";
        palette.CaptureRenderedFrame();

        Tests.Snapshot.Match(palette.CaptureRenderedFrame(), $"dialog-command-palette-{theme}");
        palette.Close();
    }
}
