using Eunomia.App.Objects;
using Eunomia.Drivers.Abstractions;
using Eunomia.Drivers.MySql;
using Eunomia.Engine.Execution;
using MySqlConnector;

namespace Eunomia.Integration.Tests;

/// <summary>popupDB operations against the server (phase 4; appendix B §6): Drop, Empty, Create and Drop database.</summary>
public sealed class ObjectActionsTests : IAsyncLifetime
{
    private SessionHost _host = null!;
    private string _database = string.Empty;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServerServices Services => _host.ServerServices!;

    private DbObjectRef Ref(string name, DbObjectKind kind = DbObjectKind.Table) => new(_database, _database, name, kind);

    public async ValueTask InitializeAsync()
    {
        if (TestServer.ConnectionString is null)
        {
            return;
        }
        var password = new MySqlConnectionStringBuilder(TestServer.ConnectionString).Password;
        _host = new SessionHost(new SessionConnectionSettings("it", TestServer.Options()) { KeepAlive = null }, new SessionServices
        {
            Provider = new MySqlProvider(),
            ResolveSecret = (_, _) => ValueTask.FromResult(password),
        });
        await _host.ConnectAsync(Ct);
        _database = "eunomia_oa_" + Guid.NewGuid().ToString("N")[..8];
        await Exec(
            $"CREATE DATABASE `{_database}`",
            $"CREATE TABLE `{_database}`.parent (id INT PRIMARY KEY) ENGINE=InnoDB",
            $"CREATE TABLE `{_database}`.child (id INT PRIMARY KEY, p INT, FOREIGN KEY (p) REFERENCES `{_database}`.parent (id)) ENGINE=InnoDB",
            $"INSERT INTO `{_database}`.parent VALUES (1), (2)",
            $"INSERT INTO `{_database}`.child VALUES (1, 1)",
            $"CREATE VIEW `{_database}`.v AS SELECT id FROM `{_database}`.parent",
            $"CREATE TRIGGER `{_database}`.tr BEFORE INSERT ON `{_database}`.child FOR EACH ROW SET NEW.p = NEW.p");
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.RunPrimaryAsync((h, ct) => h.ExecuteNonQueryAsync($"DROP DATABASE IF EXISTS `{_database}`", ct), CancellationToken.None);
            await _host.DisposeAsync();
        }
    }

    private Task Exec(params string[] statements) => _host.RunPrimaryAsync(async (h, ct) =>
    {
        foreach (var sql in statements)
        {
            await h.ExecuteNonQueryAsync(sql, ct);
        }
        return true;
    }, Ct);

    private Task<object?> Scalar(string sql) => _host.RunPrimaryAsync((h, ct) => h.ExecuteScalarAsync(sql, ct), Ct);

    [Fact]
    public void DropOrdenaTriggersViewsEDepoisORestoSemVerificacaoDeChaves()
    {
        TestServer.RequireServer();
        var sql = ObjectActions.DropObjects(Services, [Ref("parent"), Ref("v", DbObjectKind.View), Ref("tr", DbObjectKind.Trigger)]);

        Assert.Equal(
        [
            Services.Dialect.DisableForeignKeyChecks!,
            $"DROP TRIGGER `{_database}`.`tr`",
            $"DROP VIEW `{_database}`.`v`",
            $"DROP TABLE `{_database}`.`parent`",
            Services.Dialect.RestoreForeignKeyChecks!,
        ], sql);
    }

    [Fact]
    public void SelectTopRowsEPreselecaoDaCollation()
    {
        TestServer.RequireServer();
        Assert.Equal("SELECT * FROM `order` LIMIT 1000;", ObjectActions.SelectTopRows(Services.Dialect, Ref("order"), 1000, qualified: false));
        Assert.Equal($"SELECT * FROM `{_database}`.`parent` LIMIT 50;", ObjectActions.SelectTopRows(Services.Dialect, Ref("parent"), 50, qualified: true));

        string[] list = ["latin1_swedish_ci", "utf8mb4_general_ci", "utf8mb4_unicode_ci"];
        Assert.Equal("utf8mb4_general_ci", ObjectActions.PreselectCollation(list, "UTF8MB4_GENERAL_CI", "latin1_swedish_ci"));
        Assert.Equal("latin1_swedish_ci", ObjectActions.PreselectCollation(list, "nao_existe", "latin1_swedish_ci"));
        Assert.Equal("utf8mb4_unicode_ci", ObjectActions.PreselectCollation(list, null, null));
        Assert.Equal("a", ObjectActions.PreselectCollation(["a", "b"], null, null));
        Assert.Null(ObjectActions.PreselectCollation([], null, null));
    }

    [Fact]
    public async Task EmptyComChavesDesligadasEsvaziaAMaeEReporAsVerificacoes()
    {
        TestServer.RequireServer();
        // Without turning off the key checks, TRUNCATE of a referenced table fails (InnoDB).
        await Assert.ThrowsAnyAsync<Exception>(() => ObjectActions.RunAsync(_host, ObjectActions.EmptyTables(Services, [Ref("parent")], false), Ct));

        await ObjectActions.RunAsync(_host, ObjectActions.EmptyTables(Services, [Ref("parent"), Ref("v", DbObjectKind.View)], true), Ct);

        Assert.Equal(0L, Convert.ToInt64(await Scalar($"SELECT COUNT(*) FROM `{_database}`.parent"), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(1L, Convert.ToInt64(await Scalar("SELECT @@FOREIGN_KEY_CHECKS"), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task DropQueFalhaAMeioRepoeAsVerificacoesDeChaves()
    {
        TestServer.RequireServer();
        var sql = ObjectActions.DropObjects(Services, [Ref("nao_existe"), Ref("child")]);

        await Assert.ThrowsAnyAsync<Exception>(() => ObjectActions.RunAsync(_host, sql, Ct));

        Assert.Equal(1L, Convert.ToInt64(await Scalar("SELECT @@FOREIGN_KEY_CHECKS"), System.Globalization.CultureInfo.InvariantCulture));
        // The first error stops the following statements.
        Assert.Equal(1L, Convert.ToInt64(await Scalar($"SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = '{_database}' AND TABLE_NAME = 'child'"), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task CriaEApagaBasesComCollation()
    {
        TestServer.RequireServer();
        var collations = await _host.RunMetadataAsync((h, ct) => h.Metadata.GetCollationsAsync(ct), Ct);
        Assert.Contains(collations, c => c.Name == "utf8mb4_unicode_ci" && c.Charset == "utf8mb4");
        Assert.Contains(collations, c => c.IsDefault);
        var other = _database + "_n";
        try
        {
            await ObjectActions.RunAsync(_host, [Services.Ddl.CreateDatabase(other, "utf8mb4_unicode_ci")], Ct);
            Assert.Equal("utf8mb4_unicode_ci", await Scalar($"SELECT DEFAULT_COLLATION_NAME FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = '{other}'"));

            // The database in USE switches to another one before the DROP.
            await _host.UseDatabaseAsync(other, Ct);
            await ObjectActions.DropDatabaseAsync(_host, other, [other, _database], Ct);

            Assert.Equal(_database, _host.CurrentDatabase);
            Assert.Null(await Scalar($"SELECT SCHEMA_NAME FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = '{other}'"));
        }
        finally
        {
            await Exec($"DROP DATABASE IF EXISTS `{other}`");
        }
    }
}
