using Eunomia.Drivers.Abstractions;
using Eunomia.Drivers.MySql;
using Eunomia.Engine.Execution;
using Eunomia.Engine.Metadata;
using MySqlConnector;

namespace Eunomia.Integration.Tests;

/// <summary>Per-session metadata cache (phase 4; PR-08; FAM-S04, T08).</summary>
public sealed class MetadataCacheTests : IAsyncLifetime
{
    private SessionHost _host = null!;
    private string _database = string.Empty;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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
        _database = "eunomia_md_" + Guid.NewGuid().ToString("N")[..8];
        await _host.RunPrimaryAsync(async (h, ct) =>
        {
            await h.ExecuteNonQueryAsync($"CREATE DATABASE `{_database}`", ct);
            await h.ExecuteNonQueryAsync($"CREATE TABLE `{_database}`.t1 (id INT PRIMARY KEY, v VARCHAR(100))", ct);
            await h.ExecuteNonQueryAsync($"INSERT INTO `{_database}`.t1 VALUES (1, REPEAT('x', 100)), (2, 'y')", ct);
            await h.ExecuteNonQueryAsync($"CREATE VIEW `{_database}`.v1 AS SELECT id FROM `{_database}`.t1", ct);
            return true;
        }, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.RunPrimaryAsync((h, ct) => h.ExecuteNonQueryAsync($"DROP DATABASE IF EXISTS `{_database}`", ct), CancellationToken.None);
            await _host.DisposeAsync();
        }
    }

    [Fact]
    public async Task NomesPrimeiroEstatisticasDepois()
    {
        TestServer.RequireServer();
        var cache = new MetadataCache(_host, new MetadataOptions { FullTableStatus = true });
        var changes = 0;
        cache.Changed += (_, e) =>
        {
            if (e.Database == _database)
            {
                Interlocked.Increment(ref changes);
            }
        };

        var first = await cache.GetObjectsAsync(_database, Ct);
        Assert.False(first.HasStatistics);
        Assert.Equal(["t1", "v1"], first.Objects.Select(o => o.Ref.Name).Order(StringComparer.Ordinal));
        Assert.Null(first.Objects.Single(o => o.Ref.Name == "t1").SizeBytes);

        await cache.WaitForStatisticsAsync(_database);
        var withStats = cache.CachedObjects(_database)!;
        Assert.True(withStats.HasStatistics);
        Assert.True(withStats.Objects.Single(o => o.Ref.Name == "t1").SizeBytes > 0);
        Assert.Equal(2, Volatile.Read(ref changes));
        // The statistics ran on a job connection: the metadata one stayed free (primary, metadata and job open).
        Assert.Equal(3, _host.OwnConnectionIds.Count);
    }

    [Fact]
    public async Task SemFullTableStatusNaoPedeEstatisticasMasShiftF5Pede()
    {
        TestServer.RequireServer();
        var cache = new MetadataCache(_host, new MetadataOptions { FullTableStatus = false });

        await cache.GetObjectsAsync(_database, Ct);
        await cache.WaitForStatisticsAsync(_database);
        Assert.False(cache.CachedObjects(_database)!.HasStatistics);

        await cache.GetObjectsAsync(_database, Ct, forceStatistics: true);
        await cache.WaitForStatisticsAsync(_database);
        Assert.True(cache.CachedObjects(_database)!.HasStatistics);
    }

    [Fact]
    public async Task PedidosIguaisPartilhamAIdaAoServidor()
    {
        TestServer.RequireServer();
        var cache = new MetadataCache(_host, new MetadataOptions { FullTableStatus = false });

        var a = cache.GetObjectsAsync(_database, Ct);
        var b = cache.GetObjectsAsync(_database, Ct);

        Assert.Same(a, b);
        var first = await a;
        // The pass with the details arrives in the background and may already have replaced the entry: the objects are compared.
        var cached = cache.CachedObjects(_database);
        Assert.NotNull(cached);
        Assert.Equal(first.Objects.Select(o => o.Ref.Name), cached.Objects.Select(o => o.Ref.Name));
    }

    [Fact]
    public async Task InvalidateEsqueceERelê()
    {
        TestServer.RequireServer();
        var cache = new MetadataCache(_host, new MetadataOptions { FullTableStatus = false });
        await cache.GetObjectsAsync(_database, Ct);
        // The details arrive in the background: without waiting, the database could already be read with t2.
        await cache.WaitForStatisticsAsync(_database);
        await _host.RunPrimaryAsync((h, ct) => h.ExecuteNonQueryAsync($"CREATE TABLE `{_database}`.t2 (id INT)", ct), Ct);

        Assert.DoesNotContain(cache.CachedObjects(_database)!.Objects, o => o.Ref.Name == "t2");
        cache.Invalidate(_database);
        Assert.Null(cache.CachedObjects(_database));
        var again = await cache.GetObjectsAsync(_database, Ct);

        Assert.Contains(again.Objects, o => o.Ref.Name == "t2");
    }

    [Fact]
    public async Task WhitelistEHidePattern()
    {
        TestServer.RequireServer();
        var white = new MetadataCache(_host, new MetadataOptions { Databases = $" {_database} ;mysql;{_database}" });
        Assert.Equal([_database, "mysql"], await white.GetDatabasesAsync(Ct));

        var hidden = new MetadataCache(_host, new MetadataOptions { HideDatabasePattern = "^(information|performance)_schema$" });
        var names = await hidden.GetDatabasesAsync(Ct);
        Assert.Contains("mysql", names);
        Assert.DoesNotContain("information_schema", names);
        Assert.DoesNotContain("performance_schema", names);
    }

    [Fact]
    public async Task DefinicaoDaTabelaEmCache()
    {
        TestServer.RequireServer();
        var cache = new MetadataCache(_host, new MetadataOptions());
        var table = new DbObjectRef(_database, _database, "t1", DbObjectKind.Table);

        var first = await cache.GetTableAsync(table, Ct);
        var second = await cache.GetTableAsync(table, Ct);

        Assert.Same(first, second);
        Assert.Equal(["id", "v"], first.Columns.Select(c => c.Name));
        cache.InvalidateTable(table);
        Assert.Null(cache.CachedTable(table));
    }
}
