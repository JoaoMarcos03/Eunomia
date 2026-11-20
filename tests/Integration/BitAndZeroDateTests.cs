using System.Globalization;
using Eunomia.Drivers.Abstractions;
using Eunomia.Drivers.MySql;
using Eunomia.Engine.Results;

namespace Eunomia.Integration.Tests;

/// <summary>BIT(64) with all bits set (it overflowed a signed conversion) and partial zero dates (shown as 0000-00-00).</summary>
public sealed class BitAndZeroDateTests : IAsyncLifetime
{
    private IDbConnectionHandle _db = null!;
    private string _database = string.Empty;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        if (TestServer.ConnectionString is null)
        {
            return;
        }
        _db = await TestServer.OpenAsync();
        _database = await TestServer.CreateDatabaseAsync(_db);
        await ((MySqlConnectionHandle)_db).ExecuteNonQueryAsync($"USE `{_database}`", Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_db is not null)
        {
            await _db.DisposeAsync();
            await using var cleanup = await TestServer.OpenAsync();
            await TestServer.DropDatabaseAsync(cleanup, _database);
        }
    }

    [Fact]
    public async Task ShowsAllBitsAndTheServerDate()
    {
        TestServer.RequireServer();
        await TestServer.RunAsync(_db,
            "SET SESSION sql_mode = ''",
            "CREATE TABLE t (id INT PRIMARY KEY, b BIT(64) NULL, d DATE NULL, dt DATETIME NULL, dt3 DATETIME(3) NULL)",
            """
            INSERT INTO t VALUES
              (1, b'1111111111111111111111111111111111111111111111111111111111111111', '2024-00-00', '2024-00-00 10:11:12', '0000-01-15 01:02:03.456'),
              (2, b'101', '0000-01-15', '0000-00-00 00:00:00', NULL)
            """);

        var events = await TestServer.RunAsync(_db, "SELECT b, d, dt, dt3 FROM t ORDER BY id");

        var block = Assert.Single(events.OfType<RowBlockReceived>()).Block;
        var fmt = new DisplayFormatter(CultureInfo.InvariantCulture, thousandsSeparator: false);
        Assert.Equal(new string('1', 64), fmt.Format(block, 0, 0));
        Assert.EndsWith("101", fmt.Format(block, 1, 0), StringComparison.Ordinal);
        Assert.Equal("2024-00-00", fmt.Format(block, 0, 1));
        Assert.Equal("0000-01-15", fmt.Format(block, 1, 1));
        Assert.Equal("2024-00-00 10:11:12", fmt.Format(block, 0, 2));
        Assert.Equal("0000-00-00 00:00:00", fmt.Format(block, 1, 2));
        Assert.Equal("0000-01-15 01:02:03.456", fmt.Format(block, 0, 3));
    }
}
