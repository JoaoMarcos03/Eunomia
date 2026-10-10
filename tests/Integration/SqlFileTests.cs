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
        sb.Append("-- cabeçalho; com ponto e vírgula\r\n/*!40101 SET NAMES utf8mb4 */;\r\n");
        sb.Append("CREATE TABLE t (id INT PRIMARY KEY, txt VARCHAR(100)) ENGINE=InnoDB;\r\n");
        sb.Append("DELIMITER //\r\nCREATE PROCEDURE p()\r\nBEGIN\r\n  SELECT 'a;b';\r\n  SELECT 2;\r\nEND//\r\nDELIMITER ;\r\n");
        for (var i = 1; i <= rows; i++)
        {
            if (broken is not null && i == rows / 2)
            {
                sb.Append(broken).Append("\r\n");
            }
            sb.Append(CultureInfo.InvariantCulture, $"INSERT INTO t VALUES ({i}, 'ação \"{i}\"; it''s # não -- é comentário');\r\n");
        }
        sb.Append("# fim\r\n");
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
    public async Task BlocosPequenosNaoPerdemODelimiterNemOsAcentos()
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
        Assert.Equal("ação \"7\"; it's # não -- é comentário", await ScalarAsync($"SELECT txt FROM `{_database}`.t WHERE id = 7"));
        Assert.Equal("1", await ScalarAsync($"SELECT COUNT(*) FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = '{_database}' AND ROUTINE_NAME = 'p'"));
        Assert.NotEmpty(progress);
    }

    [Fact]
    public async Task BatchEmPacotesDaOMesmoResultado()
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
    public async Task ParaNoErroERetomaNaInstrucaoQueFalhou()
    {
        TestServer.RequireServer();
        var path = Write("broken.sql", Script(100, broken: "INSERT INTO falta VALUES (1);"));
        var journal = new TransferJournal(Path.Combine(_directory, "jobs"));

        var first = await new SqlFileJob(_host, Request(path), journal) { ChunkSize = 64 }.RunAsync(null, Ct);
        Assert.True(first.StoppedOnError);
        var error = Assert.Single(first.ErrorList);
        // Statements: SET NAMES, CREATE, PROCEDURE, 49 INSERTs; the 53rd fails. Line: 2 for the header + 1 + 7 for the DELIMITER + 49 + 1.
        Assert.Equal(53, error.Statement);
        Assert.Equal(60, error.Line);
        Assert.Contains("falta", error.Message, StringComparison.Ordinal);
        Assert.Equal("49", await ScalarAsync($"SELECT COUNT(*) FROM `{_database}`.t"));
        var entry = Assert.Single(journal.List());
        Assert.Equal(52, entry.Count);
        Assert.Equal(";", entry.Delimiter);

        // With the problem fixed, resuming runs the statement that failed and the rest, without repeating the earlier ones.
        await ExecAsync($"CREATE TABLE `{_database}`.falta (a INT)");
        var resumed = await new SqlFileJob(_host, entry, journal) { ChunkSize = 64 }.RunAsync(null, Ct);
        Assert.True(resumed.Completed);
        Assert.Equal(first.JobId, resumed.JobId);
        Assert.Equal(104, resumed.Statements);
        Assert.Equal("100", await ScalarAsync($"SELECT COUNT(*) FROM `{_database}`.t"));
        Assert.Equal("1", await ScalarAsync($"SELECT COUNT(*) FROM `{_database}`.falta"));
        Assert.Empty(journal.List());

        // Without Stop on errors, the errors are counted and the file runs to the end.
        await ExecAsync($"DROP TABLE `{_database}`.t, `{_database}`.falta");
        await ExecAsync($"DROP PROCEDURE `{_database}`.p");
        var lenient = await new SqlFileJob(_host, Request(path) with { StopOnErrors = false }).RunAsync(null, Ct);
        Assert.False(lenient.StoppedOnError);
        Assert.Equal(1, lenient.Errors);
        Assert.Equal(104, lenient.Statements);
        Assert.Equal("100", await ScalarAsync($"SELECT COUNT(*) FROM `{_database}`.t"));
    }

    [Fact]
    public async Task FicheiroAlteradoNaoRetoma()
    {
        TestServer.RequireServer();
        var path = Write("changed.sql", Script(10, broken: "SELECT * FROM falta;"));
        var journal = new TransferJournal(Path.Combine(_directory, "jobs2"));
        var first = await new SqlFileJob(_host, Request(path), journal).RunAsync(null, Ct);
        Assert.True(first.StoppedOnError);
        File.AppendAllText(path, "SELECT 1;");

        await Assert.ThrowsAsync<SqlFileChangedException>(() => new SqlFileJob(_host, journal.List().Single(), journal).RunAsync(null, Ct));
    }

    [Fact]
    public void DetecaoDaCodificacao()
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
