using Eunomia.Drivers.Abstractions;
using Eunomia.Drivers.MySql;

namespace Eunomia.Integration.Tests;

/// <summary>MySQL/MariaDB metadata against a real server (basis of the tree, phase 4, and of the table editor, phase 7).</summary>
public sealed class MySqlMetadataTests : IAsyncLifetime
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
        var h = (MySqlConnectionHandle)_db;
        await h.ExecuteNonQueryAsync($"USE `{_database}`", Ct);
        string[] ddl =
        [
            "CREATE TABLE parent (id INT NOT NULL AUTO_INCREMENT PRIMARY KEY, code VARCHAR(10) NOT NULL, UNIQUE KEY uq_code (code)) COMMENT='pais'",
            """
            CREATE TABLE child (
              id INT NOT NULL, seq SMALLINT UNSIGNED NOT NULL, parent_id INT NULL, price DECIMAL(10,2) NOT NULL DEFAULT 0.00 COMMENT 'preço',
              note TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL,
              PRIMARY KEY (id, seq), KEY ix_parent (parent_id, seq), FULLTEXT KEY ft_note (note),
              CONSTRAINT fk_parent FOREIGN KEY (parent_id) REFERENCES parent (id) ON DELETE CASCADE ON UPDATE SET NULL,
              CONSTRAINT chk_price CHECK (price >= 0)) ENGINE=InnoDB
            """,
            "INSERT INTO parent (code) VALUES ('a'), ('b')",
            "CREATE VIEW v_parent AS SELECT id, code FROM parent",
            "CREATE PROCEDURE p_hello(IN n INT) COMMENT 'olá' SELECT n",
            "CREATE FUNCTION f_double(n INT) RETURNS INT DETERMINISTIC RETURN n * 2",
            "CREATE TRIGGER trg_parent BEFORE INSERT ON parent FOR EACH ROW SET NEW.code = UPPER(NEW.code)",
            "CREATE EVENT ev_noop ON SCHEDULE EVERY 1 DAY DISABLE DO SELECT 1",
        ];
        foreach (var sql in ddl)
        {
            await h.ExecuteNonQueryAsync(sql, Ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_db is not null)
        {
            await TestServer.DropDatabaseAsync(_db, _database);
            await _db.DisposeAsync();
        }
    }

    [Fact]
    public async Task SchemaInteiroParaODiagrama()
    {
        TestServer.RequireServer();
        var tables = await _db.Metadata.GetSchemaAsync(_database, _database, Ct);

        // Only tables (the view is left out), in name order, with the columns in table order.
        Assert.Equal(["child", "parent"], tables.Select(t => t.Ref.Name));
        var child = tables[0];
        Assert.Equal(["id", "seq", "parent_id", "price", "note"], child.Columns.Select(c => c.Name));
        Assert.True(child.Columns.Single(c => c.Name == "parent_id").IsNullable);
        Assert.Equal(["id", "seq"], child.Indexes.Single(i => i.Kind == IndexKind.Primary).Columns);
        // Only unique keys (normal indexes and FULLTEXT do not matter to the diagram).
        Assert.Single(child.Indexes);
        var fk = Assert.Single(child.ForeignKeys);
        Assert.Equal(("fk_parent", "parent"), (fk.Name, fk.ReferencedTable.Name));
        Assert.Equal(["parent_id"], fk.Columns);
        Assert.Equal(["id"], fk.ReferencedColumns);
        var parent = tables[1];
        Assert.Equal(["code"], parent.Indexes.Single(i => i.Kind == IndexKind.Unique).Columns);
        Assert.True(parent.Columns.Single(c => c.Name == "id").IsAutoIncrement);
    }

    [Fact]
    public async Task ListaAsBases()
    {
        TestServer.RequireServer();

        var catalogs = await _db.Metadata.GetCatalogsAsync(Ct);

        Assert.Contains(_database, catalogs);
        Assert.Contains("information_schema", catalogs);
    }

    [Theory]
    [InlineData(ObjectDetail.Statistics)]
    [InlineData(ObjectDetail.Details)]
    [InlineData(ObjectDetail.Names)]
    public async Task TodosOsObjetosNumaSoIda(ObjectDetail detail)
    {
        TestServer.RequireServer();

        var objects = await _db.Metadata.GetObjectsAsync(_database, _database, detail, Ct);

        var byName = objects.ToDictionary(o => o.Ref.Name);
        Assert.Equal(DbObjectKind.Table, byName["parent"].Ref.Kind);
        Assert.Equal(DbObjectKind.Table, byName["child"].Ref.Kind);
        Assert.Equal(DbObjectKind.View, byName["v_parent"].Ref.Kind);
        Assert.Equal(DbObjectKind.Procedure, byName["p_hello"].Ref.Kind);
        Assert.Equal(DbObjectKind.Function, byName["f_double"].Ref.Kind);
        Assert.Equal(DbObjectKind.Trigger, byName["trg_parent"].Ref.Kind);
        Assert.Equal(DbObjectKind.Event, byName["ev_noop"].Ref.Kind);
        Assert.Equal("olá", byName["p_hello"].Comment);
        if (detail == ObjectDetail.Names)
        {
            // Only names and kinds: nothing that forces the server to open the tables.
            Assert.Null(byName["parent"].Engine);
            Assert.Null(byName["parent"].Status);
            return;
        }
        Assert.Equal("pais", byName["parent"].Comment);
        Assert.Equal("InnoDB", byName["child"].Engine);
        Assert.NotNull(byName["parent"].Status);
        if (detail == ObjectDetail.Statistics)
        {
            Assert.NotNull(byName["parent"].SizeBytes);
            Assert.True(byName["parent"].SizeBytes > 0);
        }
        else
        {
            Assert.Null(byName["parent"].RowsEstimate);
        }
    }

    [Fact]
    public async Task EstruturaDaTabelaComChavesFkECheck()
    {
        TestServer.RequireServer();

        var t = await _db.Metadata.GetTableAsync(new DbObjectRef(_database, _database, "child", DbObjectKind.Table), Ct);

        Assert.Equal(["id", "seq", "parent_id", "price", "note"], t.Columns.Select(c => c.Name));
        var seq = t.Columns[1];
        Assert.Equal(LogicalTypeKind.Integer, seq.Type.Kind);
        Assert.Equal(16, seq.Type.Bits);
        Assert.False(seq.Type.Signed);
        Assert.False(seq.IsNullable);
        var price = t.Columns[3];
        Assert.Equal((10, 2), (price.Type.Precision!.Value, price.Type.Scale!.Value));
        Assert.Equal("preço", price.Comment);
        Assert.Equal("utf8mb4_unicode_ci", t.Columns[4].Type.Collation);

        var pk = Assert.Single(t.Indexes, i => i.Kind == IndexKind.Primary);
        Assert.Equal(["id", "seq"], pk.Columns);
        Assert.Equal(["parent_id", "seq"], t.Indexes.Single(i => i.Name == "ix_parent").Columns);
        Assert.Equal(IndexKind.FullText, t.Indexes.Single(i => i.Name == "ft_note").Kind);

        var fk = Assert.Single(t.ForeignKeys);
        Assert.Equal("fk_parent", fk.Name);
        Assert.Equal("parent", fk.ReferencedTable.Name);
        Assert.Equal(["id"], fk.ReferencedColumns);
        Assert.Equal(ForeignKeyAction.Cascade, fk.OnDelete);
        Assert.Equal(ForeignKeyAction.SetNull, fk.OnUpdate);

        var check = Assert.Single(t.Checks);
        Assert.Equal("chk_price", check.Name);
        Assert.Contains("price", check.Expression, StringComparison.Ordinal);
        Assert.Equal("InnoDB", t.Options["Engine"]);

        var parent = await _db.Metadata.GetTableAsync(new DbObjectRef(_database, _database, "parent", DbObjectKind.Table), Ct);
        Assert.True(parent.Columns[0].IsAutoIncrement);
        Assert.Equal(IndexKind.Unique, parent.Indexes.Single(i => i.Name == "uq_code").Kind);
    }

    [Theory]
    [InlineData("child", DbObjectKind.Table, "CREATE TABLE")]
    [InlineData("v_parent", DbObjectKind.View, "VIEW")]
    [InlineData("p_hello", DbObjectKind.Procedure, "PROCEDURE")]
    [InlineData("f_double", DbObjectKind.Function, "FUNCTION")]
    [InlineData("trg_parent", DbObjectKind.Trigger, "TRIGGER")]
    [InlineData("ev_noop", DbObjectKind.Event, "EVENT")]
    public async Task CreateCode(string name, DbObjectKind kind, string expected)
    {
        TestServer.RequireServer();

        var code = await _db.Metadata.GetCreateCodeAsync(new DbObjectRef(_database, _database, name, kind), Ct);

        Assert.Contains(expected, code, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(name, code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessosEVariaveis()
    {
        TestServer.RequireServer();

        var processes = await _db.Metadata.GetProcessesAsync(Ct);
        var variables = await _db.Metadata.GetVariablesAsync(session: true, Ct);

        Assert.Contains(processes, p => p.Id == _db.ServerConnectionId);
        Assert.Contains(variables, v => v.Name == "sql_mode");
    }

    [Fact]
    public async Task DdlCreateReproduzATabela()
    {
        TestServer.RequireServer();
        var services = new MySqlProvider().ForServer(_db.Server);
        var child = await _db.Metadata.GetTableAsync(new DbObjectRef(_database, _database, "child", DbObjectKind.Table), Ct);
        // On MySQL 8, CHECK names are unique per database: the copy gets them under another name.
        var copy = child with { Ref = child.Ref with { Name = "child_copy" }, ForeignKeys = [], Checks = [.. child.Checks.Select(c => c with { Name = c.Name + "_copy" })] };

        await ((MySqlConnectionHandle)_db).ExecuteNonQueryAsync(services.Ddl.Create(copy)[0], Ct);
        var back = await _db.Metadata.GetTableAsync(copy.Ref, Ct);

        Assert.Equal(child.Columns.Select(c => (c.Name, c.NativeType, c.IsNullable)), back.Columns.Select(c => (c.Name, c.NativeType, c.IsNullable)));
        Assert.Equal(child.Indexes.Select(i => (i.Name, i.Kind)), back.Indexes.Select(i => (i.Name, i.Kind)));
        Assert.Equal("DROP TABLE `" + _database + "`.`child_copy`", services.Ddl.Drop(copy.Ref));
    }
}
