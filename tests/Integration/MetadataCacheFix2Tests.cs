using Eunomia.Drivers.MySql;
using Eunomia.Engine.Execution;
using Eunomia.Engine.Metadata;
using MySqlConnector;

namespace Eunomia.Integration.Tests;

/// <summary>Statistics read before a refresh of the database do not come back into the cache.</summary>
public sealed class MetadataCacheFix2Tests : IAsyncLifetime
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
        _database = "eunomia_md2_" + Guid.NewGuid().ToString("N")[..8];
        await _host.RunPrimaryAsync(async (h, ct) =>
        {
            await h.ExecuteNonQueryAsync($"CREATE DATABASE `{_database}`", ct);
            await h.ExecuteNonQueryAsync($"CREATE TABLE `{_database}`.t1 (id INT PRIMARY KEY)", ct);
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
    public async Task InvalidateDuringStatisticsDoesNotBringTheOldListBack()
    {
        TestServer.RequireServer();
        var cache = new MetadataCache(_host, new MetadataOptions { FullTableStatus = true });
        await cache.GetObjectsAsync(_database, Ct);
        var statistics = cache.WaitForStatisticsAsync(_database);

        cache.Invalidate(_database);
        await statistics;

        Assert.Null(cache.CachedObjects(_database));
    }
}
