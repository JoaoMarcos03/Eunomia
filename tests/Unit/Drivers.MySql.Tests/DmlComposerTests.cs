using Eunomia.Drivers.Abstractions;

namespace Eunomia.Drivers.MySql.Tests;

/// <summary>Grid editing DML (appendix D B15).</summary>
public sealed class DmlComposerTests
{
    private static readonly MySqlDmlComposer Dml = new(new MySqlDialect(new ServerIdentity(DbEngine.MariaDb, new Version(10, 4, 32), "10.4.32-MariaDB", null)));
    private static readonly DbObjectRef People = new("shop", "shop", "people", DbObjectKind.Table);

    private static ColumnDefinition Column(string name, string type, LogicalTypeKind kind) =>
        new(name, type, new LogicalType(kind), true, null, false, false, false, null);

    [Theory]
    [InlineData("INT", LogicalTypeKind.Integer, "42", "42")]
    [InlineData("INT", LogicalTypeKind.Integer, "-1.5e3", "-1.5e3")]
    [InlineData("INT", LogicalTypeKind.Integer, "abc", "'abc'")]
    [InlineData("DECIMAL(10,2)", LogicalTypeKind.Decimal, " 12.50 ", "12.50")]
    [InlineData("BIT(4)", LogicalTypeKind.Bit, "0101", "b'0101'")]
    [InlineData("VARBINARY(4)", LogicalTypeKind.Binary, "0xCAFE", "0xCAFE")]
    [InlineData("VARBINARY(4)", LogicalTypeKind.Binary, "0x", "''")]
    [InlineData("VARBINARY(4)", LogicalTypeKind.Binary, "texto", "'texto'")]
    [InlineData("DATETIME", LogicalTypeKind.DateTime, "0000-00-00 00:00:00", "'0000-00-00 00:00:00'")]
    [InlineData("VARCHAR(10)", LogicalTypeKind.Text, "it's", "'it''s'")]
    [InlineData("VARCHAR(10)", LogicalTypeKind.Text, "C:\\dir", "_utf8mb4 X'433A5C646972'")]
    [InlineData("TEXT", LogicalTypeKind.Text, "a\nb", "'a\nb'")]
    public void LiteraisPorTipo(string type, LogicalTypeKind kind, string text, string expected)
    {
        Assert.Equal(expected, Dml.Literal(Column("c", type, kind), new CellLiteral(text)));
    }

    [Fact]
    public void NullFuncaoEInstrucoes()
    {
        var id = Column("id", "INT", LogicalTypeKind.Integer);
        var name = Column("name", "VARCHAR(20)", LogicalTypeKind.Text);
        var created = Column("created", "DATETIME", LogicalTypeKind.DateTime);
        Assert.Equal("NULL", Dml.Literal(name, CellLiteral.Null));
        Assert.Equal("NOW()", Dml.Literal(created, new CellLiteral("NOW()", IsFunction: true)));

        Assert.Equal("INSERT INTO `shop`.`people` (`name`, `created`) VALUES ('Ana', NOW())",
            Dml.Insert(People, [new ColumnValue(name, new CellLiteral("Ana")), new ColumnValue(created, new CellLiteral("NOW()", true))]));
        Assert.Equal("INSERT INTO `shop`.`people` () VALUES ()", Dml.Insert(People, []));
        // With a key: the WHERE uses the column's collation (index) and carries LIMIT 1 anyway.
        Assert.Equal("UPDATE `shop`.`people` SET `name`='Rui' WHERE `id`=1 LIMIT 1",
            Dml.Update(People, [new ColumnValue(name, new CellLiteral("Rui"))], [new ColumnValue(id, new CellLiteral("1"))], withoutKey: false));
        Assert.Equal("DELETE FROM `shop`.`people` WHERE `name` IS NULL AND CAST(`created` AS CHAR)='2026-01-01 00:00:00' LIMIT 1",
            Dml.Delete(People, [new ColumnValue(name, CellLiteral.Null), new ColumnValue(created, new CellLiteral("2026-01-01 00:00:00"))], withoutKey: true));
        Assert.Equal("SELECT COUNT(*) FROM `shop`.`people` WHERE `id`=1", Dml.Count(People, [new ColumnValue(id, new CellLiteral("1"))]));
        Assert.Throws<ArgumentException>(() => Dml.Delete(People, [], withoutKey: true));
    }

    [Fact]
    public void SemChaveOTextoComparaSeDeFormaExata()
    {
        var code = new ColumnDefinition("code", "varchar(10)", new LogicalType(LogicalTypeKind.Text, Charset: "latin1", Collation: "latin1_swedish_ci"), true, null, false, false, false, null);
        var raw = new ColumnDefinition("raw", "varbinary(10)", new LogicalType(LogicalTypeKind.Binary, Charset: "binary"), true, null, false, false, false, null);
        var n = Column("n", "INT", LogicalTypeKind.Integer);
        Assert.Equal(
            "UPDATE `shop`.`people` SET `n`=2 WHERE `code`=CONVERT('Ab' USING latin1) COLLATE latin1_bin AND `raw`=0xCAFE AND `n`=1 LIMIT 1",
            Dml.Update(People, [new ColumnValue(n, new CellLiteral("2"))],
                [new ColumnValue(code, new CellLiteral("Ab")), new ColumnValue(raw, new CellLiteral("0xCAFE")), new ColumnValue(n, new CellLiteral("1"))], withoutKey: true));
        // With a key, the same column keeps its own collation.
        Assert.Equal("DELETE FROM `shop`.`people` WHERE `code`='Ab' LIMIT 1", Dml.Delete(People, [new ColumnValue(code, new CellLiteral("Ab"))], withoutKey: false));
    }
}
