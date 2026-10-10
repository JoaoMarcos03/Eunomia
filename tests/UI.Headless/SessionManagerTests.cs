using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Eunomia.App.Secrets;
using Eunomia.App.Sessions;
using Eunomia.App.Settings;
using Eunomia.Presentation.Sessions;
using Eunomia.UI.Controls.Common.TreeList;
using Eunomia.UI.Features.Sessions;

namespace Eunomia.UI.Headless.Tests;

/// <summary>Session Manager (FAM-S01, S03): snapshot with folders and colours, editing with Save, New and filter.</summary>
public sealed class SessionManagerTests
{
    private sealed class MemoryStore : ISessionStore
    {
        public int Saves { get; private set; }

        public SessionCatalog Load() => new();

        public void Save(SessionCatalog catalog) => Saves++;
    }

    private static (SessionManagerWindow Window, SessionManagerModel Model, MemoryStore Store) Open(ThemeVariant? theme = null)
    {
        var catalog = new SessionCatalog();
        var created = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);
        catalog.Folders.Add(["ALBANY"]);
        catalog.Sessions.Add(new SessionDefinition { Name = "ALLamberline", Folder = ["Amberline"], Host = "203.0.113.10", Color = 0xF03333, Statistics = { ConnectCount = 55, ServerVersionFull = "8.0.36", Created = created } });
        catalog.Sessions.Add(new SessionDefinition { Name = "PROD_Amberline", Folder = ["Amberline"], Host = "203.0.113.11", Color = 0xDC143C, Statistics = { ConnectCount = 11, Created = created } });
        catalog.Sessions.Add(new SessionDefinition { Name = "StagingAmberline", Folder = ["Amberline"], Host = "203.0.113.12", Port = 3307, Color = 0x76845C, Statistics = { ConnectCount = 23, Created = created } });
        catalog.Sessions.Add(new SessionDefinition { Name = "localhost", Host = "127.0.0.1", Color = 0x006400, Statistics = { ConnectCount = 63, Created = created } });
        catalog.Folders.Add(["Elmwood"]);
        var store = new MemoryStore();
        var model = new SessionManagerModel(catalog, store, new MemorySecretStore());
        var options = new SessionManagerOptions();
        var window = new SessionManagerWindow(new SessionManagerServices
        {
            Model = model,
            Options = options,
            OpenAsync = (_, _, _) => Task.FromResult(false),
            Version = "0.3.0",
        });
        if (theme is not null)
        {
            window.RequestedThemeVariant = theme;
        }
        window.Show();
        window.CaptureRenderedFrame();
        return (window, model, store);
    }

    private static TreeListNode Node(SessionManagerWindow window, string name) =>
        window.SessionList.AllNodes().First(n => n[0].StartsWith(name, StringComparison.Ordinal));

    /// <summary>Start tab (no session selected): logo and version above the text and the import buttons.</summary>
    [AvaloniaTheory]
    [InlineData("session-manager-start-light", "light")]
    [InlineData("session-manager-start-dark", "dark")]
    public void AspetoDoStart(string name, string theme)
    {
        using var culture = new CultureScope(CultureInfo.InvariantCulture);
        var (window, _, _) = Open(theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light);
        window.SessionList.FocusedNode = Node(window, "Amberline");
        window.CaptureRenderedFrame();

        var logo = window.GetVisualDescendants().OfType<Image>().Single(i => i.Name == "imgLogo");
        Assert.True(logo.IsEffectivelyVisible);
        Snapshot.Match(window.CaptureRenderedFrame(), name);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("session-manager-light", "light")]
    [InlineData("session-manager-dark", "dark")]
    public void Aspeto(string name, string theme)
    {
        using var culture = new CultureScope(CultureInfo.InvariantCulture);
        var (window, _, _) = Open(theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light);
        var amberline = Node(window, "Amberline");
        window.SessionList.Expand(amberline);
        window.SessionList.FocusedNode = Node(window, "StagingAmberline");
        window.CaptureRenderedFrame();

        Snapshot.Match(window.CaptureRenderedFrame(), name);
        window.Close();
    }

    [AvaloniaFact]
    public void FoldersNoTopoEOrdenados()
    {
        var (window, _, _) = Open();

        Assert.Equal(["ALBANY", "Amberline", "Elmwood", "localhost"], window.SessionList.Roots.Select(n => n[0]));
        Assert.Equal(0xF03333u, window.SessionList.Roots[1].Children[0].Background);
        window.Close();
    }

    [AvaloniaFact]
    public void EditarAtivaOSaveECtrlSGrava()
    {
        var (window, model, store) = Open();
        window.SessionList.FocusedNode = Node(window, "localhost");
        window.CaptureRenderedFrame();
        Assert.Same(model.Catalog.FindByPath("localhost"), model.Selected);
        Assert.False(window.SaveButton.IsEnabled);

        model.Draft!.Port = 3307;
        window.RefreshButtons();
        Assert.True(window.SaveButton.IsEnabled);

        window.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.S, "s");

        Assert.Equal(3307, model.Catalog.FindByPath("localhost")!.Port);
        Assert.False(window.SaveButton.IsEnabled);
        Assert.Equal(1, store.Saves);
        window.Close();
    }

    [AvaloniaFact]
    public void FiltroMantemOsFoldersPais()
    {
        var (window, _, _) = Open();

        window.FilterBox.Text = "prod";
        window.ApplyFilterNow();
        window.CaptureRenderedFrame();

        Assert.Equal(["Amberline", "PROD_Amberline"], window.SessionList.VisibleNodes.Select(n => n[0]));
        window.Close();
    }

    [AvaloniaFact]
    public void FolderSelecionadoMostraSoOStart()
    {
        var (window, _, _) = Open();

        window.SessionList.FocusedNode = Node(window, "Elmwood");
        window.CaptureRenderedFrame();

        Assert.Equal(["Start"], window.Tabs.Items.OfType<TabItem>().Where(t => t.IsVisible).Select(t => t.Header as string ?? "x"));
        window.Close();
    }
}
