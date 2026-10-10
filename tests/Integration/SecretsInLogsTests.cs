using Eunomia.App.Query;
using Eunomia.App.Settings;
using Eunomia.Drivers.MySql;
using Eunomia.Engine.Execution;
using Eunomia.Engine.Sql.Splitting;
using Eunomia.Presentation.Logging;
using MySqlConnector;

namespace Eunomia.Integration.Tests;

/// <summary>
/// Secrets kept out of the logs (phase 12; section 23.2): the password of a user created through SQL, the one of a successful
/// connection and the one of a refused connection do not show in the SQL log or in the error messages.
/// </summary>
public sealed class SecretsInLogsTests
{
    private const string Secret = "Sgr-7f3a-Test!";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PasswordsNeverReachTheSqlLogOrTheErrors()
    {
        TestServer.RequireServer();
        var builder = new MySqlConnectionStringBuilder(TestServer.ConnectionString!);
        var log = new SqlLogBuffer(new LogOptions { InternalSql = true, Script = true, Info = true, Debug = true, MaxLineLength = 0 });
        var user = "eunomia_sec_" + Guid.NewGuid().ToString("N")[..6];
        var errors = new List<string>();

        await using (var admin = new SessionHost(new SessionConnectionSettings("admin", TestServer.Options()) { KeepAlive = null }, new SessionServices
        {
            Provider = new MySqlProvider(),
            ResolveSecret = (_, _) => ValueTask.FromResult(builder.Password),
            Log = new SessionLogAdapter(log),
            SplitScript = s => [.. SqlSplitter.Split(s).Select(x => x.Text)],
        }))
        {
            await admin.ConnectAsync(Ct);
            var runner = new QueryRunner(admin);
            Task Run(string sql, CancellationToken ct) => runner.RunAsync(sql, SqlSplitter.Split(sql), new QueryRunOptions(), ct);
            // As in a query tab: the SQL goes to the log as user SQL.
            await Run($"CREATE USER '{user}'@'%' IDENTIFIED BY '{Secret}'", Ct);
            await Run($"ALTER USER '{user}'@'%' IDENTIFIED BY '{Secret}'", Ct);
            try
            {
                // Successful and refused connections with the new user.
                foreach (var password in new[] { Secret, Secret + "x" })
                {
                    var options = TestServer.Options() with { User = user, Password = new Eunomia.Drivers.Abstractions.SecretReference("pw") };
                    await using var host = new SessionHost(new SessionConnectionSettings("u", options) { KeepAlive = null }, new SessionServices
                    {
                        Provider = new MySqlProvider(),
                        ResolveSecret = (_, _) => ValueTask.FromResult(password),
                        Log = new SessionLogAdapter(log),
                    });
                    try
                    {
                        await host.ConnectAsync(Ct);
                        await host.RunPrimaryAsync((h, ct) => h.ExecuteScalarAsync("SELECT CURRENT_USER()", ct), Ct);
                    }
                    catch (Exception e)
                    {
                        errors.Add(e.ToString());
                    }
                }
            }
            finally
            {
                await Run($"DROP USER '{user}'@'%'", CancellationToken.None);
            }
        }

        var text = log.ToText();
        Assert.Contains("IDENTIFIED BY '***'", text, StringComparison.Ordinal);
        Assert.True(errors.Count == 1, string.Join(" | ", errors.Select(e => e.Split('\n')[0])));
        Assert.Contains("Access denied", errors[0], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
        Assert.All(errors, e => Assert.DoesNotContain(Secret, e, StringComparison.Ordinal));
    }
}
