using Eunomia.App.Diagram;
using Eunomia.App.Settings;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Results;

namespace Eunomia.App.Tests;

/// <summary>Schema Visualizer model from the metadata, and layout persistence (phase 9; SV-01–SV-03, SV-12).</summary>
public sealed class SchemaDiagramTests
{
    private static ColumnDefinition Column(string name, string type, LogicalTypeKind kind, bool nullable = false) =>
        new(name, type, new LogicalType(kind), nullable, null, false, false, false, null);

    private static DbObjectRef Ref(string name, string db = "shop") => new(db, db, name, DbObjectKind.Table);

    private static List<TableDefinition> Shop() =>
    [
        new(Ref("orders"), [Column("id", "int", LogicalTypeKind.Integer), Column("customer_id", "int", LogicalTypeKind.Integer), Column("created", "datetime", LogicalTypeKind.DateTime, true)],
            [new IndexDefinition("PRIMARY", IndexKind.Primary, ["id"])],
            [
                new ForeignKeyDefinition("fk_customer", ["customer_id"], Ref("customers"), ["id"], ForeignKeyAction.Restrict, ForeignKeyAction.Restrict),
                new ForeignKeyDefinition("fk_outside", ["customer_id"], Ref("x", "other"), ["id"], ForeignKeyAction.Restrict, ForeignKeyAction.Restrict),
            ],
            [], new Dictionary<string, string>()),
        new(Ref("customers"), [Column("id", "int", LogicalTypeKind.Integer), Column("email", "varchar(200)", LogicalTypeKind.Text, true)],
            [new IndexDefinition("PRIMARY", IndexKind.Primary, ["id"]), new IndexDefinition("uq_email", IndexKind.Unique, ["email"])], [], [], new Dictionary<string, string>()),
        new(Ref("profiles"), [Column("customer_id", "int", LogicalTypeKind.Integer), Column("bio", "text", LogicalTypeKind.Text, true)],
            [new IndexDefinition("PRIMARY", IndexKind.Primary, ["customer_id"])],
            [new ForeignKeyDefinition("fk_profile", ["customer_id"], Ref("customers"), ["id"], ForeignKeyAction.Cascade, ForeignKeyAction.Cascade)],
            [], new Dictionary<string, string>()),
    ];

    [Fact]
    public void TablesColumnsAndRelationsWithCardinality()
    {
        var diagram = SchemaDiagram.From("shop", Shop());

        Assert.Equal(["customers", "orders", "profiles"], diagram.Tables.Select(t => t.Name));
        var customers = diagram.Tables[0];
        Assert.Equal(new DiagramColumn("id", "int", true, false, false, false, TypeCategory.Integer), customers.Columns[0]);
        Assert.Equal(new DiagramColumn("email", "varchar(200)", false, true, false, true, TypeCategory.Text), customers.Columns[1]);
        Assert.True(diagram.Tables[1].Columns[1].ForeignKey);
        Assert.Equal(TypeCategory.Temporal, diagram.Tables[1].Columns[2].Category);

        // The FK to another database is left out; the profile one is 1:1 (the FK columns are the primary key).
        Assert.Equal(2, diagram.Relations.Count);
        var cliente = diagram.Relations.Single(r => r.Name == "fk_customer");
        Assert.False(cliente.OneToOne);
        Assert.Equal(("orders", "customers"), (cliente.Table, cliente.ReferencedTable));
        Assert.True(diagram.Relations.Single(r => r.Name == "fk_profile").OneToOne);
    }

    [Fact]
    public void HashChangesWithTheSchemaAndNotWithTheOrder()
    {
        var a = SchemaDiagram.From("shop", Shop());
        var b = SchemaDiagram.From("shop", Enumerable.Reverse(Shop()));
        var tables = Shop();
        tables[1] = tables[1] with { Columns = [.. tables[1].Columns, Column("name", "varchar(50)", LogicalTypeKind.Text)] };
        var c = SchemaDiagram.From("shop", tables);

        Assert.Equal(a.Hash, b.Hash);
        Assert.NotEqual(a.Hash, c.Hash);
        Assert.Equal(64, a.Hash.Length);
    }

    [Fact]
    public void LayoutSavedPerSessionAndDatabase()
    {
        var database = new MemoryAppDatabase();
        var state = new DiagramLayoutState("abc", new Dictionary<string, DiagramNodeState>
        {
            ["customers"] = new(10.26, -20, false),
            ["profiles"] = new(300, 40, true),
        }, 0.75, 12, -8);
        state.Save(database, "local", "shop");

        var loaded = DiagramLayoutState.Load(database, "local", "shop");
        Assert.NotNull(loaded);
        Assert.Equal("abc", loaded.Hash);
        Assert.Equal(new DiagramNodeState(10.3, -20, false), loaded.Nodes["customers"]);
        Assert.True(loaded.Nodes["profiles"].Hidden);
        Assert.Equal((0.75, 12, -8), (loaded.Zoom, loaded.OffsetX, loaded.OffsetY));
        Assert.Null(DiagramLayoutState.Load(database, "local", "other"));

        database.SetTableState(new TableKey("local", "shop", string.Empty), "diagram.layout", "{not json");
        Assert.Null(DiagramLayoutState.Load(database, "local", "shop"));
    }

    private sealed class MemoryAppDatabase : IAppDatabase
    {
        private readonly Dictionary<(TableKey, string), string> _state = [];

        public string? GetTableState(TableKey table, string name) => _state.GetValueOrDefault((table, name));

        public void SetTableState(TableKey table, string name, string? value)
        {
            if (value is null)
            {
                _state.Remove((table, name));
            }
            else
            {
                _state[(table, name)] = value;
            }
        }

        public IReadOnlyList<string> GetRecentFilters(TableKey table) => [];

        public void AddRecentFilter(TableKey table, string filter, int max = 20)
        {
        }

        public void ClearRecentFilters(TableKey table)
        {
        }

        public IReadOnlyList<string> GetFavorites(string session) => [];

        public void SetFavorite(string session, string path, bool favorite)
        {
        }

        public long AddHistory(string session, string? database, string sql, DateTimeOffset executedAt, TimeSpan duration) => 0;

        public IReadOnlyList<QueryHistoryEntry> GetHistory(string session, int limit = 1000) => [];

        public void DeleteHistory(long id)
        {
        }

        public void ClearHistory(string session)
        {
        }

        public void Dispose()
        {
        }
    }
}
