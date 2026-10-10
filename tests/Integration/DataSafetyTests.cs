using System.Text;
using Eunomia.App.Query;
using Eunomia.Drivers.Abstractions;
using Eunomia.Drivers.MySql;
using Eunomia.Engine.Execution;
using Eunomia.Engine.Execution.Editing;
using Eunomia.Engine.Metadata;
using Eunomia.Engine.Sql.Splitting;
using MySqlConnector;

namespace Eunomia.Integration.Tests;

/// <summary>
/// Writes that must not damage data (phase 14): exact values under any <c>sql_mode</c>, tables without a key with
/// rows that only differ in case, and stale keys in the cache.
/// </summary>
public sealed class DataSafetyTests : IAsyncLifetime
{
    /// <summary>Values that wrong escaping alters: backslashes, quotes, line breaks, NUL, Ctrl+Z and characters outside the BMP.</summary>
    private static readonly string[] Tricky =
    [
        @"C:\dir\file.txt",
        "it's",
        "linha 1\nlinha 2\r\n",
        @"barra-n literal \n e \\ dupla",
        "nul\0meio",
        "ctrl\u001Az",
        "emoji 😀 e acentuação",
        "\\",
        "'",
        "\"aspas\" `crases`",
        "{\"json\": \"a\\\"b\\\\c\"}",
    ];

    private SessionHost _host = null!;
    private MetadataCache _metadata = null!;
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
        _metadata = new MetadataCache(_host, new MetadataOptions());
        _database = "eunomia_safe_" + Guid.NewGuid().ToString("N")[..8];
        await ExecAsync($"CREATE DATABASE `{_database}` CHARACTER SET utf8mb4");
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.RunPrimaryAsync((h, ct) => h.ExecuteNonQueryAsync($"DROP DATABASE IF EXISTS `{_database}`", ct), CancellationToken.None);
            await _host.DisposeAsync();
        }
    }

    private Task<int> ExecAsync(string sql) => _host.RunPrimaryAsync((h, ct) => h.ExecuteNonQueryAsync(sql, ct), Ct);

    private Task<object?> ScalarAsync(string sql) => _host.RunPrimaryAsync((h, ct) => h.ExecuteScalarAsync(sql, ct), Ct);

    private Task<TableDefinition> TableAsync(string name) =>
        _metadata.GetTableAsync(new DbObjectRef(_database, _database, name, DbObjectKind.Table), Ct);

    private static Dictionary<string, CellLiteral> Values(params (string Name, string? Text)[] values) =>
        values.ToDictionary(v => v.Name, v => new CellLiteral(v.Text), StringComparer.OrdinalIgnoreCase);

    private static string Hex(string text) => Convert.ToHexString(Encoding.UTF8.GetBytes(text));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValoresDificeisGravamExatosComQualquerSqlMode(bool noBackslashEscapes)
    {
        TestServer.RequireServer();
        await ExecAsync($"CREATE TABLE `{_database}`.v (k VARCHAR(100) PRIMARY KEY, t TEXT, c VARCHAR(100) COLLATE latin1_swedish_ci)");
        var table = await TableAsync("v");
        // The mode changes during the session without the client knowing (like a SET by the user or by a login trigger).
        if (noBackslashEscapes)
        {
            await ExecAsync("SET SESSION sql_mode = CONCAT(@@SESSION.sql_mode, ',NO_BACKSLASH_ESCAPES')");
        }
        var writer = new RowWriter(_host);
        for (var i = 0; i < Tricky.Length; i++)
        {
            var key = "k" + i + Tricky[i];
            var value = Tricky[i];
            var latin = value.Contains("😀", StringComparison.Ordinal) || value.Contains('\0', StringComparison.Ordinal) ? "simples" : value;
            await writer.WriteAsync(table, new RowChange(RowChangeKind.Insert, Values(), Values(("k", key), ("t", value), ("c", latin))), Ct);
            Assert.Equal(Hex(value), await ScalarAsync($"SELECT HEX(t) FROM `{_database}`.v WHERE k = _utf8mb4 X'{Hex(key)}'"));

            // UPDATE by key with backslashes and quotes in the WHERE: changes exactly this row.
            var updated = value + "|2";
            var result = await writer.WriteAsync(table, new RowChange(RowChangeKind.Update, Values(("k", key)), Values(("t", updated))), Ct);
            Assert.Equal(1, result.AffectedRows);
            Assert.Equal(Hex(updated), await ScalarAsync($"SELECT HEX(t) FROM `{_database}`.v WHERE k = _utf8mb4 X'{Hex(key)}'"));
            Assert.Equal(latin, Convert.ToString(await ScalarAsync($"SELECT c FROM `{_database}`.v WHERE k = _utf8mb4 X'{Hex(key)}'"), System.Globalization.CultureInfo.InvariantCulture));
        }
        Assert.Equal(Tricky.Length, Convert.ToInt32(await ScalarAsync($"SELECT COUNT(*) FROM `{_database}`.v"), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task SemChaveNaoMudaALinhaQueSoDifereEmMaiusculas()
    {
        TestServer.RequireServer();
        await ExecAsync($"CREATE TABLE `{_database}`.nokey (name VARCHAR(10) COLLATE utf8mb4_general_ci, latin VARCHAR(10) COLLATE latin1_general_ci, n INT)");
        await ExecAsync($"INSERT INTO `{_database}`.nokey VALUES ('Ab', 'Çé', 1), ('ab', 'çé', 1)");
        var table = await TableAsync("nokey");
        var writer = new RowWriter(_host);

        // The visible row is ('ab', 'çé', 1); with the column collation, ('Ab', 'Çé', 1) also matched the WHERE.
        var result = await writer.WriteAsync(table, new RowChange(RowChangeKind.Update, Values(("name", "ab"), ("latin", "çé"), ("n", "1")), Values(("n", "2"))), Ct);
        Assert.Equal(1, result.AffectedRows);
        Assert.Equal("Ab:1,ab:2", await ScalarAsync($"SELECT GROUP_CONCAT(CONCAT(name, ':', n) ORDER BY BINARY name) FROM `{_database}`.nokey"));

        await writer.WriteAsync(table, new RowChange(RowChangeKind.Delete, Values(("name", "Ab"), ("latin", "Çé"), ("n", "1")), Values()), Ct);
        Assert.Equal("ab:2", await ScalarAsync($"SELECT GROUP_CONCAT(CONCAT(name, ':', n)) FROM `{_database}`.nokey"));
    }

    [Fact]
    public async Task ChaveDesatualizadaNaCacheNuncaMudaMaisDeUmaLinha()
    {
        TestServer.RequireServer();
        await ExecAsync($"CREATE TABLE `{_database}`.stale (id INT PRIMARY KEY, v VARCHAR(10))");
        var cached = await TableAsync("stale");
        // Someone else removes the key and inserts rows with the same id after the definition is in the cache.
        await ExecAsync($"ALTER TABLE `{_database}`.stale DROP PRIMARY KEY");
        await ExecAsync($"INSERT INTO `{_database}`.stale VALUES (1, 'a'), (1, 'b'), (1, 'c')");

        var writer = new RowWriter(_host);
        await writer.WriteAsync(cached, new RowChange(RowChangeKind.Update, Values(("id", "1")), Values(("v", "z"))), Ct);
        Assert.Equal(1L, Convert.ToInt64(await ScalarAsync($"SELECT COUNT(*) FROM `{_database}`.stale WHERE v = 'z'"), System.Globalization.CultureInfo.InvariantCulture));
        await writer.WriteAsync(cached, new RowChange(RowChangeKind.Delete, Values(("id", "1")), Values()), Ct);
        Assert.Equal(2L, Convert.ToInt64(await ScalarAsync($"SELECT COUNT(*) FROM `{_database}`.stale"), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task DuplicarPeloTextoBrutoGuardaOsMesmosValores()
    {
        TestServer.RequireServer();
        // Zero dates (MySQL 8 refuses them in the default mode): the session runs without strict mode during the test.
        await ExecAsync("SET SESSION sql_mode = ''");
        await ExecAsync($"CREATE TABLE `{_database}`.dup (d DOUBLE, f FLOAT, b BIT(8), n DECIMAL(30,10), big BIGINT UNSIGNED, bin LONGBLOB, t TEXT, dt DATETIME(6), z DATE)");
        await ExecAsync($"INSERT INTO `{_database}`.dup VALUES (PI(), 0.1, b'1010', 12345678901234567890.0123456789, 18446744073709551615, " +
            $"UNHEX(REPEAT('00FF7F80C3A9', 200)), REPEAT('ação \\\\ ', 100), '2026-10-09 12:34:56.123456', '0000-00-00')");
        var table = await TableAsync("dup");
        var raw = await _host.RunPrimaryAsync(async (h, ct) =>
        {
            var values = new Dictionary<string, CellLiteral>(StringComparer.OrdinalIgnoreCase);
            await foreach (var e in h.Executor.ExecuteAsync(new SqlBatch([$"SELECT * FROM `{_database}`.dup"]), new ExecuteOptions { CollectWarnings = false }, ct))
            {
                if (e is RowBlockReceived received)
                {
                    for (var c = 0; c < received.Block.Schema.Count; c++)
                    {
                        values[received.Block.Schema[c].Name] = new CellLiteral(Engine.Results.DisplayFormatter.FormatRaw(received.Block, 0, c));
                    }
                }
            }
            return values;
        }, Ct);

        // "Duplicate row" saves the values read: the copy must be identical byte for byte.
        await ExecAsync("SET SESSION sql_mode = ''");
        await new RowWriter(_host).WriteAsync(table, new RowChange(RowChangeKind.Insert, Values(), raw), Ct);
        Assert.Equal(1L, Convert.ToInt64(await ScalarAsync(
            $"SELECT COUNT(DISTINCT d, f, b, n, big, MD5(bin), MD5(t), dt, z) FROM `{_database}`.dup"), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(2L, Convert.ToInt64(await ScalarAsync($"SELECT COUNT(*) FROM `{_database}`.dup"), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("10", Convert.ToString(await ScalarAsync($"SELECT MIN(b + 0) FROM `{_database}`.dup"), System.Globalization.CultureInfo.InvariantCulture));

        // Without a key, the WHERE with every raw value (full DOUBLE and BLOB) finds the row.
        var noFloat = raw.Where(v => !string.Equals(v.Key, "f", StringComparison.OrdinalIgnoreCase)).ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);
        await ExecAsync($"ALTER TABLE `{_database}`.dup DROP COLUMN f");
        _metadata.InvalidateTable(new DbObjectRef(_database, _database, "dup", DbObjectKind.Table));
        var withoutFloat = await TableAsync("dup");
        var result = await new RowWriter(_host).WriteAsync(withoutFloat, new RowChange(RowChangeKind.Update, noFloat, Values(("t", "mudou"))), Ct);
        Assert.Equal(1, result.AffectedRows);
    }

    [Theory]
    [InlineData("1,5")]
    [InlineData("1.234,50")]
    [InlineData("true")]
    [InlineData("")]
    public async Task NumerosInvalidosNaoSaoGravados(string typed)
    {
        TestServer.RequireServer();
        await ExecAsync($"CREATE TABLE `{_database}`.num (id INT PRIMARY KEY, amount DECIMAL(10,2), flag BOOL)");
        await ExecAsync($"INSERT INTO `{_database}`.num VALUES (1, 10.00, 0)");
        var table = await TableAsync("num");
        await ExecAsync("SET SESSION sql_mode = ''");
        var writer = new RowWriter(_host);
        await Assert.ThrowsAsync<RowWriteException>(() => writer.WriteAsync(table, new RowChange(RowChangeKind.Update, Values(("id", "1")), Values(("amount", typed))), Ct));
        await Assert.ThrowsAsync<RowWriteException>(() => writer.WriteAsync(table, new RowChange(RowChangeKind.Update, Values(("id", "1")), Values(("flag", typed))), Ct));
        Assert.Equal("10.00|0", await ScalarAsync($"SELECT CONCAT(amount, '|', flag) FROM `{_database}`.num"));
        // Valid numbers and expressions (Ctrl+F2) go through.
        await writer.WriteAsync(table, new RowChange(RowChangeKind.Update, Values(("id", "1")), Values(("amount", " 1.5e1 "))), Ct);
        await writer.WriteAsync(table, new RowChange(RowChangeKind.Update, Values(("id", "1")),
            new Dictionary<string, CellLiteral> { ["flag"] = new("1 + 0", IsFunction: true) }), Ct);
        Assert.Equal("15.00|1", await ScalarAsync($"SELECT CONCAT(amount, '|', flag) FROM `{_database}`.num"));
    }

    [Fact]
    public async Task AlterQueCortariaValoresFalhaSemModoEstrito()
    {
        TestServer.RequireServer();
        await ExecAsync($"CREATE TABLE `{_database}`.shrink (id INT PRIMARY KEY, name VARCHAR(40))");
        await ExecAsync($"INSERT INTO `{_database}`.shrink VALUES (1, 'um nome comprido demais')");
        // Session without strict mode: MySQL would truncate the value and only leave a warning.
        await ExecAsync("SET SESSION sql_mode = 'NO_ENGINE_SUBSTITUTION'");
        var session = new Eunomia.App.Sessions.OpenSession(new Eunomia.App.Sessions.SessionDefinition(), _host);
        var baseline = await TableAsync("shrink");
        var design = Eunomia.App.Objects.TableDesign.From(baseline);
        design.Columns[1].LengthSet = "5";
        var statements = Eunomia.App.Objects.TableEditing.Statements(_host.ServerServices!, baseline, design);
        Assert.NotEmpty(Eunomia.App.Objects.TableEditing.DataLossWarnings(baseline, design.ToDefinition(_host.ServerServices!.Types, _host.ServerServices.Dialect)));

        await Assert.ThrowsAsync<Eunomia.Engine.Execution.SessionOperationException>(() => Eunomia.App.Objects.TableEditing.ApplyAsync(session, statements, Ct));
        Assert.Equal("um nome comprido demais", await ScalarAsync($"SELECT name FROM `{_database}`.shrink"));
        Assert.Equal("varchar(40)", await ScalarAsync(
            $"SELECT COLUMN_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = '{_database}' AND TABLE_NAME = 'shrink' AND COLUMN_NAME = 'name'"));
        // The session mode goes back to the user's.
        Assert.Equal("NO_ENGINE_SUBSTITUTION", await ScalarAsync("SELECT @@SESSION.sql_mode"));
    }

    [Fact]
    public async Task ChavesEstrangeirasVoltamSeOAlterPrincipalFalhar()
    {
        TestServer.RequireServer();
        await ExecAsync($"CREATE TABLE `{_database}`.parent (id INT PRIMARY KEY) ENGINE=InnoDB");
        await ExecAsync($"CREATE TABLE `{_database}`.child (id INT PRIMARY KEY, parent_id INT, note VARCHAR(40), " +
            "CONSTRAINT fk_parent FOREIGN KEY (parent_id) REFERENCES parent (id) ON DELETE CASCADE) ENGINE=InnoDB");
        await ExecAsync($"INSERT INTO `{_database}`.parent VALUES (1)");
        await ExecAsync($"INSERT INTO `{_database}`.child VALUES (1, 1, 'nota longa demais para cinco')");
        var session = new Eunomia.App.Sessions.OpenSession(new Eunomia.App.Sessions.SessionDefinition(), _host);
        var baseline = await TableAsync("child");
        var design = Eunomia.App.Objects.TableDesign.From(baseline);
        // The FK changes (the first ALTER drops it) and the column narrows (the second ALTER fails in strict mode).
        design.ForeignKeys[0].OnDelete = ForeignKeyAction.Restrict;
        design.Columns[2].LengthSet = "5";
        var statements = Eunomia.App.Objects.TableEditing.Statements(_host.ServerServices!, baseline, design);
        Assert.Equal(2, statements.Count);
        var restore = Eunomia.App.Objects.TableEditing.RestoreDroppedForeignKeys(_host.ServerServices!, baseline, design);
        Assert.NotNull(restore);

        await Assert.ThrowsAsync<Eunomia.Engine.Execution.SessionOperationException>(() => Eunomia.App.Objects.TableEditing.ApplyAsync(session, statements, Ct, restore));
        Assert.Equal("fk_parent|CASCADE", await ScalarAsync(
            $"SELECT CONCAT(CONSTRAINT_NAME, '|', DELETE_RULE) FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = '{_database}' AND TABLE_NAME = 'child'"));
        Assert.Equal("nota longa demais para cinco", await ScalarAsync($"SELECT note FROM `{_database}`.child"));
    }

    [Theory]
    [InlineData(DbObjectKind.View, "CREATE VIEW v_obj AS SELECT 1 AS a", "CREATE VIEW v_obj AS SELEC 2")]
    [InlineData(DbObjectKind.Procedure, "CREATE PROCEDURE p_obj() SELECT 1", "CREATE PROCEDURE p_obj() SELEC 2")]
    [InlineData(DbObjectKind.Function, "CREATE FUNCTION f_obj() RETURNS INT DETERMINISTIC RETURN 1", "CREATE FUNCTION f_obj() RETURNS INT DETERMINISTIC RETURN")]
    [InlineData(DbObjectKind.Trigger, "CREATE TRIGGER t_obj BEFORE INSERT ON host FOR EACH ROW SET NEW.n = 1", "CREATE TRIGGER t_obj BEFORE INSERT ON host FOR EACH ROW SET NEW.nope = 1")]
    public async Task GravarCodigoInvalidoDeixaOObjetoComoEstava(DbObjectKind kind, string original, string broken)
    {
        TestServer.RequireServer();
        await ExecAsync($"CREATE TABLE `{_database}`.host (n INT)");
        await ExecAsync($"USE `{_database}`");
        await ExecAsync(original);
        var session = new Eunomia.App.Sessions.OpenSession(new Eunomia.App.Sessions.SessionDefinition(), _host);
        var obj = new DbObjectRef(_database, _database, Eunomia.App.Objects.ObjectCode.Parse(original)!.Value.Name, kind);
        var before = await _host.RunMetadataAsync((h, ct) => h.Metadata.GetCreateCodeAsync(obj, ct), Ct);

        await Assert.ThrowsAnyAsync<Exception>(() => Eunomia.App.Objects.ObjectCode.SaveAsync(session, _database, obj, before, broken, Ct));
        Assert.Equal(before, await _host.RunMetadataAsync((h, ct) => h.Metadata.GetCreateCodeAsync(obj, ct), Ct));

        // A valid change saves.
        var changed = original.Replace("1", "7", StringComparison.Ordinal);
        await Eunomia.App.Objects.ObjectCode.SaveAsync(session, _database, obj, before, changed, Ct);
        Assert.Contains("7", await _host.RunMetadataAsync((h, ct) => h.Metadata.GetCreateCodeAsync(obj, ct), Ct), StringComparison.Ordinal);
        // No temporary validation object is left behind.
        Assert.Equal(0L, Convert.ToInt64(await ScalarAsync(
            $"SELECT COUNT(*) FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = '{_database}' AND ROUTINE_NAME LIKE 'eunomia_check_%'"), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task OriginalQueNaoVoltaFicaNumFicheiro()
    {
        TestServer.RequireServer();
        await ExecAsync($"CREATE TABLE `{_database}`.host (n INT)");
        await ExecAsync($"USE `{_database}`");
        await ExecAsync("CREATE TRIGGER t_lost BEFORE INSERT ON host FOR EACH ROW SET NEW.n = 1");
        var session = new Eunomia.App.Sessions.OpenSession(new Eunomia.App.Sessions.SessionDefinition(), _host);
        var obj = new DbObjectRef(_database, _database, "t_lost", DbObjectKind.Trigger);
        var recovery = Path.Combine(Path.GetTempPath(), "eunomia-recovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            // New name (DROP + CREATE path) with invalid code and an original that the server also refuses.
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Eunomia.App.Objects.ObjectCode.SaveAsync(
                session, _database, obj, "CREATE TRIGGER t_lost BEFORE INSERT ON host FOR EACH ROW SET NEW.nope = 1",
                "CREATE TRIGGER t_new BEFORE INSERT ON host FOR EACH ROW SET NEW.nope = 2", Ct, recovery));
            var file = Assert.Single(Directory.GetFiles(recovery));
            Assert.Contains(file, error.Message, StringComparison.Ordinal);
            Assert.Contains("t_lost", await File.ReadAllTextAsync(file, Ct), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(recovery))
            {
                Directory.Delete(recovery, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AlterAEsperaDeUmLockDeMetadataFalhaDepressa()
    {
        TestServer.RequireServer();
        await ExecAsync($"CREATE TABLE `{_database}`.busy (id INT PRIMARY KEY, v VARCHAR(10)) ENGINE=InnoDB");
        // Another session with an open transaction that read the table: it holds the metadata lock until COMMIT.
        await using var other = new MySqlConnection(TestServer.ConnectionString);
        await other.OpenAsync(Ct);
        await using (var hold = new MySqlCommand($"START TRANSACTION; SELECT * FROM `{_database}`.busy;", other))
        {
            await hold.ExecuteNonQueryAsync(Ct);
        }
        var session = new Eunomia.App.Sessions.OpenSession(new Eunomia.App.Sessions.SessionDefinition(), _host);
        var error = await Assert.ThrowsAsync<Eunomia.Engine.Execution.SessionOperationException>(() => Eunomia.App.Objects.TableEditing.ApplyAsync(
            session, [$"ALTER TABLE `{_database}`.busy ADD COLUMN extra INT"], Ct));
        // On its own it waits ~10 s (one attempt); with another DDL running in parallel on the server there may be several attempts.
        Assert.Equal(1205, error.Error.Code);
        Assert.Contains("metadata lock", error.Error.Message, StringComparison.Ordinal);
        // The session goes back to the lock_wait_timeout it had.
        Assert.NotEqual(10L, Convert.ToInt64(await ScalarAsync("SELECT @@SESSION.lock_wait_timeout"), System.Globalization.CultureInfo.InvariantCulture));
        await using (var commit = new MySqlCommand("COMMIT", other))
        {
            await commit.ExecuteNonQueryAsync(Ct);
        }
    }

    [Fact]
    public async Task EdicaoNaoApagaAlteracaoDeOutraSessaoSemPerguntar()
    {
        TestServer.RequireServer();
        await ExecAsync($"CREATE TABLE `{_database}`.shared (id INT PRIMARY KEY, v VARCHAR(20), w VARCHAR(20), f FLOAT)");
        await ExecAsync($"INSERT INTO `{_database}`.shared VALUES (1, 'a', 'x', 0.1), (2, 'b', 'y', 0.2), (3, 'c', 'z', 0.3)");
        var table = await TableAsync("shared");
        var writer = new RowWriter(_host);
        await using var other = new MySqlConnection(TestServer.ConnectionString);
        await other.OpenAsync(Ct);
        async Task OtherAsync(string sql)
        {
            await using var cmd = new MySqlCommand(sql, other);
            await cmd.ExecuteNonQueryAsync(Ct);
        }
        RowChange Change(string id, string column, string expected, string value) =>
            new(RowChangeKind.Update, Values(("id", id)), Values((column, value))) { Expected = Values((column, expected)) };

        // Another session changes v after the grid read 'a': saving stops and does not wipe out its change.
        await OtherAsync($"UPDATE `{_database}`.shared SET v = 'outra' WHERE id = 1");
        await Assert.ThrowsAsync<RowConflictException>(() => writer.WriteAsync(table, Change("1", "v", "a", "minha"), Ct));
        Assert.Equal("outra", await ScalarAsync($"SELECT v FROM `{_database}`.shared WHERE id = 1"));
        // Overwrite (the user confirmed): no check.
        await writer.WriteAsync(table, Change("1", "v", "a", "minha") with { Expected = null }, Ct);
        Assert.Equal("minha", await ScalarAsync($"SELECT v FROM `{_database}`.shared WHERE id = 1"));

        // The other session saved the same value: there is no conflict.
        await OtherAsync($"UPDATE `{_database}`.shared SET v = 'igual' WHERE id = 2");
        await writer.WriteAsync(table, Change("2", "v", "b", "igual"), Ct);

        // The other session changed another column: the save goes through and does not touch it.
        await OtherAsync($"UPDATE `{_database}`.shared SET w = 'dela' WHERE id = 3");
        await writer.WriteAsync(table, Change("3", "v", "c", "nova"), Ct);
        Assert.Equal("nova|dela", await ScalarAsync($"SELECT CONCAT(v, '|', w) FROM `{_database}`.shared WHERE id = 3"));

        // FLOAT is left out of the check (the text read rounds): saves without a false conflict.
        await writer.WriteAsync(table, Change("3", "f", "0.3", "0.5"), Ct);

        // Row deleted by another session: error, nothing saved.
        await OtherAsync($"DELETE FROM `{_database}`.shared WHERE id = 2");
        await Assert.ThrowsAsync<RowWriteException>(() => writer.WriteAsync(table, Change("2", "v", "igual", "x"), Ct));
    }

    [Fact]
    public async Task SetDoSqlModeNaQueryTabMudaOEscapingDosComentarios()
    {
        TestServer.RequireServer();
        var runner = new QueryRunner(_host);
        var sql = "SET SESSION sql_mode = CONCAT(@@SESSION.sql_mode, ',NO_BACKSLASH_ESCAPES')";
        await runner.RunAsync(sql, SqlSplitter.Split(sql), new QueryRunOptions(), Ct);
        var services = _host.ServerServices!;
        Assert.Equal(@"'a\b'", services.Dialect.PlainStringLiteral(@"a\b"));

        // A COMMENT with backslashes generated by the DDL composer stays exactly as written.
        var comment = @"C:\dir 'x'";
        await ExecAsync($"CREATE TABLE `{_database}`.commented (id INT) COMMENT {services.Dialect.PlainStringLiteral(comment)}");
        Assert.Equal(comment, await ScalarAsync(
            $"SELECT TABLE_COMMENT FROM information_schema.TABLES WHERE TABLE_SCHEMA = '{_database}' AND TABLE_NAME = 'commented'"));

        var back = "SET SESSION sql_mode = REPLACE(@@SESSION.sql_mode, 'NO_BACKSLASH_ESCAPES', '')";
        await runner.RunAsync(back, SqlSplitter.Split(back), new QueryRunOptions(), Ct);
        Assert.Equal(@"'a\\b'", _host.ServerServices!.Dialect.PlainStringLiteral(@"a\b"));
    }
}
