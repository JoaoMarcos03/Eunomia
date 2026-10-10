using Avalonia.Headless.XUnit;
using Eunomia.App.Settings;
using Eunomia.Infra.Settings.Journal;
using Eunomia.UI.Features.QueryTab;
using Eunomia.UI.Shell;

namespace Eunomia.UI.Headless.Tests;

/// <summary>Query tab restore after reopening or after a crash (appendix C §4.3; FAM-Q07; PR-21).</summary>
public sealed class TabRestoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eunomia-tr-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private MainWindow Window()
    {
        var settings = new AppSettings();
        var context = new ShellContext(settings, new TestShell.MemorySettingsStore(settings)) { TabJournal = new TabJournal(_dir) };
        var window = new MainWindow(context);
        window.Show();
        return window;
    }

    [AvaloniaFact]
    public void TabsComeBackWithTextCaptionCursorAndFocus()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), MainWindow.JournalInterval);
        var first = Window();
        first.Tabs.SelectedItem = first.Tabs.QueryTabs[0].Item;
        first.SqlEditor.Text = "SELECT 1;";
        var second = first.Tabs.AddQueryTab();
        var view = Assert.IsType<QueryTabView>(second.Content);
        view.Editor.Text = "SELECT 2;\nSELECT 3;";
        view.Editor.Editor.Document.Insert(0, "-- note\n");
        view.Editor.Editor.CaretOffset = 5;
        second.SetCaption("Report", modified: true);
        first.Tabs.SelectedItem = second.Item;
        first.SaveJournal(wait: true);
        // Crash: the window does not close and saves nothing more.

        var reopened = Window();
        Assert.Equal(2, reopened.Tabs.QueryTabs.Count);
        var restored = reopened.Tabs.QueryTabs[1];
        Assert.Equal("Report", restored.Name);
        Assert.True(restored.IsModified);
        Assert.Same(restored.Item, reopened.Tabs.SelectedItem);
        var restoredView = Assert.IsType<QueryTabView>(restored.Content);
        Assert.Equal("-- note\nSELECT 2;\nSELECT 3;", restoredView.Editor.Text);
        Assert.Equal(5, restoredView.Editor.Editor.CaretOffset);
        Assert.Equal("SELECT 1;", Assert.IsType<QueryTabView>(reopened.Tabs.QueryTabs[0].Content).Editor.Text);

        // Keeps saving on top of the restored journal; closing the tab deletes it.
        restoredView.Editor.Editor.Document.Insert(restoredView.Editor.Text.Length, "\nSELECT 4;");
        reopened.SaveJournal(wait: true);
        Assert.EndsWith("SELECT 4;", new TabJournal(_dir).Restore()[1].Text, StringComparison.Ordinal);
        reopened.Tabs.CloseQueryTab(restored);
        reopened.SaveJournal(wait: true);
        Assert.Single(new TabJournal(_dir).Restore());
        reopened.Close();
        first.Close();
    }
}
