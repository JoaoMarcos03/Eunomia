using Eunomia.App.Objects;
using Eunomia.Drivers.Abstractions;
using Eunomia.Drivers.MySql;

namespace Eunomia.App.Tests;

/// <summary>Editable model of the table editor (appendix C §5; FAM-E01–E03).</summary>
public sealed class TableDesignTests
{
    private static readonly ServerIdentity Maria = new(DbEngine.MariaDb, new Version(10, 4, 32), "10.4.32-MariaDB", null);
    private static readonly MySqlDialect Dialect = new(Maria);
    private static readonly MySqlTypeMapper Types = new();
    private static readonly MySqlDdlComposer Ddl = new(Dialect);

    private static ColumnDefinition Column(string name, string native, bool nullable, string? defaultSql, bool expression = false) =>
        new(name, native, Types.Map(native), nullable, defaultSql, false, false, false, null) { DefaultIsExpression = expression, OriginalName = name };

    private static TableDefinition Sample() => new(
        new DbObjectRef("shop", "shop", "items", DbObjectKind.Table),
        [
            new ColumnDefinition("id", "int(10) unsigned", Types.Map("int(10) unsigned"), false, null, true, false, false, null) { OriginalName = "id" },
            Column("name", "varchar(40)", false, "'it''s'") with { Comment = "o nome", Type = Types.Map("varchar(40)") with { Collation = "utf8mb4_unicode_ci" } },
            Column("price", "decimal(10,2)", true, "0.00"),
            Column("created", "timestamp", false, "current_timestamp()", expression: true) with { OnUpdate = "current_timestamp()" },
            Column("notes", "text", true, "NULL"),
            new ColumnDefinition("doubled", "int(11)", Types.Map("int(11)"), true, null, false, true, false, null) { GenerationExpression = "`id` * 2", IsStored = true, OriginalName = "doubled" },
        ],
        [new IndexDefinition("PRIMARY", IndexKind.Primary, ["id"]), new IndexDefinition("uq_name", IndexKind.Unique, ["name"]) { SubParts = [10] }],
        [new ForeignKeyDefinition("fk_x", ["price"], new DbObjectRef("other", "other", "prices", DbObjectKind.Table), ["value"], ForeignKeyAction.Restrict, ForeignKeyAction.Cascade)],
        [new CheckDefinition("ck_price", "`price` >= 0")],
        new Dictionary<string, string> { ["Engine"] = "InnoDB", ["Collation"] = "utf8mb4_unicode_ci", ["Comment"] = "itens", ["Create_options"] = "checksum=1 max_rows=100" });

    [Fact]
    public void VaiEVemSemAlteracoes()
    {
        var original = Sample();
        var design = TableDesign.From(original);

        var id = design.Columns[0];
        Assert.Equal(("INT", "10", true, DefaultKind.AutoIncrement), (id.DataType, id.LengthSet, id.Unsigned, id.DefaultKind));
        Assert.Equal((DefaultKind.Text, "it's"), (design.Columns[1].DefaultKind, design.Columns[1].DefaultText));
        Assert.Equal("'it's'", design.Columns[1].DefaultCaption);
        Assert.Equal("current_timestamp() ON UPDATE current_timestamp()", design.Columns[3].DefaultCaption);
        Assert.Equal(DefaultKind.Null, design.Columns[4].DefaultKind);
        Assert.Equal(("`id` * 2", "STORED"), (design.Columns[5].Expression, design.Columns[5].Virtuality));
        Assert.Equal("name(10)", design.Indexes[1].Columns);
        Assert.Equal("other.prices", design.ForeignKeys[0].ReferenceTable);
        Assert.Equal(("1", "100"), (design.Checksum, design.MaxRows));

        var again = design.ToDefinition(Types, Dialect);
        Assert.Equal(original.Columns.Select(Ddl.ColumnSpec), again.Columns.Select(Ddl.ColumnSpec));
        Assert.Equal(original.Indexes.Select(Ddl.IndexSpec), again.Indexes.Select(Ddl.IndexSpec));
        Assert.Equal(original.ForeignKeys.Select(Ddl.ForeignKeySpec), again.ForeignKeys.Select(Ddl.ForeignKeySpec));
        Assert.Empty(Ddl.Alter(again, TableDesign.From(original).ToDefinition(Types, Dialect)));
    }

    [Fact]
    public void DefaultComBarraVemEmHexEVoltaATexto()
    {
        // The metadata gives defaults with backslashes as _utf8mb4 X'…' (valid under any sql_mode); the editor shows the text.
        var table = Sample() with { Columns = [Column("path", "varchar(40)", false, "_utf8mb4 X'433A5C78'")] };
        var design = TableDesign.From(table);
        Assert.Equal((DefaultKind.Text, @"C:\x"), (design.Columns[0].DefaultKind, design.Columns[0].DefaultText));
        var again = design.ToDefinition(Types, Dialect);
        Assert.Equal("_utf8mb4 X'433A5C78'", again.Columns[0].DefaultExpression);
        Assert.Empty(Ddl.Alter(again, TableDesign.From(table).ToDefinition(Types, Dialect)));
    }

    [Fact]
    public void AlteracoesDoEditorDaoOAlter()
    {
        var before = TableDesign.From(Sample()).ToDefinition(Types, Dialect);
        var design = TableDesign.From(Sample());
        design.Columns[2].DefaultKind = DefaultKind.Text;
        design.Columns[2].DefaultText = "1.5";
        design.Columns.Insert(1, new ColumnDesign { Name = "sku", DataType = "CHAR", LengthSet = "8", AllowNull = false, DefaultKind = DefaultKind.Text, DefaultText = "x" });
        design.Checksum = "0";
        design.Name = "products";

        var statements = Ddl.Alter(before, design.ToDefinition(Types, Dialect));
        Assert.Equal(2, statements.Count);
        Assert.Contains("CHECKSUM=0", statements[0], StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN `sku` char(8) NOT NULL DEFAULT 'x' AFTER `id`", statements[0], StringComparison.Ordinal);
        Assert.Contains("CHANGE COLUMN `price` `price` decimal(10,2) NULL DEFAULT 1.5 AFTER `name`", statements[0], StringComparison.Ordinal);
        Assert.Equal("RENAME TABLE `shop`.`items` TO `shop`.`products`", statements[1]);
    }

    [Fact]
    public void Validacao()
    {
        var design = TableDesign.New("shop");
        Assert.Contains("Please enter a table name.", design.Validate());
        design.Name = "t";
        design.Columns.Add(new ColumnDesign { Name = "id" });
        design.Indexes.Add(new IndexDesign { Name = "PRIMARY", Kind = IndexKind.Primary, Columns = " " });
        Assert.Equal(["Index \"PRIMARY\" has no columns."], design.Validate());
        Assert.True(TableDesign.IsInteger("bigint"));
        Assert.False(TableDesign.IsNumeric("BIT"));
    }
}
