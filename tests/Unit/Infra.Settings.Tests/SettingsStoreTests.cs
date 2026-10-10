using Eunomia.App.Settings;

namespace Eunomia.Infra.Settings.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eunomia-settings-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    [Fact]
    public void WithoutAFileReturnsHeidiSqlDefaults()
    {
        var s = new JsonSettingsStore(SettingsPath).Load();

        Assert.Equal(270, s.Panels.TreeWidth);
        Assert.Equal(80, s.Panels.LogHeight);
        Assert.Equal(100, s.Panels.QueryEditorHeight);
        Assert.Equal(200, s.Panels.QueryHelpersWidth);
        Assert.Equal(950, s.MainWindow.Width);
        Assert.Equal(600, s.MainWindow.Height);
        Assert.True(s.Display.FilterPanel);
        Assert.True(s.Display.ObjectSizes);
        Assert.Equal(300, s.Log.MaxLines);
        Assert.Equal(2000, s.Log.MaxLineLength);
    }

    [Fact]
    public void SavesAndReadsWithoutLoss()
    {
        var store = new JsonSettingsStore(SettingsPath);
        var s = new AppSettings();
        s.Panels.TreeWidth = 565;
        s.Panels.LogHeight = 120;
        s.MainWindow.Maximized = true;
        s.Display.LogPanel = false;
        s.Theme = ThemeNames.Dark;
        s.Shortcuts["query.run"] = "Ctrl+Enter";
        s.Shortcuts["file.newWindow"] = string.Empty;

        store.Save(s);
        var back = store.Load();

        Assert.Equal(565, back.Panels.TreeWidth);
        Assert.Equal(120, back.Panels.LogHeight);
        Assert.True(back.MainWindow.Maximized);
        Assert.False(back.Display.LogPanel);
        Assert.Equal(ThemeNames.Dark, back.Theme);
        Assert.Equal("Ctrl+Enter", back.Shortcuts["query.run"]);
        Assert.Equal(string.Empty, back.Shortcuts["file.newWindow"]);
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Fact]
    public void ResetPanelDimensionsRestoresTheDefaults()
    {
        var p = new PanelLayout { TreeWidth = 600, LogHeight = 300, QueryEditorHeight = 400, QueryHelpersWidth = 50, PreviewHeight = 10 };

        p.Reset();

        Assert.Equal(270, p.TreeWidth);
        Assert.Equal(80, p.LogHeight);
        Assert.Equal(100, p.QueryEditorHeight);
        Assert.Equal(200, p.QueryHelpersWidth);
        Assert.Equal(100, p.PreviewHeight);
    }

    [Fact]
    public void CorruptedFileIsSetAsideAndStartsWithDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{ \"panels\": { \"treeWidth\": 4");
        var store = new JsonSettingsStore(SettingsPath);

        var s = store.Load();

        Assert.Equal(270, s.Panels.TreeWidth);
        Assert.NotNull(store.LastCorruptBackup);
        Assert.True(File.Exists(store.LastCorruptBackup));
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void UnknownFieldsAndMissingSectionsAreTolerated()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{ \"version\": 1, \"futureThing\": [1,2], \"panels\": null, // comment\n \"theme\": \"dark\", }");

        var s = new JsonSettingsStore(SettingsPath).Load();

        Assert.Equal(ThemeNames.Dark, s.Theme);
        Assert.Equal(270, s.Panels.TreeWidth);
    }

    [Fact]
    public void AtomicWriteReplacesTheWholeFile()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "x.txt");
        File.WriteAllText(path, new string('a', 10_000));

        AtomicFile.WriteAllText(path, "curto");

        Assert.Equal("curto", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void PortableModeWithTheMarkerNextToTheExecutable()
    {
        Directory.CreateDirectory(_dir);
        Assert.False(AppDataPaths.Resolve(_dir).IsPortable);

        File.WriteAllText(Path.Combine(_dir, AppDataPaths.PortableMarker), "{}");
        var paths = AppDataPaths.Resolve(_dir);

        Assert.True(paths.IsPortable);
        Assert.Equal(Path.Combine(_dir, AppDataPaths.PortableMarker), paths.SettingsFile);
        Assert.StartsWith(_dir, paths.AppDatabase, StringComparison.Ordinal);
        Assert.StartsWith(_dir, paths.TabsJournal, StringComparison.Ordinal);
    }
}
