using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Eunomia.App.Diagnostics;
using Eunomia.App.Updates;
using Eunomia.Presentation.Logging;
using Eunomia.UI.Controls.Common.Accessibility;
using Eunomia.UI.Controls.Common.Theming;

namespace Eunomia.UI.Shell;

/// <summary>
/// The update notice (request of 16 Nov 2026). After the window opens, without holding it up, the
/// <see cref="UpdateChecker"/> asks GitHub for the releases at most once a day; a newer one shows a bar under the toolbar
/// with its page, "Skip this version" and "Turn off update checks" (Preferences › General turns them back on).
/// Help › Check for updates asks at once. When GitHub cannot be reached, the startup check only writes to the
/// diagnostics log: being offline is not the user's problem.
/// </summary>
public sealed partial class MainWindow
{
    private readonly CancellationTokenSource _updateCancel = new();
    private Border _updateBar = null!;
    private TextBlock _updateText = null!;
    private bool _checkingForUpdates;

    /// <summary>Tests: the release the bar shows; <c>null</c> while it is hidden.</summary>
    internal AvailableUpdate? ShownUpdate { get; private set; }

    /// <summary>Tests: the notice under the toolbar.</summary>
    internal Border UpdateBar => _updateBar;

    /// <summary>Tests: the startup check once the window opened (<c>null</c> when there is none to make).</summary>
    internal Task? StartupUpdateCheck { get; private set; }

    /// <summary>The pause before the startup check, so it does not compete with the first paint; tests set zero.</summary>
    internal TimeSpan UpdateCheckDelay { get; set; } = TimeSpan.FromSeconds(2);

    private void InitUpdates()
    {
        _context.Commands.SetHandler("help.checkUpdates", CheckForUpdatesNowAsync,
            () => _context.Updates is not null && ShellContext.Release is not null && !_checkingForUpdates);
        Closed += (_, _) => _updateCancel.Cancel();
    }

    private Border BuildUpdateBar()
    {
        _updateText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Themed.Bind(_updateText, TextBlock.ForegroundProperty, "text.primary");

        var open = new Button { Content = "Open release page", Padding = new Thickness(8, 1) }.Named("Open release page");
        open.Click += (_, _) => OpenShownRelease();
        var skip = new Button { Content = "Skip this version", Padding = new Thickness(8, 1) }.Named("Skip this version");
        skip.Click += (_, _) => SkipShownUpdate();
        var turnOff = new Button { Content = "Turn off update checks", Padding = new Thickness(8, 1) }.Named("Turn off update checks");
        ToolTip.SetTip(turnOff, "Preferences › General turns them back on");
        turnOff.Click += (_, _) => TurnOffUpdateChecks();
        var close = new Button
        {
            Content = Controls.Common.Icons.IconFactory.CreateImage("close-tab"),
            Padding = new Thickness(1),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        }.Named("Close the update notice");
        ToolTip.SetTip(close, "Hide until Eunomia starts again");
        close.Click += (_, _) => HideUpdateBar();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { open, skip, turnOff, close } };
        DockPanel.SetDock(buttons, Dock.Right);
        var row = new DockPanel { Margin = new Thickness(8, 3, 4, 3), Children = { buttons, _updateText } };
        _updateBar = new Border { Child = row, BorderThickness = new Thickness(0, 0, 0, 1), IsVisible = false }.Named("Update available");
        Themed.Bind(_updateBar, Border.BackgroundProperty, "list.highlight");
        Themed.Bind(_updateBar, Border.BorderBrushProperty, "border");
        return _updateBar;
    }

    /// <summary>From <see cref="Window.Opened"/>: once, and only when the app has a checker and knows its own version.</summary>
    private void StartUpdateCheck()
    {
        if (StartupUpdateCheck is null && _context.Updates is { } checker && ShellContext.Release is { } current)
        {
            StartupUpdateCheck = CheckForUpdatesAtStartupAsync(checker, current);
        }
    }

    private async Task CheckForUpdatesAtStartupAsync(UpdateChecker checker, ReleaseVersion current)
    {
        var options = _context.Settings.Updates;
        var lastCheck = options.LastCheck;
        try
        {
            if (UpdateCheckDelay > TimeSpan.Zero)
            {
                await Task.Delay(UpdateCheckDelay, _updateCancel.Token);
            }
            var update = await checker.CheckAtStartupAsync(options, current, _updateCancel.Token);
            if (options.LastCheck != lastCheck)
            {
                _context.SaveSettings();
                _context.Diagnostics.Write(DiagnosticLevel.Info, "updates", update is null
                    ? "Checked GitHub for updates: none newer than " + current.Display
                    : "Checked GitHub for updates: " + update.Version.Display + " is available");
            }
            if (update is not null)
            {
                ShowUpdate(update);
            }
        }
        catch (OperationCanceledException) when (_updateCancel.IsCancellationRequested)
        {
            // The window closed first.
        }
        catch (Exception ex) when (IsUpdateFailure(ex))
        {
            _context.Diagnostics.Write(DiagnosticLevel.Warning, "updates", "Could not check for updates: " + ex.Message);
        }
    }

    /// <summary>Help › Check for updates: asks GitHub now; a version skipped earlier is offered again.</summary>
    private async ValueTask CheckForUpdatesNowAsync(CancellationToken cancellationToken)
    {
        if (_context.Updates is not { } checker || ShellContext.Release is not { } current || _checkingForUpdates)
        {
            return;
        }
        _checkingForUpdates = true;
        _ui.Refresh();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _updateCancel.Token);
        try
        {
            LogInfo("Checking for updates...");
            var update = await checker.CheckNowAsync(_context.Settings.Updates, current, cancel.Token);
            _context.SaveSettings();
            if (update is null)
            {
                HideUpdateBar();
                LogInfo(string.Create(CultureInfo.InvariantCulture, $"Eunomia {current.Display} is the latest version."));
            }
            else
            {
                ShowUpdate(update);
                LogInfo(string.Create(CultureInfo.InvariantCulture, $"Eunomia {update.Version.Display} is available: {update.Url.AbsoluteUri}"));
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // Cancelled, or the window closed.
        }
        catch (Exception ex) when (IsUpdateFailure(ex))
        {
            _context.Log.Append(LogCategory.Error, "Could not check for updates: " + ex.Message);
        }
        finally
        {
            _checkingForUpdates = false;
            _ui.Refresh();
        }
    }

    /// <summary>What a failed request or an unexpected answer throws (a timeout is a cancellation we did not ask for).</summary>
    private static bool IsUpdateFailure(Exception ex) =>
        ex is HttpRequestException or OperationCanceledException or JsonException or IOException or InvalidOperationException;

    private void ShowUpdate(AvailableUpdate update)
    {
        ShownUpdate = update;
        _updateText.Text = string.Create(CultureInfo.InvariantCulture,
            $"Eunomia {update.Version.Display} is available (you have {ShellContext.DisplayVersion}).");
        ToolTip.SetTip(_updateText, update.Url.AbsoluteUri);
        _updateBar.IsVisible = true;
    }

    private void HideUpdateBar()
    {
        ShownUpdate = null;
        _updateBar.IsVisible = false;
    }

    private void OpenShownRelease()
    {
        // Only a page of the project's releases (UpdateChecker.Offer already refused others; checked again here).
        if (ShownUpdate is { } update && ReleasePages.IsTrusted(update.Url))
        {
            OpenUrl(update.Url.AbsoluteUri);
        }
    }

    private void SkipShownUpdate()
    {
        if (ShownUpdate is not { } update)
        {
            return;
        }
        UpdateChecker.Skip(_context.Settings.Updates, update);
        _context.SaveSettings();
        HideUpdateBar();
        LogInfo(string.Create(CultureInfo.InvariantCulture,
            $"Eunomia {update.Version.Display} skipped: no notice until a newer release (Help › Check for updates still shows it)."));
    }

    private void TurnOffUpdateChecks()
    {
        _context.Settings.Updates.CheckOnStartup = false;
        _context.SaveSettings();
        HideUpdateBar();
        LogInfo("Update checks turned off. Preferences › General turns them back on.");
    }
}
