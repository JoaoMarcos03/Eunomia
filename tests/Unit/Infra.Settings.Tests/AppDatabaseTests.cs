using Eunomia.App.Settings;
using Microsoft.Data.Sqlite;

namespace Eunomia.Infra.Settings.Tests;

public sealed class AppDatabaseTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "eunomia-appdb-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly SqliteAppDatabase _db;

    public AppDatabaseTests() => _db = new SqliteAppDatabase(_path);

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            File.Delete(_path + suffix);
        }
    }

    private static readonly TableKey Products = new("localhost", "shop", "products");

    [Fact]
    public void CriaOEsquemaNaVersaoAtual()
    {
        Assert.Equal(SqliteAppDatabase.SchemaVersion, _db.CurrentSchemaVersion);
    }

    [Fact]
    public void EstadoPorTabelaGravaSubstituiEApaga()
    {
        _db.SetTableState(Products, "sort", "`id` DESC");
        _db.SetTableState(Products, "sort", "`name` ASC");
        Assert.Equal("`name` ASC", _db.GetTableState(Products, "sort"));
        Assert.Null(_db.GetTableState(Products with { Table = "orders" }, "sort"));

        _db.SetTableState(Products, "sort", null);
        Assert.Null(_db.GetTableState(Products, "sort"));
    }

    [Fact]
    public void FiltrosRecentesSobemParaOTopoECortamEm20()
    {
        for (var i = 0; i < 25; i++)
        {
            _db.AddRecentFilter(Products, $"id > {i}");
        }
        _db.AddRecentFilter(Products, "id > 10");

        var recent = _db.GetRecentFilters(Products);

        Assert.Equal(20, recent.Count);
        Assert.Equal("id > 10", recent[0]);
        Assert.Equal("id > 24", recent[1]);
        Assert.DoesNotContain("id > 4", recent);

        _db.ClearRecentFilters(Products);
        Assert.Empty(_db.GetRecentFilters(Products));
    }

    [Fact]
    public void FavoritosPorSessao()
    {
        _db.SetFavorite("a", "shop||products", true);
        _db.SetFavorite("a", "shop||products", true);
        _db.SetFavorite("a", "shop||orders", true);
        _db.SetFavorite("b", "crm||clients", true);
        _db.SetFavorite("a", "shop||orders", false);

        Assert.Equal(["shop||products"], _db.GetFavorites("a"));
        Assert.Equal(["crm||clients"], _db.GetFavorites("b"));
    }

    [Fact]
    public void HistoricoDoMaisRecenteParaOMaisAntigo()
    {
        var t0 = new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);
        var first = _db.AddHistory("a", "shop", "SELECT 1", t0, TimeSpan.FromMilliseconds(12));
        _db.AddHistory("a", null, "SELECT 2", t0.AddMinutes(1), TimeSpan.FromMilliseconds(5));
        _db.AddHistory("b", "crm", "SELECT 3", t0.AddMinutes(2), TimeSpan.Zero);

        var history = _db.GetHistory("a");

        Assert.Equal(["SELECT 2", "SELECT 1"], history.Select(h => h.Sql));
        Assert.Null(history[0].Database);
        Assert.Equal(TimeSpan.FromMilliseconds(12), history[1].Duration);
        Assert.Equal(t0, history[1].ExecutedAt);

        _db.DeleteHistory(first);
        Assert.Single(_db.GetHistory("a"));
        _db.ClearHistory("a");
        Assert.Empty(_db.GetHistory("a"));
        Assert.Single(_db.GetHistory("b"));
    }

    [Fact]
    public void ReabrirMantemOsDados()
    {
        _db.SetFavorite("a", "x", true);
        _db.Dispose();

        using var again = new SqliteAppDatabase(_path);

        Assert.Equal(["x"], again.GetFavorites("a"));
    }
}
