using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Eunomia.App.Query;
using Eunomia.Engine.Transfer;
using Eunomia.UI.Shell.Views;
using MySqlConnector;
using static Eunomia.UI.Headless.Tests.RealServer;

namespace Eunomia.UI.Headless.Tests;

/// <summary>Run SQL file through the main window (phase 8; appendix B §4.1–4.2): a large file on Load, progress and Resume.</summary>
public sealed class RunSqlFileTests
{
    private static async Task<string?> ScalarAsync(string cs, string sql)
    {
        await using var connection = new MySqlConnection(cs);
        await connection.OpenAsync();
        await using var cmd = new MySqlCommand(sql, connection);
        return Convert.ToString(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task LargeFileRunsDirectlyAndResumesAfterStoppingOnAnError()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "No test server.");
        var db = NewDatabaseName("eunomia_rf_");
        var dir = Path.Combine(Path.GetTempPath(), "eunomia-it", "runfile-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        await ExecAsync(cs, $"CREATE DATABASE `{db}`");
        try
        {
            var journal = new TransferJournal(Path.Combine(dir, "jobs"));
            var (window, context, session, connector, log) = CreateShell(cs, db, journal: journal);
            Assert.True(await window.ConnectAsync(window, session, null));
            // The database focused in the tree becomes the session's current one (the file runs in it).
            var model = window.Tree.Tree.Model!;
            var root = await WaitAsync(() => model.Roots.FirstOrDefault());
            window.Tree.Tree.FocusedNode = await WaitAsync(() => root.Children?.FirstOrDefault(n => n.Name == db));
            var open = connector.Find(session.Id)!;
            await WaitAsync(() => open.Host.CurrentDatabase == db ? "ok" : null);
            var dialogs = new List<RunSqlFileDialog>();
            window.RunTargetOverride = d =>
            {
                dialogs.Add(d);
                return d.Result;
            };

            // Over 5 MiB: Load asks and "Run file(s) directly" runs it without opening it.
            var big = new StringBuilder("CREATE TABLE t (id INT PRIMARY KEY, txt VARCHAR(200));\r\n");
            var row = 0;
            var statements = 0;
            // INSERTs of 100 rows: the server commits (fsync) per statement, and 58,000 took minutes.
            while (big.Length < SqlFile.LargeFileBytes + 100_000)
            {
                statements++;
                big.Append("INSERT INTO t VALUES ");
                for (var r = 0; r < 100; r++)
                {
                    row++;
                    big.Append(CultureInfo.InvariantCulture, $"{(r > 0 ? ", " : string.Empty)}({row}, '{new string('x', 80)} ação')");
                }
                big.Append(";\r\n");
            }
            var bigPath = Path.Combine(dir, "big.sql");
            await File.WriteAllTextAsync(bigPath, big.ToString(), new UTF8Encoding(false));
            IReadOnlyList<(string Path, long Bytes)>? asked = null;
            window.LargeFilesOverride = files =>
            {
                asked = files;
                return LargeFilesChoice.Run;
            };
            window.OpenSqlFilesOverride = () => [bigPath];
            window.LoadEncodingOverride = () => SqlFile.AutoDetect;
            Assert.True(await context.Commands.ExecuteAsync("query.load"));
            await window.RunningFiles;

            Assert.Single(asked!);
            Assert.Equal(row.ToString(CultureInfo.InvariantCulture), await ScalarAsync(cs, $"SELECT COUNT(*) FROM `{db}`.t"));
            Assert.Equal(string.Empty, window.SqlEditor.Text);
            Assert.Contains(log.Snapshot(), l => l.Text.Contains($"File {bigPath} executed, with {statements + 1} queries and {row} affected rows", StringComparison.Ordinal));
            var run = Assert.IsType<SqlFileRunWindow>(window.RunWindow);
            Assert.Equal("1 file(s) processed", run.Line1.Split(',')[0]);
            // The target dialog named the server and the database, and so does the progress window.
            var asked1 = Assert.Single(dialogs);
            Assert.Equal($"it ({session.Host}:{session.Port})", asked1.Server);
            Assert.Equal(db, asked1.DatabaseSelect.SelectedItem);
            Assert.Equal($"Server: it ({session.Host}:{session.Port}) · Database: {db}", run.Target);
            Assert.False(run.ResumeButton.IsVisible);
            run.Close();

            // "Run SQL file..." with Stop on errors: stops at the failing statement; Resume continues from it.
            var broken = Path.Combine(dir, "broken.sql");
            await File.WriteAllTextAsync(broken, "INSERT INTO u VALUES (1);\r\nINSERT INTO u VALUES (2);\r\nINSERT INTO missing_table VALUES (1);\r\nINSERT INTO u VALUES (3);\r\n");
            await ExecAsync(cs, $"CREATE TABLE `{db}`.u (a INT)");
            window.OpenSqlFilesOverride = () => [broken];
            var running = context.Commands.ExecuteAsync("query.runFile").AsTask();
            var stopped = await WaitAsync(() => window.RunWindow is { Running: false } w && w.ResumeButton.IsVisible ? w : null);
            Assert.StartsWith("Stopped at query #3:", stopped.Line1, StringComparison.Ordinal);
            Assert.Contains(log.Snapshot(), l => l.Text.Contains("Error in query #3 (line 3):", StringComparison.Ordinal));
            Assert.Single(journal.List());
            Assert.Equal("2", await ScalarAsync(cs, $"SELECT COUNT(*) FROM `{db}`.u"));

            await ExecAsync(cs, $"CREATE TABLE `{db}`.missing_table (a INT)");
            stopped.ResumeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => window.RunWindow is { Running: false } w && w.Line1.StartsWith("1 file(s) processed", StringComparison.Ordinal) ? "ok" : null);
            await running;
            Assert.Equal("3", await ScalarAsync(cs, $"SELECT COUNT(*) FROM `{db}`.u"));
            Assert.Equal("1", await ScalarAsync(cs, $"SELECT COUNT(*) FROM `{db}`.missing_table"));
            Assert.Empty(journal.List());
            window.RunWindow?.Close();
            window.Close();
        }
        finally
        {
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`");
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// A dump that creates and selects its own database: the dialog says so and lets it run in another database
    /// (its USE and CREATE DATABASE skipped) or as in the file; the progress window follows the USE; the session's
    /// current database never changes; Cancel runs nothing.
    /// </summary>
    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task ADumpOfAnotherDatabaseRunsInTheChosenOneOrAsInTheFile()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "No test server.");
        var db = NewDatabaseName("eunomia_rt_");
        var db2 = db + "_b";
        var src = db + "_src";
        var dir = Path.Combine(Path.GetTempPath(), "eunomia-it", "runtarget-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        await ExecAsync(cs, $"CREATE DATABASE `{db}`");
        await ExecAsync(cs, $"CREATE DATABASE `{db2}`");
        try
        {
            var (window, context, session, connector, _) = CreateShell(cs, $"{db};{db2}");
            Assert.True(await window.ConnectAsync(window, session, null));
            var model = window.Tree.Tree.Model!;
            var root = await WaitAsync(() => model.Roots.FirstOrDefault());
            window.Tree.Tree.FocusedNode = await WaitAsync(() => root.Children?.FirstOrDefault(n => n.Name == db));
            var open = connector.Find(session.Id)!;
            await WaitAsync(() => open.Host.CurrentDatabase == db ? "ok" : null);

            var dump = Path.Combine(dir, "dump.sql");
            await File.WriteAllTextAsync(dump,
                $"/*!40014 SET FOREIGN_KEY_CHECKS=0 */;\nCREATE DATABASE IF NOT EXISTS `{src}`;\nUSE `{src}`;\nCREATE TABLE t (id INT, db VARCHAR(64));\nINSERT INTO t VALUES (1, DATABASE());\n");
            window.OpenSqlFilesOverride = () => [dump];
            window.LoadEncodingOverride = () => SqlFile.AutoDetect;
            var server = $"it ({session.Host}:{session.Port})";

            // Cancel in the dialog: nothing runs, no progress window.
            RunSqlFileDialog? asked = null;
            window.RunTargetOverride = d =>
            {
                asked = d;
                return null;
            };
            Assert.True(await context.Commands.ExecuteAsync("query.runFile"));
            Assert.NotNull(asked);
            Assert.Null(window.RunWindow);
            Assert.Equal("0", await ScalarAsync(cs, $"SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = '{src}'"));

            // What the dialog shows: the server, the focused database (any of the session's can be chosen), the file's USE.
            Assert.Equal(server, asked.Server);
            Assert.Equal(db, asked.DatabaseSelect.SelectedItem);
            Assert.Contains(db2, asked.DatabaseSelect.Items);
            Assert.True(asked.AsFileOption.IsChecked);
            Assert.True(asked.StayOption.IsEnabled);
            Assert.Equal("dump.sql", asked.Files);

            // Everything in db2: the rows land there, the file's database is never created.
            window.RunTargetOverride = d =>
            {
                d.DatabaseSelect.SelectedItem = db2;
                d.StayOption.IsChecked = true;
                return d.Result;
            };
            Assert.True(await context.Commands.ExecuteAsync("query.runFile"));
            await window.RunningFiles;
            var run = Assert.IsType<SqlFileRunWindow>(window.RunWindow);
            Assert.StartsWith("1 file(s) processed", run.Line1, StringComparison.Ordinal);
            Assert.Equal($"Server: {server} · Database: {db2}", run.Target);
            Assert.Equal(db2, await ScalarAsync(cs, $"SELECT db FROM `{db2}`.t"));
            Assert.Equal("0", await ScalarAsync(cs, $"SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = '{src}'"));
            Assert.Equal(db, open.Host.CurrentDatabase);
            run.Close();

            // As in the file: it creates its database and runs there; the progress window follows the USE.
            window.RunTargetOverride = d => d.Result;
            Assert.True(await context.Commands.ExecuteAsync("query.runFile"));
            await window.RunningFiles;
            run = Assert.IsType<SqlFileRunWindow>(window.RunWindow);
            await WaitAsync(() => run.Target.EndsWith("Database: " + src, StringComparison.Ordinal) ? "ok" : null);
            Assert.Equal(src, await ScalarAsync(cs, $"SELECT db FROM `{src}`.t"));
            Assert.Equal("0", await ScalarAsync(cs, $"SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = '{db}'"));
            Assert.Equal(db, open.Host.CurrentDatabase);
            run.Close();
            window.Close();
        }
        finally
        {
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`");
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db2}`");
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{src}`");
            Directory.Delete(dir, recursive: true);
        }
    }
}
