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

    private static DbObjectRef Ref(string name, string db = "loja") => new(db, db, name, DbObjectKind.Table);

    private static List<TableDefinition> Shop() =>
    [
        new(Ref("encomendas"), [Column("id", "int", LogicalTypeKind.Integer), Column("cliente_id", "int", LogicalTypeKind.Integer), Column("criada", "datetime", LogicalTypeKind.DateTime, true)],
            [new IndexDefinition("PRIMARY", IndexKind.Primary, ["id"])],
            [
                new ForeignKeyDefinition("fk_cliente", ["cliente_id"], Ref("clientes"), ["id"], ForeignKeyAction.Restrict, ForeignKeyAction.Restrict),
                new ForeignKeyDefinition("fk_fora", ["cliente_id"], Ref("x", "outra"), ["id"], ForeignKeyAction.Restrict, ForeignKeyAction.Restrict),
            ],
            [], new Dictionary<string, string>()),
        new(Ref("clientes"), [Column("id", "int", LogicalTypeKind.Integer), Column("email", "varchar(200)", LogicalTypeKind.Text, true)],
            [new IndexDefinition("PRIMARY", IndexKind.Primary, ["id"]), new IndexDefinition("uq_email", IndexKind.Unique, ["email"])], [], [], new Dictionary<string, string>()),
        new(Ref("perfis"), [Column("cliente_id", "int", LogicalTypeKind.Integer), Column("bio", "text", LogicalTypeKind.Text, true)],
            [new IndexDefinition("PRIMARY", IndexKind.Primary, ["cliente_id"])],
            [new ForeignKeyDefinition("fk_perfil", ["cliente_id"], Ref("clientes"), ["id"], ForeignKeyAction.Cascade, ForeignKeyAction.Cascade)],
            [], new Dictionary<string, string>()),
    ];

    [Fact]
    public void TabelasColunasERelacoesComCardinalidade()
    {
        var diagram = SchemaDiagram.From("loja", Shop());

        Assert.Equal(["clientes", "encomendas", "perfis"], diagram.Tables.Select(t => t.Name));
        var clientes = diagram.Tables[0];
        Assert.Equal(new DiagramColumn("id", "int", true, false, false, false, TypeCategory.Integer), clientes.Columns[0]);
        Assert.Equal(new DiagramColumn("email", "varchar(200)", false, true, false, true, TypeCategory.Text), clientes.Columns[1]);
        Assert.True(diagram.Tables[1].Columns[1].ForeignKey);
        Assert.Equal(TypeCategory.Temporal, diagram.Tables[1].Columns[2].Category);

        // The FK to another database is left out; the profile one is 1:1 (the FK columns are the primary key).
        Assert.Equal(2, diagram.Relations.Count);
        var cliente = diagram.Relations.Single(r => r.Name == "fk_cliente");
        Assert.False(cliente.OneToOne);
        Assert.Equal(("encomendas", "clientes"), (cliente.Table, cliente.ReferencedTable));
        Assert.True(diagram.Relations.Single(r => r.Name == "fk_perfil").OneToOne);
    }

    [Fact]
    public void HashMudaComOSchemaENaoComAOrdem()
    {
        var a = SchemaDiagram.From("loja", Shop());
        var b = SchemaDiagram.From("loja", Enumerable.Reverse(Shop()));
        var tables = Shop();
        tables[1] = tables[1] with { Columns = [.. tables[1].Columns, Column("nome", "varchar(50)", LogicalTypeKind.Text)] };
        var c = SchemaDiagram.From("loja", tables);

        Assert.Equal(a.Hash, b.Hash);
        Assert.NotEqual(a.Hash, c.Hash);
        Assert.Equal(64, a.Hash.Length);
    }

    [Fact]
    public void LayoutGuardadoPorSessaoEBase()
    {
        var database = new MemoryAppDatabase();
        var state = new DiagramLayoutState("abc", new Dictionary<string, DiagramNodeState>
        {
            ["clientes"] = new(10.26, -20, false),
            ["perfis"] = new(300, 40, true),
        }, 0.75, 12, -8);
        state.Save(database, "local", "loja");

        var loaded = DiagramLayoutState.Load(database, "local", "loja");
        Assert.NotNull(loaded);
        Assert.Equal("abc", loaded.Hash);
        Assert.Equal(new DiagramNodeState(10.3, -20, false), loaded.Nodes["clientes"]);
        Assert.True(loaded.Nodes["perfis"].Hidden);
        Assert.Equal((0.75, 12, -8), (loaded.Zoom, loaded.OffsetX, loaded.OffsetY));
        Assert.Null(DiagramLayoutState.Load(database, "local", "outra"));

        database.SetTableState(new TableKey("local", "loja", string.Empty), "diagram.layout", "{nao e json");
        Assert.Null(DiagramLayoutState.Load(database, "local", "loja"));
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
