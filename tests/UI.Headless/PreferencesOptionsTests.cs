using Avalonia.Headless.XUnit;
using Eunomia.App.Settings;
using Eunomia.UI.Shell;
using static Eunomia.UI.Headless.Tests.RealServer;

namespace Eunomia.UI.Headless.Tests;

/// <summary>Preferences options with an effect (phase 13): grid font and width, list bars, reconnecting sessions.</summary>
public sealed class PreferencesOptionsTests
{
    [AvaloniaFact]
    public void GridFontAndMaximumWidthApply()
    {
        var settings = new AppSettings();
        var window = new MainWindow(TestShell.CreateContext(settings));
        window.Show();
        Assert.Equal("Tahoma", window.DataGrid.FontFamilyName);
        Assert.Equal(8 * 96.0 / 72.0, window.DataGrid.FontSize, 3);

        settings.Data.FontName = "Segoe UI";
        settings.Data.FontSize = 10;
        settings.Data.MaxColumnWidth = 150;
        window.ApplyEditorOptionsToTabs();

        Assert.Equal("Segoe UI", window.DataGrid.FontFamilyName);
        Assert.Equal(10 * 96.0 / 72.0, window.DataGrid.FontSize, 3);
        Assert.Equal(150, window.DataGrid.MaxAutoColumnWidth);
        window.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task ReconnectsTheSessionsOpenAtShutdownAndHidesBars()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "No test server.");
        var settings = new AppSettings();
        var (window, _, session, connector, _) = CreateShell(cs, string.Empty, settings);

        // Off: connects nothing.
        settings.SessionManager.LastSessions = [session.Id];
        Assert.False(await window.ReconnectLastSessionsAsync());
        Assert.Empty(connector.Open);

        settings.SessionManager.AutoReconnect = true;
        Assert.True(await window.ReconnectLastSessionsAsync());
        Assert.Single(connector.Open);

        // Display bars off: the Host tab lists no longer have bars.
        await WaitAsync(() => window.Tree.Tree.Model!.Roots.FirstOrDefault());
        window.Tabs.SelectedItem = window.Tabs.HostTab;
        var host = window.HostView;
        Assert.True(host.DatabaseList.ShowBars);
        settings.Display.Bars = false;
        window.ApplyEditorOptionsToTabs();
        Assert.False(host.DatabaseList.ShowBars);
        Assert.False(host.ProcessList.ShowBars);

        await connector.DisconnectAllAsync();
        window.Close();
    }
}
