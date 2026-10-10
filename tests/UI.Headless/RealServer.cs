using System.Net.Sockets;
using Avalonia.Threading;
using Eunomia.App.Secrets;
using Eunomia.App.Sessions;
using Eunomia.App.Settings;
using Eunomia.Drivers.MySql;
using Eunomia.Presentation.Logging;
using Eunomia.UI.Shell;
using MySqlConnector;

namespace Eunomia.UI.Headless.Tests;

/// <summary>Shell connected to the test server (tests/Integration/Start-TestServer.ps1 or <c>EUNOMIA_IT_MYSQL</c>).</summary>
internal static class RealServer
{
    public static string? ConnectionString()
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
    /// The Start-TestServer.ps1 file stays after the server stops (for example, after a restart). Without this
    /// check, the tests tried to connect, the connection error dialog waited on the shared UI thread and the
    /// whole run stopped. With <c>EUNOMIA_IT_MYSQL</c> (CI) there is no check: a server that is down has to fail.
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

    /// <summary>Runs the UI jobs until <paramref name="probe"/> returns a value (10 s by default).</summary>
    public static async Task<T> WaitAsync<T>(Func<T?> probe, TimeSpan? timeout = null)
        where T : class
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            if (probe() is { } value)
            {
                return value;
            }
            await Task.Delay(25);
        }
        throw new TimeoutException("Condition not reached.");
    }

    public static async Task ExecAsync(string connectionString, string sql)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var cmd = new MySqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }

    public static string NewDatabaseName(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    /// <summary>Main window with a saved session for the test server (not yet connected).</summary>
    public static Shell CreateShell(string connectionString, string databases, AppSettings? settings = null, IAppDatabase? appDatabase = null, uint? color = null, Eunomia.Engine.Transfer.TransferJournal? journal = null)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString);
        settings ??= new AppSettings();
        settings.MainWindow.Width = 1280;
        settings.MainWindow.Height = 800;
        var catalog = new SessionCatalog();
        var session = new SessionDefinition { Name = "it", Host = builder.Server, Port = (int)builder.Port, User = builder.UserID, PasswordSaved = true, Color = color, Databases = databases };
        catalog.Sessions.Add(session);
        var secrets = new MemorySecretStore();
        secrets.Set(SecretKeys.Password(session.Id), builder.Password);
        var log = new SqlLogBuffer(settings.Log);
        var connector = new SessionConnector(new MySqlProvider(), secrets, null, new SessionLogAdapter(log));
        var context = new ShellContext(settings, new TestShell.MemorySettingsStore(settings), appDatabase, log)
        {
            Sessions = new ShellSessions { Catalog = catalog, Store = new NullSessionStore(), Secrets = secrets, Connector = connector },
            TransferJournal = journal,
        };
        var window = new MainWindow(context);
        window.Show();
        return new Shell(window, context, session, connector, log);
    }

    public sealed record Shell(MainWindow Window, ShellContext Context, SessionDefinition Session, SessionConnector Connector, SqlLogBuffer Log);

    private sealed class NullSessionStore : ISessionStore
    {
        public SessionCatalog Load() => new();

        public void Save(SessionCatalog catalog)
        {
        }
    }
}
