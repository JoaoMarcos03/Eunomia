using Eunomia.App.Query;
using Eunomia.Infra.Settings.HeidiImport;
using Eunomia.Infra.Settings.Journal;
using Microsoft.Data.Sqlite;

namespace Eunomia.Infra.Settings.Tests;

/// <summary>app.db only set aside when damaged, the tab journal keeps what failed to write, Heidi values with the delimiter.</summary>
public sealed class SettingsFix2Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eunomia-fix2-" + Guid.NewGuid().ToString("N")[..8]);

    public SettingsFix2Tests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void AppDatabaseThatCannotBeOpenedIsNotSetAside()
    {
        var path = Path.Combine(_dir, "app.db");
        new SqliteAppDatabase(path).Dispose();
        SqliteConnection.ClearAllPools();

        // Another process holds the file (only rename allowed): SQLite cannot open it, but it is not damaged.
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete))
        {
            Assert.Throws<SqliteException>(() => new SqliteAppDatabase(path));
        }

        Assert.True(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.corrupt-*"));
    }

    [Fact]
    public void TabJournalKeepsTheEditsWhenTheWriteFails()
    {
        var id = Guid.NewGuid();
        var journal = new TabJournal(_dir);
        journal.SaveState(new TabState { Id = id, Caption = "Query" });
        journal.Append(id, new TextEdit(0, 0, "SELECT 1"));
        journal.Flush();

        journal.Append(id, new TextEdit(7, 1, "42"));
        var journalPath = Path.Combine(_dir, $"query-tab-{id:N}.journal");
        using (new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Throws<IOException>(journal.Flush);
        }
        journal.Flush();

        Assert.Equal("SELECT 42", TabJournal.Replay(journalPath));
    }

    [Fact]
    public void HeidiValueWithTheDelimiterIsKept()
    {
        var d = HeidiSettingsFile.Delimiter;
        var file = HeidiSettingsFile.Parse($"Servers\\s\\Comment{d}1{d}a{d}b\n");

        Assert.Equal(0, file.InvalidLines);
        Assert.Equal($"a{d}b", Assert.Single(file.Values).Value);
    }
}
