using System.Globalization;
using System.Text;
using Eunomia.App.Transfer;
using Eunomia.Drivers.MySql;
using Eunomia.Engine.Execution;
using Eunomia.Engine.Transfer;
using MySqlConnector;

namespace Eunomia.Integration.Tests;

/// <summary>Streaming Run SQL file (phase 8; 27.1 #21; PR-14): blocks, DELIMITER, batch, errors and resume.</summary>
public sealed class SqlFileTests : IAsyncLifetime
{
    private SessionHost _host = null!;
    private string _database = string.Empty;
    private string _directory = string.Empty;
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
        _database = "eunomia_sf_" + Guid.NewGuid().ToString("N")[..8];
        _directory = Path.Combine(Path.GetTempPath(), "eunomia-it", "sqlfile-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_directory);
        await ExecAsync($"CREATE DATABASE `{_database}` CHARACTER SET utf8mb4");
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await ExecAsync($"DROP DATABASE IF EXISTS `{_database}`");
            await _host.DisposeAsync();
        }
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private Task ExecAsync(string sql) => _host.RunPrimaryAsync((h, ct) => h.ExecuteNonQueryAsync(sql, ct), CancellationToken.None);

    private async Task<string?> ScalarAsync(string sql) =>
        Convert.ToString(await _host.RunPrimaryAsync((h, ct) => h.ExecuteScalarAsync(sql, ct), Ct), CultureInfo.InvariantCulture);

    /// <summary>Script with DELIMITER, comments, text with accents and quotes, and <paramref name="rows"/> INSERTs.</summary>
    private static string Script(int rows, string? broken = null)
    {
        var sb = new StringBuilder();
        sb.Append("-- header; with a semicolon\r\n/*!40101 SET NAMES utf8mb4 */;\r\n");
        sb.Append("CREATE TABLE t (id INT PRIMARY KEY, txt VARCHAR(100)) ENGINE=InnoDB;\r\n");
        sb.Append("DELIMITER //\r\nCREATE PROCEDURE p()\r\nBEGIN\r\n  SELECT 'a;b';\r\n  SELECT 2;\r\nEND//\r\nDELIMITER ;\r\n");
        for (var i = 1; i <= rows; i++)
        {
            if (broken is not null && i == rows / 2)
            {
                sb.Append(broken).Append("\r\n");
            }
            sb.Append(CultureInfo.InvariantCulture, $"INSERT INTO t VALUES ({i}, 'ação \"{i}\"; it''s # not -- a comment');\r\n");
        }
        sb.Append("# end\r\n");
        return sb.ToString();
    }

    private string Write(string name, string text, bool bom = false)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, text, new UTF8Encoding(bom));
        return path;
    }

    private SqlFileRequest Request(string path) => new() { Path = path, Database = _database, Session = "it" };

    [Fact]
    public async Task SmallBlocksLoseNeitherTheDelimiterNorTheAccents()
    {
        TestServer.RequireServer();
        var path = Write("script.sql", Script(300), bom: true);
        var progress = new List<SqlFileProgress>();
        // 37 bytes: the boundaries fall inside the DELIMITER, the strings and the 2-byte characters.
        var result = await new SqlFileJob(_host, Request(path)) { ChunkSize = 37 }.RunAsync(new SyncProgress<SqlFileProgress>(progress.Add), Ct);

        Assert.True(result.Completed);
        Assert.Equal(0, result.Errors);
        Assert.Equal(303, result.Statements);
        Assert.Equal(300, result.AffectedRows);
        Assert.Equal(new FileInfo(path).Length, result.BytesRead);
        Assert.Equal("300", await ScalarAsync($"SELECT COUNT(*) FROM `{_database}`.t"));
        Assert.Equal("ação \"7\"; it's # not -- a comment", await ScalarAsync($"SELECT txt FROM `{_database}`.t WHERE id = 7"));
        Assert.Equal("1", await ScalarAsync($"SELECT COUNT(*) FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = '{_database}' AND ROUTINE_NAME = 'p'"));
        Assert.NotEmpty(progress);
    }

    [Fact]
    public async Task BatchInPacketsGivesTheSameResult()
    {
        TestServer.RequireServer();
        var path = Write("batch.sql", Script(2000));
        var result = await new SqlFileJob(_host, Request(path) with { BatchInOneGo = true }) { ChunkSize = 4096 }.RunAsync(null, Ct);

        Assert.True(result.Completed);
        Assert.Equal(2003, result.Statements);
        Assert.Equal("2000", await ScalarAsync($"SELECT COUNT(*) FROM `{_database}`.t"));
        Assert.Equal("1", await ScalarAsync($"SELECT COUNT(*) FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = '{_database}' AND ROUTINE_NAME = 'p'"));
    }

    [Fact]
    public async Task StopsOnTheErrorAndResumesAtTheFailedStatement()
    {
        TestServer.RequireServer();
        var path = Write("broken.sql", Script(100, broken: "INSERT INTO missing_table VALUES (1);"));
        var journal = new TransferJournal(Path.Combine(_directory, "jobs"));

        var first = await new SqlFileJob(_host, Request(path), journal) { ChunkSize = 64 }.RunAsync(null, Ct);
        Assert.True(first.StoppedOnError);
        var error = Assert.Single(first.ErrorList);
        // Statements: SET NAMES, CREATE, PROCEDURE, 49 INSERTs; the 53rd fails. Line: 2 for the header + 1 + 7 for the DELIMITER + 49 + 1.
        Assert.Equal(53, error.Statement);
        Assert.Equal(60, error.Line);
        Assert.Contains("missing_table", error.Message, StringComparison.Ordinal);
        Assert.Equal("49", await ScalarAsync($"SELECT COUNT(*) FROM `{_database}`.t"));
        var entry = Assert.Single(journal.List());
        Assert.Equal(52, entry.Count);
        Assert.Equal(";", entry.Delimiter);

        // With the problem fixed, resuming runs the statement that failed and the rest, without repeating the earlier ones.
        await ExecAsync($"CREATE TABLE `{_database}`.missing_table (a INT)");
        var resumed = await new SqlFileJob(_host, entry, journal) { ChunkSize = 64 }.RunAsync(null, Ct);
        Assert.True(resumed.Completed);
        Assert.Equal(first.JobId, resumed.JobId);
        Assert.Equal(104, resumed.Statements);
        Assert.Equal("100", await ScalarAsync($"SELECT COUNT(*) FROM `{_database}`.t"));
        Assert.Equal("1", await ScalarAsync($"SELECT COUNT(*) FROM `{_database}`.missing_table"));
        Assert.Empty(journal.List());

        // Without Stop on errors, the errors are counted and the file runs to the end.
        await ExecAsync($"DROP TABLE `{_database}`.t, `{_database}`.missing_table");
        await ExecAsync($"DROP PROCEDURE `{_database}`.p");
        var lenient = await new SqlFileJob(_host, Request(path) with { StopOnErrors = false }).RunAsync(null, Ct);
        Assert.False(lenient.StoppedOnError);
        Assert.Equal(1, lenient.Errors);
        Assert.Equal(104, lenient.Statements);
        Assert.Equal("100", await ScalarAsync($"SELECT COUNT(*) FROM `{_database}`.t"));
    }

    /// <summary>
    /// Dump with its own database and settings (like mysqldump and the Eunomia export): resuming replays the file's USE and
    /// SETs, so the rest runs in the file's database, with FOREIGN_KEY_CHECKS=0, the time zone and the variables. A journal
    /// from an older version (without the state) reads them from the start of the file.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumeReplaysTheFileUseAndSessionSets(bool olderJournal)
    {
        TestServer.RequireServer();
        var other = _database + "_use";
        var path = Write("dump.sql", $"""
            /*!40014 SET @OLD_FOREIGN_KEY_CHECKS=@@FOREIGN_KEY_CHECKS, FOREIGN_KEY_CHECKS=0 */;
            /*!40103 SET TIME_ZONE='+05:00' */;
            SET GLOBAL eunomia_not_a_variable = 1;
            CREATE DATABASE `{other}`;
            USE `{other}`;
            SET @marker = 'from the file';
            CREATE TABLE child (id INT PRIMARY KEY, parent INT, CONSTRAINT fk FOREIGN KEY (parent) REFERENCES parent (id)) ENGINE=InnoDB;
            INSERT INTO child VALUES (1, 99);
            INSERT INTO missing_table VALUES (1);
            INSERT INTO child VALUES (2, 98);
            CREATE TABLE seen (marker VARCHAR(50), tz VARCHAR(10), fk INT, db VARCHAR(64));
            INSERT INTO seen SELECT @marker, @@time_zone, @@foreign_key_checks, DATABASE();
            """);
        var journal = new TransferJournal(Path.Combine(_directory, "jobs-state"));
        try
        {
            // The SET GLOBAL fails (unknown variable) and stops the job; it is never part of the session state.
            var first = await new SqlFileJob(_host, Request(path), journal) { ChunkSize = 64 }.RunAsync(null, Ct);
            Assert.True(first.StoppedOnError);
            Assert.Contains("eunomia_not_a_variable", first.ErrorList[0].Message, StringComparison.OrdinalIgnoreCase);

            // Resumes past the SET GLOBAL (statement 3), as a user would after reading the error.
            var entry = Assert.Single(journal.List());
            Assert.Equal(0, entry.Errors);
            journal.Save(entry = SkipStatement(path, entry));
            var second = await new SqlFileJob(_host, entry, journal) { ChunkSize = 64 }.RunAsync(null, Ct);
            Assert.True(second.StoppedOnError);
            Assert.Contains("missing_table", second.ErrorList[0].Message, StringComparison.Ordinal);
            entry = Assert.Single(journal.List());
            Assert.Equal(8, entry.Count);
            // The statement that stopped the job runs again when it resumes: its error is not counted twice.
            Assert.Equal(0, entry.Errors);
            Assert.True(entry.Elapsed > TimeSpan.Zero);
            Assert.Equal(
                [
                    "/*!40014 SET @OLD_FOREIGN_KEY_CHECKS=@@FOREIGN_KEY_CHECKS, FOREIGN_KEY_CHECKS=0 */",
                    "/*!40103 SET TIME_ZONE='+05:00' */",
                    $"USE `{other}`",
                    "SET @marker = 'from the file'",
                ],
                entry.SessionState);
            if (olderJournal)
            {
                journal.Save(entry = entry with { SessionState = null });
            }

            // Resumes on a new job connection whose default database is the one chosen in the dialog.
            await ExecAsync($"CREATE TABLE `{other}`.missing_table (a INT)");
            var progress = new List<SqlFileProgress>();
            var resumed = await new SqlFileJob(_host, entry, journal) { ChunkSize = 64 }.RunAsync(new SyncProgress<SqlFileProgress>(progress.Add), Ct);

            Assert.True(resumed.Completed, string.Join("; ", resumed.ErrorList.Select(e => e.Message)));
            // The run window shows the database the file selected, not the one chosen in the dialog.
            Assert.Equal(other, progress[^1].Database);
            Assert.Equal(0, resumed.Errors);
            Assert.True(resumed.Elapsed >= entry.Elapsed);
            Assert.Equal("2", await ScalarAsync($"SELECT COUNT(*) FROM `{other}`.child"));
            Assert.Equal("from the file|+05:00|0|" + other, await ScalarAsync($"SELECT CONCAT_WS('|', marker, tz, fk, db) FROM `{other}`.seen"));
            Assert.Equal("0", await ScalarAsync($"SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = '{_database}'"));
        }
        finally
        {
            await ExecAsync($"DROP DATABASE IF EXISTS `{other}`");
        }
    }

    /// <summary>
    /// "Run everything in the chosen database": a dump of another database (DROP, CREATE, USE, ALTER DATABASE) is restored
    /// into the session's database; the file's statements on databases are counted but touch nothing.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StayInDatabaseRestoresADumpOfAnotherDatabaseInTheChosenOne(bool batch)
    {
        TestServer.RequireServer();
        var other = _database + "_src";
        var fresh = _database + "_new";
        await ExecAsync($"CREATE DATABASE `{other}` CHARACTER SET utf8mb4");
        await ExecAsync($"CREATE TABLE `{other}`.keep (id INT)");
        var path = Write("other.sql", $"""
            /*!40014 SET @OLD_FOREIGN_KEY_CHECKS=@@FOREIGN_KEY_CHECKS, FOREIGN_KEY_CHECKS=0 */;
            /*!40000 DROP DATABASE IF EXISTS `{other}`*/;
            CREATE DATABASE IF NOT EXISTS `{fresh}` /*!40100 DEFAULT CHARACTER SET latin1 */;
            USE `{fresh}`;
            ALTER DATABASE `{other}` CHARACTER SET latin1;
            CREATE TABLE t (id INT PRIMARY KEY, db VARCHAR(64), fk INT);
            INSERT INTO t VALUES (1, DATABASE(), @@foreign_key_checks);
            /*!40000 USE `{other}` */;
            INSERT INTO t VALUES (2, DATABASE(), @@foreign_key_checks);
            """);
        try
        {
            var progress = new List<SqlFileProgress>();
            var result = await new SqlFileJob(_host, Request(path) with { StayInDatabase = true, BatchInOneGo = batch }) { ChunkSize = 64 }
                .RunAsync(new SyncProgress<SqlFileProgress>(progress.Add), Ct);

            Assert.True(result.Completed, string.Join("; ", result.ErrorList.Select(e => e.Message)));
            Assert.Equal(9, result.Statements);
            Assert.Equal(_database, progress[^1].Database);
            Assert.Equal($"1|{_database}|0,2|{_database}|0", await ScalarAsync($"SELECT GROUP_CONCAT(CONCAT_WS('|', id, db, fk) ORDER BY id) FROM `{_database}`.t"));
            // Nothing of the file reached the databases it names.
            Assert.Equal("1", await ScalarAsync($"SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = '{other}'"));
            Assert.Equal("utf8mb4", await ScalarAsync($"SELECT DEFAULT_CHARACTER_SET_NAME FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = '{other}'"));
            Assert.Equal("0", await ScalarAsync($"SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = '{fresh}'"));
        }
        finally
        {
            await ExecAsync($"DROP DATABASE IF EXISTS `{other}`");
            await ExecAsync($"DROP DATABASE IF EXISTS `{fresh}`");
        }
    }

    /// <summary>The choice is kept in the journal: a resumed job (also from an older journal) stays in the chosen database.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StayInDatabaseSurvivesAResume(bool olderJournal)
    {
        TestServer.RequireServer();
        var other = _database + "_src";
        var path = Write("stay.sql", $"""
            CREATE DATABASE IF NOT EXISTS `{other}`;
            USE `{other}`;
            SET @marker = 'kept';
            CREATE TABLE t (id INT PRIMARY KEY, db VARCHAR(64), marker VARCHAR(20));
            INSERT INTO t VALUES (1, DATABASE(), @marker);
            INSERT INTO missing_table VALUES (1);
            INSERT INTO t VALUES (2, DATABASE(), @marker);
            """);
        var journal = new TransferJournal(Path.Combine(_directory, "jobs-stay"));
        try
        {
            var first = await new SqlFileJob(_host, Request(path) with { StayInDatabase = true }, journal) { ChunkSize = 64 }.RunAsync(null, Ct);
            Assert.True(first.StoppedOnError);
            var entry = Assert.Single(journal.List());
            Assert.Equal("true", entry.Options["stayInDatabase"]);
            Assert.True(SqlFileJob.RequestOf(entry).StayInDatabase);
            Assert.Equal(["SET @marker = 'kept'"], entry.SessionState);
            if (olderJournal)
            {
                journal.Save(entry = entry with { SessionState = null });
            }

            await ExecAsync($"CREATE TABLE `{_database}`.missing_table (a INT)");
            var resumed = await new SqlFileJob(_host, entry, journal) { ChunkSize = 64 }.RunAsync(null, Ct);

            Assert.True(resumed.Completed, string.Join("; ", resumed.ErrorList.Select(e => e.Message)));
            Assert.Equal($"1|{_database}|kept,2|{_database}|kept", await ScalarAsync($"SELECT GROUP_CONCAT(CONCAT_WS('|', id, db, marker) ORDER BY id) FROM `{_database}`.t"));
            Assert.Equal("0", await ScalarAsync($"SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = '{other}'"));
        }
        finally
        {
            await ExecAsync($"DROP DATABASE IF EXISTS `{other}`");
        }
    }

    [Fact]
    public async Task StayInDatabaseNeedsADatabase()
    {
        await using var host = new SessionHost(new SessionConnectionSettings("x", TestServer.Options()), new SessionServices { Provider = new MySqlProvider(), ResolveSecret = (_, _) => ValueTask.FromResult(string.Empty) });
        var request = new SqlFileRequest { Path = "x.sql", StayInDatabase = true };

        Assert.Throws<ArgumentException>(() => new SqlFileJob(host, request));
        _ = new SqlFileJob(host, request with { Database = "shop" });
    }

    /// <summary>Moves the journal past the statement at its resume point (the "Skip" of a stopped job).</summary>
    private static TransferJournalEntry SkipStatement(string path, TransferJournalEntry entry)
    {
        var text = File.ReadAllText(path);
        var end = text.IndexOf(';', (int)entry.CharOffset) + 1;
        var skipped = text[(int)entry.CharOffset..end];
        return entry with
        {
            Offset = entry.Offset + Encoding.UTF8.GetByteCount(skipped),
            CharOffset = end,
            Line = entry.Line + skipped.Count(c => c == '\n'),
            Count = entry.Count + 1,
        };
    }

    [Fact]
    public void FindsTheDatabasesTheFileSelects()
    {
        var directory = Path.Combine(Path.GetTempPath(), "eunomia-sf-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        try
        {
            var dump = Path.Combine(directory, "dump.sql");
            File.WriteAllText(dump, "-- USE commented;\nCREATE DATABASE IF NOT EXISTS `shop`;\nUSE `shop`;\nINSERT INTO t VALUES ('USE fake');\n/*!40000 USE archive */;\nUSE `shop`;\n", new UTF8Encoding(true));
            var plain = Path.Combine(directory, "plain.sql");
            File.WriteAllText(plain, "INSERT INTO t VALUES (1);");

            Assert.Equal(["shop", "archive"], SqlFileJob.DatabasesSelectedBy(dump));
            Assert.Empty(SqlFileJob.DatabasesSelectedBy(plain));
            // Only the start of the file is read.
            Assert.Equal(["shop"], SqlFileJob.DatabasesSelectedBy(dump, maxBytes: 80));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ChangedFileDoesNotResume()
    {
        TestServer.RequireServer();
        var path = Write("changed.sql", Script(10, broken: "SELECT * FROM missing_table;"));
        var journal = new TransferJournal(Path.Combine(_directory, "jobs2"));
        var first = await new SqlFileJob(_host, Request(path), journal).RunAsync(null, Ct);
        Assert.True(first.StoppedOnError);
        File.AppendAllText(path, "SELECT 1;");

        await Assert.ThrowsAsync<SqlFileChangedException>(() => new SqlFileJob(_host, journal.List().Single(), journal).RunAsync(null, Ct));
    }

    /// <summary>A file that does not read (another program holds it, a lost network share) fails the run; the connection is fine.</summary>
    [Fact]
    public async Task UnreadableFileIsNotALostConnection()
    {
        TestServer.RequireServer();
        var path = Write("locked.sql", Script(3));
        var states = new List<SessionState>();
        _host.StateChanged += (_, e) => { lock (states) { states.Add(e.State); } };
        await using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var error = await Assert.ThrowsAsync<LocalFileException>(() => new SqlFileJob(_host, Request(path)).RunAsync(null, Ct));
            Assert.Contains("used by another process", error.Message, StringComparison.Ordinal);
        }
        Assert.Equal(SessionState.Ready, _host.State);
        lock (states)
        {
            Assert.DoesNotContain(SessionState.Reconnecting, states);
        }
        var result = await new SqlFileJob(_host, Request(path)).RunAsync(null, Ct);
        Assert.True(result.Completed);
        Assert.Equal(0, result.Errors);
    }

    [Fact]
    public void EncodingDetection()
    {
        Assert.Equal(("UTF-8", 3), TextFileEncoding.Detect([0xEF, 0xBB, 0xBF, 0x41], App.Query.SqlFile.AutoDetect));
        Assert.Equal(("Unicode", 2), TextFileEncoding.Detect([0xFF, 0xFE, 0x41, 0], "UTF-8"));
        Assert.Equal(("UTF-8", 0), TextFileEncoding.Detect("ação"u8.ToArray().AsSpan(0, 4), App.Query.SqlFile.AutoDetect));
        Assert.Equal(("ANSI", 0), TextFileEncoding.Detect([0x61, 0xE7, 0x61], App.Query.SqlFile.AutoDetect));
        Assert.Equal(("ANSI", 0), TextFileEncoding.Detect([0x61, 0xE7, 0x61], "ANSI"));
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
