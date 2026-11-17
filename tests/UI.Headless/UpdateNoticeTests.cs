using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Eunomia.App.Diagnostics;
using Eunomia.App.Settings;
using Eunomia.App.Updates;
using Eunomia.Presentation.Logging;
using Eunomia.UI.Shell;
using Eunomia.UI.Shell.Views;

namespace Eunomia.UI.Headless.Tests;

/// <summary>
/// The update notice (request of 16 Nov 2026): a newer GitHub release shows a bar under the toolbar when Eunomia opens,
/// with its page, "Skip this version" and "Turn off update checks"; being offline shows nothing.
/// </summary>
public sealed class UpdateNoticeTests
{
    /// <summary>Newer than any build of 1.x, so the tests do not depend on the version or the CI build number.</summary>
    private const string NewerTag = "v9.9.9-build.1";

    private const string NewerPage = "https://github.com/JoaoMarcos03/Eunomia/releases/tag/" + NewerTag;

    private static ReleaseInfo Newer => new(NewerTag, new Uri(NewerPage), IsPrerelease: false, IsDraft: false);

    private static (MainWindow Window, TestShell.MemorySettingsStore Store, Diagnostics Diagnostics) Open(FakeFeed feed, AppSettings? settings = null)
    {
        settings ??= new AppSettings();
        var store = new TestShell.MemorySettingsStore(settings);
        var diagnostics = new Diagnostics();
        var context = new ShellContext(settings, store) { Updates = new UpdateChecker(feed), Diagnostics = diagnostics };
        var window = new MainWindow(context) { UpdateCheckDelay = TimeSpan.Zero };
        window.Show();
        return (window, store, diagnostics);
    }

    private static Button ButtonNamed(MainWindow window, string name) =>
        window.UpdateBar.GetLogicalDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == name);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>The SQL log's lines of a kind (information is a <see cref="LogLineKind.Comment"/>).</summary>
    private static List<LogLine> Lines(MainWindow window, LogLineKind kind) =>
        window.Context.Log.Snapshot().Where(l => l.Kind == kind).ToList();

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task ANewerReleaseShowsTheNoticeWhichOpensItsPage()
    {
        Assert.NotNull(ShellContext.Release);
        var feed = new FakeFeed(Newer);
        var (window, store, diagnostics) = Open(feed);
        var opened = new List<string>();
        window.OpenUrlOverride = opened.Add;

        await window.StartupUpdateCheck!;

        Assert.Equal(1, feed.Calls);
        Assert.Equal(NewerTag, window.ShownUpdate?.Tag);
        Assert.True(window.UpdateBar.IsVisible);
        var text = window.UpdateBar.GetLogicalDescendants().OfType<TextBlock>().Single().Text;
        Assert.Equal($"Eunomia 9.9.9 (build 1) is available (you have {ShellContext.DisplayVersion}).", text);
        Assert.NotNull(window.Context.Settings.Updates.LastCheck);
        Assert.True(store.Saves > 0);
        Assert.Contains(diagnostics.Lines, l => l.Contains("9.9.9 (build 1) is available", StringComparison.Ordinal));

        Click(ButtonNamed(window, "Open release page"));

        Assert.Equal([NewerPage], opened);
        Assert.True(window.UpdateBar.IsVisible);
        window.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task TheNoticeWaitsForTheWindowAndADayBetweenQuestions()
    {
        var feed = new FakeFeed(Newer);
        var settings = new AppSettings();
        var store = new TestShell.MemorySettingsStore(settings);
        var window = new MainWindow(new ShellContext(settings, store) { Updates = new UpdateChecker(feed) }) { UpdateCheckDelay = TimeSpan.Zero };

        Assert.Null(window.StartupUpdateCheck);
        Assert.False(window.UpdateBar.IsVisible);
        window.Show();
        await window.StartupUpdateCheck!;
        Assert.True(window.UpdateBar.IsVisible);
        window.Close();

        // The next start, the same day: GitHub is not asked again and the notice still shows.
        var (again, _, _) = Open(feed, settings);
        await again.StartupUpdateCheck!;

        Assert.Equal(1, feed.Calls);
        Assert.Equal(NewerTag, again.ShownUpdate?.Tag);
        again.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task SkipHidesTheVersionUntilANewerOne()
    {
        var feed = new FakeFeed(Newer);
        var (window, store, _) = Open(feed);
        await window.StartupUpdateCheck!;
        var saves = store.Saves;

        Click(ButtonNamed(window, "Skip this version"));

        Assert.False(window.UpdateBar.IsVisible);
        Assert.Null(window.ShownUpdate);
        Assert.Equal(NewerTag, window.Context.Settings.Updates.SkippedTag);
        Assert.Equal(saves + 1, store.Saves);
        Assert.Contains(Lines(window, LogLineKind.Comment), l => l.Text.Contains("9.9.9 (build 1) skipped", StringComparison.Ordinal));
        var settings = window.Context.Settings;
        window.Close();

        var (again, _, _) = Open(feed, settings);
        await again.StartupUpdateCheck!;
        Assert.False(again.UpdateBar.IsVisible);
        again.Close();

        // A newer release shows again (a day later, when GitHub is asked again).
        feed.Releases = [Newer, new ReleaseInfo("v9.9.9-build.2", new Uri(NewerPage + "x"), false, false)];
        settings.Updates.LastCheck = DateTimeOffset.UtcNow.AddDays(-2);
        var (later, _, _) = Open(feed, settings);
        await later.StartupUpdateCheck!;
        Assert.Equal("v9.9.9-build.2", later.ShownUpdate?.Tag);
        later.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task TurningTheChecksOffStopsAskingGitHub()
    {
        var feed = new FakeFeed(Newer);
        var (window, _, _) = Open(feed);
        await window.StartupUpdateCheck!;

        Click(ButtonNamed(window, "Turn off update checks"));

        Assert.False(window.UpdateBar.IsVisible);
        Assert.False(window.Context.Settings.Updates.CheckOnStartup);
        Assert.Contains(Lines(window, LogLineKind.Comment), l => l.Text.Contains("Preferences › General", StringComparison.Ordinal));
        var settings = window.Context.Settings;
        window.Close();

        settings.Updates.LastCheck = DateTimeOffset.UtcNow.AddDays(-2);
        var (again, _, _) = Open(feed, settings);
        await again.StartupUpdateCheck!;
        Assert.Equal(1, feed.Calls);
        Assert.False(again.UpdateBar.IsVisible);
        again.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task TheCloseButtonHidesItForThisRunOnly()
    {
        var feed = new FakeFeed(Newer);
        var (window, _, _) = Open(feed);
        await window.StartupUpdateCheck!;

        Click(ButtonNamed(window, "Close the update notice"));

        Assert.False(window.UpdateBar.IsVisible);
        Assert.Null(window.Context.Settings.Updates.SkippedTag);
        Assert.True(window.Context.Settings.Updates.CheckOnStartup);
        window.Close();
    }

    /// <summary>Offline or GitHub down: no notice and nothing in the SQL log, only a line in the diagnostics log.</summary>
    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task OfflineTheStartupIsQuiet()
    {
        var feed = new FakeFeed(Newer) { Error = new HttpRequestException("No such host is known. (api.github.com:443)") };
        var (window, store, diagnostics) = Open(feed);

        await window.StartupUpdateCheck!;

        Assert.False(window.UpdateBar.IsVisible);
        Assert.Empty(Lines(window, LogLineKind.Error));
        Assert.Null(window.Context.Settings.Updates.LastCheck);
        Assert.Equal(0, store.Saves);
        Assert.Contains(diagnostics.Lines, l => l.StartsWith("Warning updates: Could not check for updates: No such host", StringComparison.Ordinal));
        window.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task CheckForUpdatesAsksNowAndSaysWhatItFound()
    {
        var feed = new FakeFeed();
        var settings = new AppSettings();
        settings.Updates.CheckOnStartup = false;
        var (window, _, _) = Open(feed, settings);
        await window.StartupUpdateCheck!;
        Assert.Equal(0, feed.Calls);
        var commands = window.Context.Commands;
        Assert.True(commands.StateOf("help.checkUpdates").CanExecute);

        Assert.True(await commands.ExecuteAsync("help.checkUpdates"));
        Assert.Equal(1, feed.Calls);
        Assert.False(window.UpdateBar.IsVisible);
        Assert.Contains(Lines(window, LogLineKind.Comment), l => l.Text.Contains($"Eunomia {ShellContext.Release!.Display} is the latest version.", StringComparison.Ordinal));

        feed.Releases = [Newer];
        Assert.True(await commands.ExecuteAsync("help.checkUpdates"));
        Assert.Equal(NewerTag, window.ShownUpdate?.Tag);
        Assert.Contains(Lines(window, LogLineKind.Comment), l => l.Text.Contains($"Eunomia 9.9.9 (build 1) is available: {NewerPage}", StringComparison.Ordinal));

        // A skipped version: the startup notice leaves it out, the command shows it.
        settings.Updates.SkippedTag = NewerTag;
        Assert.True(await commands.ExecuteAsync("help.checkUpdates"));
        Assert.Equal(NewerTag, window.ShownUpdate?.Tag);

        feed.Error = new HttpRequestException("Response status code does not indicate success: 403 (rate limit exceeded).");
        Assert.True(await commands.ExecuteAsync("help.checkUpdates"));
        Assert.Contains(Lines(window, LogLineKind.Error), l => l.Text.Contains("Could not check for updates: Response status code", StringComparison.Ordinal));
        Assert.True(commands.StateOf("help.checkUpdates").CanExecute);
        Assert.False(settings.Updates.CheckOnStartup);
        window.Close();
    }

    /// <summary>Tests and measurements build the shell without a checker: no request, and the command is off.</summary>
    [AvaloniaFact]
    public void WithoutACheckerNothingIsAsked()
    {
        var window = new MainWindow(TestShell.CreateContext());
        window.Show();

        Assert.Null(window.StartupUpdateCheck);
        Assert.False(window.UpdateBar.IsVisible);
        Assert.False(window.Context.Commands.StateOf("help.checkUpdates").CanExecute);
        window.Close();
    }

    [AvaloniaFact]
    public void PreferencesTurnTheCheckOffAndBackOn()
    {
        var window = new MainWindow(TestShell.CreateContext());
        window.Show();
        var prefs = new PreferencesWindow(window.Context, PreferencesWindow.GeneralPage);
        prefs.Show();
        var check = prefs.GetLogicalDescendants().OfType<CheckBox>().Distinct()
            .Single(c => Equals(c.Content, "Check for updates on startup (once a day, on GitHub)"));
        Assert.True(check.IsChecked);

        check.IsChecked = false;
        prefs.Apply();
        Assert.False(window.Context.Settings.Updates.CheckOnStartup);

        check.IsChecked = true;
        prefs.Apply();
        Assert.True(window.Context.Settings.Updates.CheckOnStartup);
        prefs.Close();
        window.Close();
    }

    private sealed class FakeFeed(params ReleaseInfo[] releases) : IReleaseFeed
    {
        public IReadOnlyList<ReleaseInfo> Releases { get; set; } = releases;

        public Exception? Error { get; set; }

        public int Calls { get; private set; }

        public async Task<IReadOnlyList<ReleaseInfo>> GetReleasesAsync(CancellationToken cancellationToken)
        {
            Calls++;
            // Like the network: the answer arrives later, on another thread.
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            return Error is { } error ? throw error : Releases;
        }
    }

    private sealed class Diagnostics : IDiagnosticLog
    {
        public List<string> Lines { get; } = [];

        public void Write(DiagnosticLevel level, string category, string message, Exception? exception = null) =>
            Lines.Add($"{level} {category}: {message}");
    }
}
