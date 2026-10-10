using Eunomia.App.Commands;
using Eunomia.App.Secrets;
using Eunomia.App.Sessions;
using Eunomia.App.Settings;
using Eunomia.Infra.Settings.HeidiImport;

namespace Eunomia.Infra.Settings.Tests;

/// <summary>
/// Import of HeidiSQL's preferences and state (phase 13): global options, per-table state (Filter, Sort,
/// HiddenColumns, RecentFilters) and the query history of the imported sessions.
/// </summary>
public sealed class HeidiPreferencesImportTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eunomia-hp-" + Guid.NewGuid().ToString("N")[..8]);

    public HeidiPreferencesImportTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private (HeidiImportResult Result, SessionCatalog Catalog, AppSettings Settings, SqliteAppDatabase Db) Import()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "heidisql-export-sample.txt"))
            .Replace("PASSWORD_ANSI", HeidiPassword.Encrypt("root-pw", 4), StringComparison.Ordinal)
            .Replace("PASSWORD_UNICODE", HeidiPassword.Encrypt("ssh-pw-€", 7), StringComparison.Ordinal);
        var catalog = new SessionCatalog();
        var settings = new AppSettings();
        var db = new SqliteAppDatabase(Path.Combine(_dir, "app.db"));
        var importer = new HeidiSettingsImporter(catalog, settings, HeidiSqlCatalog.CreateRegistry(), new MemorySecretStore(), db);
        return (importer.Import(HeidiSettingsFile.Parse(text), Now), catalog, settings, db);
    }

    [Fact]
    public void PreferenciasGlobaisDoHeidiSql()
    {
        var (result, catalog, settings, db) = Import();
        using (db)
        {
            Assert.True(settings.SessionManager.AutoReconnect);
            Assert.False(settings.SessionManager.RestoreLastUsedDatabase);
            Assert.Equal(500, settings.Data.RowsPerStep);
            Assert.Equal(("Verdana", 9), (settings.Data.FontName, settings.Data.FontSize));
            Assert.Equal(("Courier New", 11), (settings.Query.EditorFont, settings.Query.EditorFontSize));
            Assert.True(settings.Query.BatchInOneGo);
            Assert.Equal(1000, settings.Log.MaxLines);
            Assert.False(settings.Log.InternalSql);
            Assert.Equal(565, settings.Panels.TreeWidth);
            Assert.Equal("UTF-8-BOM", settings.Export.Encoding);
            Assert.True(settings.Export.SelectedRowsOnly);
            Assert.Equal(",", settings.Export.Separator);
            Assert.Equal(ThemeNames.Dark, settings.Theme);
            Assert.True(result.ImportedPreferences >= 15);

            // LastSessions and LastActiveSession by their paths (the session that does not exist is left out).
            Assert.Equal([catalog.FindByPath("localhost")!.Id, catalog.FindByPath(@"Amberline\StagingAmberline")!.Id], settings.SessionManager.LastSessions);
            Assert.Equal(catalog.FindByPath(@"Amberline\StagingAmberline")!.Id, settings.SessionManager.LastActiveSession);
        }
    }

    [Fact]
    public void EstadoPorTabelaEHistorico()
    {
        var (result, catalog, _, db) = Import();
        using (db)
        {
            var local = catalog.FindByPath("localhost")!;
            var budgets = new TableKey(local.Id.ToString("D"), "elmwood", "b2b_budgets");
            Assert.Equal("1_price|0_name", db.GetTableState(budgets, "Sort"));
            Assert.Equal("notes|blob", db.GetTableState(budgets, "HiddenColumns"));
            Assert.Equal(["price > 10", "name LIKE 'a%'"], db.GetRecentFilters(budgets));

            // The old "host|db|table" key works too.
            Assert.Equal("id > 3", db.GetTableState(new TableKey(local.Id.ToString("D"), "elmwood", "prd_products"), "Filter"));
            Assert.Equal(2, result.ImportedTableStates);

            // History: the SQL with "|" stays whole; the line without HeidiSQL's format and the one with a password are ignored.
            var history = db.GetHistory(local.Name);
            Assert.Equal(2, result.ImportedHistory);
            Assert.Contains(history, h => h.Sql == "SELECT * FROM t WHERE a = 'x|y'" && h.Database == "elmwood" && h.Duration == TimeSpan.FromMilliseconds(12));
            Assert.Contains(history, h => h.Sql == "SELECT NOW()" && h.Database is null);
            Assert.DoesNotContain(history, h => h.Sql.Contains("S3gr3do", StringComparison.Ordinal));
        }
    }
}
