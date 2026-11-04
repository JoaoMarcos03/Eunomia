using System.Text;
using Eunomia.App.Sessions;
using Eunomia.App.Settings;
using Microsoft.Data.Sqlite;

namespace Eunomia.Infra.Settings.Tests;

/// <summary>
/// File › Export settings file read back by Import settings file in another Eunomia (user feedback, 2 Nov 2026: the
/// import only understood HeidiSQL files, so an Eunomia export imported nothing).
/// </summary>
public sealed class EunomiaSettingsFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eunomia-settings-file-" + Guid.NewGuid().ToString("N"));

    public EunomiaSettingsFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string FilePath => Path.Combine(_dir, "eunomia-settings.json");

    private static (SessionCatalog Catalog, AppSettings Settings) Source()
    {
        var catalog = new SessionCatalog();
        catalog.Folders.Add(["Customers", "Acme"]);
        catalog.Sessions.Add(new SessionDefinition { Name = "staging", Folder = ["Customers", "Acme"], Host = "db.staging.example", Port = 3307, User = "app", PasswordSaved = true, Comment = "Ünïcode ✓" });
        catalog.Sessions.Add(new SessionDefinition { Name = "local", Host = "127.0.0.1", User = "root", PasswordSaved = true });
        var settings = new AppSettings { Theme = ThemeNames.Dark };
        settings.Shortcuts["query.run"] = "Ctrl+Shift+F9";
        settings.Data.RowsPerStep = 250;
        settings.Tree.OpenTablesInDataTab = false;
        settings.MainWindow.Width = 2000;
        settings.Query.RecentFiles.Add(@"C:\source-machine\a.sql");
        return (catalog, settings);
    }

    [Fact]
    public void ExportFromOneEunomiaImportsIntoAnother()
    {
        var (catalog, settings) = Source();
        using (var appDatabase = new SqliteAppDatabase(Path.Combine(_dir, "source.db")))
        {
            appDatabase.SetFavorite(catalog.Sessions[0].Id.ToString("D"), "shop|shop|orders", true);
            EunomiaSettingsFile.Export(FilePath, catalog, settings, appDatabase);
        }
        Assert.True(EunomiaSettingsFile.LooksLikeEunomiaFile(FilePath));
        Assert.DoesNotContain("password\":\"", File.ReadAllText(FilePath), StringComparison.OrdinalIgnoreCase);

        var target = new SessionCatalog();
        var targetSettings = new AppSettings();
        targetSettings.MainWindow.Width = 1200;
        targetSettings.Query.RecentFiles.Add(@"D:\target\b.sql");
        using var targetDatabase = new SqliteAppDatabase(Path.Combine(_dir, "target.db"));
        var result = EunomiaSettingsFile.Import(FilePath, target, targetSettings, targetDatabase);

        Assert.Equal(2, result.ImportedSessions.Count);
        Assert.Equal(1, result.ImportedFolders);
        Assert.Equal(1, result.ImportedFavorites);
        Assert.True(result.ImportedPreferences);
        var staging = Assert.Single(target.Sessions, s => s.Name == "staging");
        Assert.Equal(["Customers", "Acme"], staging.Folder);
        Assert.Equal(("db.staging.example", 3307, "app", "Ünïcode ✓"), (staging.Host, staging.Port, staging.User, staging.Comment));
        Assert.False(staging.PasswordSaved);
        Assert.Equal(["shop|shop|orders"], targetDatabase.GetFavorites(staging.Id.ToString("D")));

        Assert.Equal(ThemeNames.Dark, targetSettings.Theme);
        Assert.Equal("Ctrl+Shift+F9", targetSettings.Shortcuts["query.run"]);
        Assert.Equal(250, targetSettings.Data.RowsPerStep);
        Assert.False(targetSettings.Tree.OpenTablesInDataTab);
        // What belongs to this machine stays.
        Assert.Equal(1200, targetSettings.MainWindow.Width);
        Assert.Equal([@"D:\target\b.sql"], targetSettings.Query.RecentFiles);
    }

    [Fact]
    public void ImportIsAddOnlyAndGivesClashingIdsANewOne()
    {
        var (catalog, settings) = Source();
        EunomiaSettingsFile.Export(FilePath, catalog, settings, null);

        var target = new SessionCatalog();
        var existing = new SessionDefinition { Name = "local", Host = "10.0.0.9" };
        target.Sessions.Add(existing);
        var clash = new SessionDefinition { Id = catalog.Sessions[0].Id, Name = "other" };
        target.Sessions.Add(clash);
        var result = EunomiaSettingsFile.Import(FilePath, target, new AppSettings(), null);

        Assert.Equal(["local"], result.SkippedSessions);
        Assert.Equal("10.0.0.9", existing.Host);
        var staging = Assert.Single(result.ImportedSessions);
        Assert.NotEqual(clash.Id, staging.Id);
        Assert.Equal(3, target.Sessions.Count);

        // A second import of the same file adds nothing.
        var again = EunomiaSettingsFile.Import(FilePath, target, new AppSettings(), null);
        Assert.Empty(again.ImportedSessions);
        Assert.Equal(0, again.ImportedFolders);
    }

    [Fact]
    public void OlderExportWithoutFavoritesStillImports()
    {
        File.WriteAllText(FilePath, """
            {
              "format": "eunomia-settings",
              "version": 1,
              "settings": { "theme": "dark" },
              "sessions": { "version": 1, "sessions": [ { "name": "old", "host": "h" } ], "folders": [] }
            }
            """, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Assert.True(EunomiaSettingsFile.LooksLikeEunomiaFile(FilePath));

        var target = new SessionCatalog();
        var settings = new AppSettings();
        var result = EunomiaSettingsFile.Import(FilePath, target, settings, null);

        Assert.Equal("old", Assert.Single(result.ImportedSessions).Name);
        Assert.Equal(ThemeNames.Dark, settings.Theme);
        Assert.NotNull(settings.Query);
    }

    [Theory]
    [InlineData("""{ "format": "something-else" }""")]
    [InlineData("""{ "format": "eunomia-settings", "version": 99 }""")]
    [InlineData("""{ "format": "eunomia-settings", """)]
    public void ForeignNewerOrBrokenFilesAreRefusedWithoutChanges(string content)
    {
        File.WriteAllText(FilePath, content);
        var target = new SessionCatalog();
        var settings = new AppSettings();

        Assert.Throws<FormatException>(() => EunomiaSettingsFile.Import(FilePath, target, settings, null));
        Assert.Empty(target.Sessions);
        Assert.Equal(ThemeNames.Light, settings.Theme);
    }

    [Fact]
    public void HeidiSqlFilesAreNotTakenForEunomiaFiles()
    {
        File.WriteAllText(FilePath, "Servers\\local\\Host<|||>1<|||>127.0.0.1\r\n");
        Assert.False(EunomiaSettingsFile.LooksLikeEunomiaFile(FilePath));
    }
}
