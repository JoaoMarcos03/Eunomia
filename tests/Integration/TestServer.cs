using System.Net.Sockets;
using Eunomia.Drivers.Abstractions;
using Eunomia.Drivers.MySql;
using MySqlConnector;

namespace Eunomia.Integration.Tests;

/// <summary>
/// Test server: connection string in <c>EUNOMIA_IT_MYSQL</c> or in the file written by
/// <c>Start-TestServer.ps1</c>. Without a server, the tests are skipped (never false greens).
/// </summary>
internal static class TestServer
{
    public static string? ConnectionString { get; } = Find();

    private static string? Find()
    {
        var env = Environment.GetEnvironmentVariable("EUNOMIA_IT_MYSQL");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }
        var file = Path.Combine(Path.GetTempPath(), "eunomia-it", "connection.txt");
        return File.Exists(file) && File.ReadAllText(file).Trim() is var local && Reachable(local) ? local : null;
    }

    /// <summary>
    /// The Start-TestServer.ps1 file stays after the server stops (for example, after a restart): without a server
    /// answering, the tests are skipped instead of failing to connect. With <c>EUNOMIA_IT_MYSQL</c> (CI) there is no
    /// check: a server that is down has to fail.
    /// </summary>
    private static bool Reachable(string connectionString)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString);
        try
        {
            using var client = new TcpClient();
            client.Connect(builder.Server, (int)builder.Port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public static void RequireServer() =>
        Assert.SkipWhen(ConnectionString is null, "No test server: run tests/Integration/Start-TestServer.ps1 or set EUNOMIA_IT_MYSQL.");

    public static ConnectionOptions Options(string? database = null)
    {
        RequireServer();
        var b = new MySqlConnectionStringBuilder(ConnectionString!);
        return new ConnectionOptions(b.Server, (int)b.Port, b.UserID, b.Password.Length > 0 ? new SecretReference("pw") : null, database, TlsMode.Preferred);
    }

    public static async Task<IDbConnectionHandle> OpenAsync(string? database = null)
    {
        var password = new MySqlConnectionStringBuilder(ConnectionString!).Password;
        return await new MySqlProvider().Connections.OpenAsync(Options(database), (_, _) => ValueTask.FromResult(password), TestContext.Current.CancellationToken);
    }

    /// <summary>A new, unique database, dropped at the end.</summary>
    public static async Task<string> CreateDatabaseAsync(IDbConnectionHandle handle)
    {
        var name = "eunomia_it_" + Guid.NewGuid().ToString("N")[..10];
        await ((MySqlConnectionHandle)handle).ExecuteNonQueryAsync($"CREATE DATABASE `{name}` CHARACTER SET utf8mb4", TestContext.Current.CancellationToken);
        return name;
    }

    public static Task DropDatabaseAsync(IDbConnectionHandle handle, string name) =>
        ((MySqlConnectionHandle)handle).ExecuteNonQueryAsync($"DROP DATABASE IF EXISTS `{name}`", CancellationToken.None);

    public static async Task<List<ResultEvent>> RunAsync(IDbConnectionHandle handle, params string[] statements) =>
        await RunAsync(handle, new ExecuteOptions(), statements);

    public static async Task<List<ResultEvent>> RunAsync(IDbConnectionHandle handle, ExecuteOptions options, params string[] statements)
    {
        var events = new List<ResultEvent>();
        await foreach (var e in handle.Executor.ExecuteAsync(new SqlBatch(statements), options, TestContext.Current.CancellationToken))
        {
            events.Add(e);
        }
        return events;
    }
}
