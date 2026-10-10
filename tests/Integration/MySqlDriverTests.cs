using System.Diagnostics;
using System.Globalization;
using Eunomia.Drivers.Abstractions;
using Eunomia.Drivers.MySql;
using Eunomia.Engine.Results;

namespace Eunomia.Integration.Tests;

/// <summary>MySQL/MariaDB driver against a real server (section 24; phase 3 integration).</summary>
public sealed class MySqlDriverTests : IAsyncLifetime
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
            // The connection may have been killed by the test: cleans up through a new connection.
            await _db.DisposeAsync();
            await using var cleanup = await TestServer.OpenAsync();
            await TestServer.DropDatabaseAsync(cleanup, _database);
        }
    }

    [Fact]
    public void IdentificaOServidor()
    {
        TestServer.RequireServer();

        Assert.True(_db.IsOpen);
        Assert.True(_db.ServerConnectionId > 0);
        Assert.True(_db.Server.Version.Major >= 5);
        Assert.Equal(_db.Server.VersionText.Contains("MariaDB", StringComparison.OrdinalIgnoreCase) ? DbEngine.MariaDb : DbEngine.MySql, _db.Server.Engine);
    }

    [Fact]
    public async Task TiposSemPerdasEmBlocosColunares()
    {
        TestServer.RequireServer();
        await TestServer.RunAsync(_db,
            """
            CREATE TABLE t (
              id INT UNSIGNED NOT NULL PRIMARY KEY, i BIGINT NULL, u BIGINT UNSIGNED NULL, d DECIMAL(12,2) NULL, big DECIMAL(40,10) NULL,
              f DOUBLE NULL, b BIT(3) NULL, v VARCHAR(50) NULL, tx TEXT NULL, bl BLOB NULL, dt DATETIME NULL, dt6 DATETIME(6) NULL,
              dd DATE NULL, tm TIME NULL, y YEAR NULL, e ENUM('a','b c') NULL, j LONGTEXT NULL)
            """,
            "SET SESSION sql_mode = ''",
            """
            INSERT INTO t VALUES
              (1, -9223372036854775808, 18446744073709551615, 1234567890.12, 123456789012345678901234567890.0123456789,
               3.25, b'101', 'café ☕', 'linha1\nlinha2', 0x00FF10, '2024-02-29 13:45:01', '2024-02-29 13:45:01.123456',
               '2024-02-29', '-838:59:59', 2024, 'b c', '{"a":1}'),
              (2, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, '0000-00-00 00:00:00', NULL, '0000-00-00', '25:00:00', NULL, NULL, NULL)
            """);

        var events = await TestServer.RunAsync(_db, "SELECT * FROM t ORDER BY id");

        var schema = Assert.Single(events.OfType<ResultSetStarted>()).Schema;
        var block = Assert.Single(events.OfType<RowBlockReceived>()).Block;
        Assert.Equal(2, block.RowCount);
        int Col(string name) => schema.Columns.ToList().FindIndex(c => c.Name == name);
        var fmt = new DisplayFormatter(CultureInfo.InvariantCulture, thousandsSeparator: false);
        string Cell(int row, string col) => fmt.Format(block, row, Col(col));

        Assert.True(schema[Col("id")].IsPrimaryKey);
        Assert.Equal(StorageKind.Int64, schema[Col("i")].Storage);
        Assert.Equal("-9223372036854775808", Cell(0, "i"));
        Assert.Equal(StorageKind.String, schema[Col("u")].Storage);
        Assert.Equal("18446744073709551615", Cell(0, "u"));
        Assert.Equal(StorageKind.Decimal, schema[Col("d")].Storage);
        Assert.Equal("1234567890.12", Cell(0, "d"));
        Assert.Equal("123456789012345678901234567890.0123456789", Cell(0, "big"));
        Assert.Equal("3.25", Cell(0, "f"));
        // BIT as binary digits (like HeidiSQL; the DML reads this form as b'101').
        Assert.Equal("101", Cell(0, "b"));
        Assert.Equal("café ☕", Cell(0, "v"));
        Assert.Equal("linha1\nlinha2", Cell(0, "tx"));
        Assert.Equal(StorageKind.Binary, schema[Col("bl")].Storage);
        Assert.Equal("0x00FF10", Cell(0, "bl"));
        Assert.Equal("2024-02-29 13:45:01", Cell(0, "dt"));
        Assert.Equal("2024-02-29 13:45:01.123456", Cell(0, "dt6"));
        Assert.Equal("2024-02-29", Cell(0, "dd"));
        Assert.Equal("-838:59:59", Cell(0, "tm"));
        Assert.Equal("2024", Cell(0, "y"));
        Assert.Equal("b c", Cell(0, "e"));
        Assert.Equal("{\"a\":1}", Cell(0, "j"));
        Assert.Equal(TypeCategory.Temporal, schema[Col("dt")].Category);
        Assert.Equal(TypeCategory.Integer, schema[Col("i")].Category);

        Assert.Equal("(NULL)", Cell(1, "i"));
        Assert.Equal("0000-00-00 00:00:00", Cell(1, "dt"));
        Assert.Equal("0000-00-00", Cell(1, "dd"));
        Assert.Equal("25:00:00", Cell(1, "tm"));

        var done = Assert.Single(events.OfType<StatementCompleted>());
        Assert.Equal(2, done.FoundRows);
    }

    [Fact]
    public async Task BlocosDeTamanhoFixoComPrimeiraLinhaCerta()
    {
        TestServer.RequireServer();

        var events = await TestServer.RunAsync(_db, new ExecuteOptions(BlockSize: 1024),
            "SELECT seq FROM (SELECT a.n * 100 + b.n * 10 + c.n + d.n * 1000 AS seq FROM " +
            "(SELECT 0 n UNION SELECT 1 UNION SELECT 2 UNION SELECT 3 UNION SELECT 4 UNION SELECT 5 UNION SELECT 6 UNION SELECT 7 UNION SELECT 8 UNION SELECT 9) a, " +
            "(SELECT 0 n UNION SELECT 1 UNION SELECT 2 UNION SELECT 3 UNION SELECT 4 UNION SELECT 5 UNION SELECT 6 UNION SELECT 7 UNION SELECT 8 UNION SELECT 9) b, " +
            "(SELECT 0 n UNION SELECT 1 UNION SELECT 2 UNION SELECT 3 UNION SELECT 4 UNION SELECT 5 UNION SELECT 6 UNION SELECT 7 UNION SELECT 8 UNION SELECT 9) c, " +
            "(SELECT 0 n UNION SELECT 1 UNION SELECT 2) d) x WHERE seq < 2500 ORDER BY seq");

        var blocks = events.OfType<RowBlockReceived>().Select(e => e.Block).ToList();
        Assert.Equal([1024, 1024, 452], blocks.Select(b => b.RowCount));
        Assert.Equal([0L, 1024L, 2048L], blocks.Select(b => b.FirstRow));
        Assert.Equal([0, 1, 2], blocks.Select(b => b.BlockIndex));
        Assert.Equal(2048L, blocks[2].GetInt64(0, 0));
        Assert.Equal(2500, Assert.Single(events.OfType<ResultSetCompleted>()).RowCount);
    }

    [Fact]
    public async Task VariasInstrucoesResultSetsEContagens()
    {
        TestServer.RequireServer();

        var events = await TestServer.RunAsync(_db,
            "CREATE TABLE a (x INT)",
            "INSERT INTO a VALUES (1), (2), (3)",
            "UPDATE a SET x = x + 10 WHERE x > 1",
            "SELECT 1 AS one; SELECT 'b' AS two",
            "SELECT x FROM a");

        var completed = events.OfType<StatementCompleted>().ToList();
        Assert.Equal(5, completed.Count);
        Assert.Equal(3, completed[1].AffectedRows);
        Assert.Equal(2, completed[2].AffectedRows);
        Assert.Equal(3, events.OfType<ResultSetStarted>().Count());
        Assert.Equal([0, 1, 2], events.OfType<ResultSetStarted>().Select(e => e.ResultSetIndex));
        Assert.Equal(3, completed[4].FoundRows);
    }

    [Fact]
    public async Task ErroParaOBatchComStopOnErrors()
    {
        TestServer.RequireServer();

        var events = await TestServer.RunAsync(_db, "SELECT 1", "SELEC 2", "SELECT 3");

        var error = Assert.Single(events.OfType<ExecutionError>());
        Assert.Equal(1, error.StatementIndex);
        Assert.Equal(1064, error.Error.Code);
        Assert.Equal(DbErrorCategory.Syntax, new MySqlErrorClassifier().Classify(error.Error).Category);
        Assert.Single(events.OfType<StatementCompleted>());

        var all = await TestServer.RunAsync(_db, new ExecuteOptions(StopOnError: false), "SELECT 1", "SELEC 2", "SELECT 3");
        Assert.Equal(2, all.OfType<StatementCompleted>().Count());
    }

    [Fact]
    public async Task WarningsDoServidor()
    {
        TestServer.RequireServer();

        var events = await TestServer.RunAsync(_db, "SELECT CAST('12abc' AS SIGNED) AS n");

        var warning = Assert.Single(events.OfType<ServerMessage>());
        Assert.Equal(MessageSeverity.Warning, warning.Severity);
        Assert.Equal(1292, warning.Code);
        Assert.Equal(1, Assert.Single(events.OfType<StatementCompleted>()).Warnings);
    }

    [Fact]
    public async Task CancelInterrompeAQueryEAligacaoContinuaUtil()
    {
        TestServer.RequireServer();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var clock = Stopwatch.StartNew();
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));

        var ex = await Record.ExceptionAsync(async () =>
        {
            await foreach (var _ in _db.Executor.ExecuteAsync(SqlBatch.Single("SELECT SLEEP(10)"), new ExecuteOptions(), cts.Token))
            {
            }
        });

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"Cancel demorou {clock.Elapsed}.");
        Assert.True(ex is OperationCanceledException || ex is null, ex?.ToString());
        await _db.PingAsync(Ct);
        var events = await TestServer.RunAsync(_db, "SELECT 42");
        Assert.Equal(42L, events.OfType<RowBlockReceived>().Single().Block.GetInt64(0, 0));
    }

    [Fact]
    public async Task CancelCurrentComKillQuery()
    {
        TestServer.RequireServer();
        var run = Task.Run(async () => await TestServer.RunAsync(_db, "SELECT SLEEP(10)"));
        await Task.Delay(400, Ct);
        var clock = Stopwatch.StartNew();

        await _db.CancelCurrentAsync(Ct);
        var events = await run;

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
        // MariaDB returns 1 for a SLEEP interrupted by KILL QUERY, or error 1317.
        Assert.True(events.OfType<StatementCompleted>().Any() || events.OfType<ExecutionError>().Any(e => e.Error.Code == 1317));
    }

    [Fact]
    public async Task PingFalhaDepoisDeMatarALigacao()
    {
        TestServer.RequireServer();
        await using var other = await TestServer.OpenAsync();

        await ((MySqlConnectionHandle)other).ExecuteNonQueryAsync($"KILL {_db.ServerConnectionId}", Ct);

        await Assert.ThrowsAnyAsync<Exception>(() => _db.PingAsync(Ct));
    }
}
