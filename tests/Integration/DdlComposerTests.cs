using System.Globalization;
using Eunomia.Drivers.Abstractions;
using Eunomia.Drivers.MySql;
using Eunomia.Engine.Execution;
using Eunomia.Engine.Metadata;
using MySqlConnector;

namespace Eunomia.Integration.Tests;

/// <summary>Table editor CREATE and ALTER against the server (phase 7; appendix C §5.3; FAM-E02).</summary>
public sealed class DdlComposerTests : IAsyncLifetime
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
        _database = "eunomia_ddl_" + Guid.NewGuid().ToString("N")[..8];
        await ExecAsync($"CREATE DATABASE `{_database}`");
        await ExecAsync($"CREATE TABLE `{_database}`.parent (id INT PRIMARY KEY) ENGINE=InnoDB");
        await ExecAsync($"CREATE TABLE `{_database}`.t (id INT UNSIGNED NOT NULL AUTO_INCREMENT, " +
            "name VARCHAR(40) COLLATE utf8mb4_unicode_ci NOT NULL DEFAULT 'sem nome' COMMENT 'o nome', " +
            "price DECIMAL(10,2) NULL DEFAULT 0.00, created TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP, " +
            "doubled DECIMAL(12,2) AS (price * 2) VIRTUAL, notes TEXT NULL, parent INT NULL, gone INT NULL, " +
            "PRIMARY KEY (id), UNIQUE INDEX uq_name (name(10)), INDEX ix_price (price), " +
            "CONSTRAINT fk_parent FOREIGN KEY (parent) REFERENCES parent (id) ON DELETE SET NULL) " +
            "ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='tabela'");
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await ExecAsync($"DROP DATABASE IF EXISTS `{_database}`");
            await _host.DisposeAsync();
        }
    }

    private Task ExecAsync(string sql) => _host.RunPrimaryAsync((h, ct) => h.ExecuteNonQueryAsync(sql, ct), CancellationToken.None);

    private Task<TableDefinition> ReadAsync(string name) =>
        new MetadataCache(_host, new MetadataOptions()).GetTableAsync(new DbObjectRef(_database, _database, name, DbObjectKind.Table), Ct);

    private async Task<string?> ScalarAsync(string sql) =>
        Convert.ToString(await _host.RunPrimaryAsync((h, ct) => h.ExecuteScalarAsync(sql, ct), Ct), CultureInfo.InvariantCulture);

    private MySqlDdlComposer Ddl => (MySqlDdlComposer)_host.ServerServices!.Ddl;

    [Fact]
    public async Task ExpressoesComPlicaEBarraSobrevivemALeitura()
    {
        TestServer.RequireServer();
        // Generated column, expression default and CHECK with a quote and a backslash in the literals: MySQL 8 returns them escaped
        // in information_schema (bug #100607); the CREATE built from the read must give the same table.
        await ExecAsync($"CREATE TABLE `{_database}`.expr (id INT PRIMARY KEY, a VARCHAR(20) NOT NULL DEFAULT 'a', " +
            @"g VARCHAR(60) AS (CONCAT(a, 'it''s \\ ok')) VIRTUAL, d VARCHAR(20) DEFAULT (CONCAT('x', '''y')), " +
            "CONSTRAINT no_quote CHECK (a <> 'it''s')) ENGINE=InnoDB");
        var table = await ReadAsync("expr");
        var copy = table with
        {
            Ref = table.Ref with { Name = "expr_copy" },
            // On MySQL, CHECK names are unique in the database.
            Checks = [.. table.Checks.Select(c => c with { Name = "no_quote_copy" })],
        };
        foreach (var sql in Ddl.Create(copy))
        {
            await ExecAsync(sql);
        }

        await ExecAsync($"INSERT INTO `{_database}`.expr (id, a) VALUES (1, 'z')");
        await ExecAsync($"INSERT INTO `{_database}`.expr_copy (id, a) VALUES (1, 'z')");
        var original = await ScalarAsync($"SELECT CONCAT_WS('|', g, d) FROM `{_database}`.expr");
        Assert.Equal(original, await ScalarAsync($"SELECT CONCAT_WS('|', g, d) FROM `{_database}`.expr_copy"));
        Assert.StartsWith("zit's ", original, StringComparison.Ordinal);
        Assert.EndsWith("|x'y", original, StringComparison.Ordinal);
        await Assert.ThrowsAnyAsync<Exception>(() => ExecAsync($"INSERT INTO `{_database}`.expr_copy (id, a) VALUES (2, 'it''s')"));
        var reread = await ReadAsync("expr_copy");
        Assert.Equal(Ddl.Create(copy)[0], Ddl.Create(reread with { Ref = copy.Ref })[0]);
    }

    [Fact]
    public async Task LeituraNormalizadaECreateQueReproduzATabela()
    {
        TestServer.RequireServer();
        var table = await ReadAsync("t");
        var name = table.Columns.Single(c => c.Name == "name");
        Assert.Equal("'sem nome'", name.DefaultExpression);
        Assert.False(name.DefaultIsExpression);
        Assert.Equal("utf8mb4_unicode_ci", name.Type.Collation);
        var created = table.Columns.Single(c => c.Name == "created");
        Assert.True(created.DefaultIsExpression);
        Assert.StartsWith("current_timestamp", created.OnUpdate!, StringComparison.OrdinalIgnoreCase);
        var doubled = table.Columns.Single(c => c.Name == "doubled");
        Assert.True(doubled.IsGenerated);
        Assert.Contains("price", doubled.GenerationExpression!, StringComparison.Ordinal);
        Assert.Equal([10], table.Indexes.Single(i => i.Name == "uq_name").SubParts!);
        Assert.Equal("NULL", table.Columns.Single(c => c.Name == "notes").DefaultExpression);

        // The generated CREATE, run under another name, gives the same definition.
        var copy = table with { Ref = table.Ref with { Name = "t_copy" }, ForeignKeys = [] };
        foreach (var sql in Ddl.Create(copy))
        {
            await ExecAsync(sql);
        }
        var reread = await ReadAsync("t_copy");
        Assert.Equal(Ddl.Create(copy)[0], Ddl.Create(reread with { Ref = copy.Ref })[0]);
    }

    [Fact]
    public async Task AlterPorDiffDeixaATabelaComoPedido()
    {
        TestServer.RequireServer();
        var before = await ReadAsync("t");
        var columns = before.Columns.ToList();
        var name = columns.Single(c => c.Name == "name");
        var notes = columns.Single(c => c.Name == "notes");
        columns.Remove(columns.Single(c => c.Name == "gone"));
        columns[columns.IndexOf(name)] = name with { Name = "title", NativeType = "varchar(60)", Comment = "o título" };
        // notes moves to after id; new column at the end.
        columns.Remove(notes);
        columns.Insert(1, notes with { IsNullable = false, DefaultExpression = null });
        // No display width: MySQL 8 removes it from integers and MariaDB does not.
        columns.Add(new ColumnDefinition("rating", "decimal(5,1)", new LogicalType(LogicalTypeKind.Decimal, Precision: 5, Scale: 1), true, "5.0", false, false, false, null));
        var indexes = before.Indexes.Where(i => i.Name != "ix_price")
            .Select(i => i.Name == "uq_name" ? i with { Columns = ["title"], SubParts = [20] } : i)
            .Append(new IndexDefinition("ix_rating", IndexKind.Key, ["rating", "price"]))
            .ToList();
        // The index InnoDB creates for the foreign key is still there.
        var after = before with
        {
            Ref = before.Ref with { Name = "t2" },
            Columns = columns,
            Indexes = indexes,
            ForeignKeys = [before.ForeignKeys.Single() with { OnDelete = ForeignKeyAction.Cascade }],
            Options = new Dictionary<string, string>(before.Options, StringComparer.OrdinalIgnoreCase) { ["Comment"] = "renomeada" },
        };

        var statements = Ddl.Alter(before, after);
        Assert.StartsWith($"ALTER TABLE `{_database}`.`t`\n\tDROP FOREIGN KEY `fk_parent`", statements[0], StringComparison.Ordinal);
        Assert.Contains("CHANGE COLUMN `name` `title` varchar(60)", statements[1], StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN `rating` decimal(5,1) NULL DEFAULT 5.0 AFTER `parent`", statements[1], StringComparison.Ordinal);
        Assert.Contains("DROP COLUMN `gone`", statements[1], StringComparison.Ordinal);
        Assert.StartsWith("RENAME TABLE", statements[^1], StringComparison.Ordinal);
        foreach (var sql in statements)
        {
            await ExecAsync(sql);
        }

        var actual = await ReadAsync("t2");
        // The server reorders the indexes after dropping and creating them: they are compared by each one's definition.
        // MySQL 8 caches the information_schema statistics (information_schema_stats_expiry) and only refreshes them
        // after the ALTER: AUTO_INCREMENT shows on one side and not on the other. They are compared without it.
        static TableDefinition Sorted(TableDefinition t) => t with
        {
            Indexes = [.. t.Indexes.OrderBy(i => i.Name, StringComparer.Ordinal)],
            Options = t.Options.Where(o => !o.Key.Equals("Auto_increment", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(o => o.Key, o => o.Value, StringComparer.OrdinalIgnoreCase),
        };
        Assert.Equal(Ddl.Create(Sorted(after))[0], Ddl.Create(Sorted(actual))[0]);
        Assert.Empty(Ddl.Alter(actual, actual));
    }
}
