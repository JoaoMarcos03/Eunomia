using Eunomia.App.Objects;
using Eunomia.Drivers.Abstractions;
using Eunomia.Drivers.MySql;

namespace Eunomia.App.Tests;

/// <summary>Saving in the table editor without losing data (phase 14): warnings before saving and strict mode on the ALTER.</summary>
public sealed class TableEditingSafetyTests
{
    private static readonly MySqlTypeMapper Types = new();

    private static ColumnDefinition Column(string name, string native, string? collation = null) =>
        new(name, native, Types.Map(native) with { Collation = collation }, true, null, false, false, false, null) { OriginalName = name };

    private static TableDefinition Table(params ColumnDefinition[] columns) =>
        new(new DbObjectRef("db", "db", "t", DbObjectKind.Table), columns, [], [], [], new Dictionary<string, string>());

    [Theory]
    [InlineData("varchar(20)", "varchar(60)", true)]
    [InlineData("varchar(60)", "varchar(20)", false)]
    [InlineData("int(11)", "bigint(20)", true)]
    [InlineData("bigint(20)", "int(11)", false)]
    [InlineData("int(11)", "int(10) unsigned", false)]
    [InlineData("int(10) unsigned", "bigint(20) unsigned", true)]
    [InlineData("decimal(10,2)", "decimal(12,2)", true)]
    [InlineData("decimal(10,2)", "decimal(10,4)", false)]
    [InlineData("decimal(10,4)", "decimal(10,2)", false)]
    [InlineData("float", "double", true)]
    [InlineData("double", "float", false)]
    [InlineData("varchar(100)", "text", true)]
    [InlineData("varchar(20000)", "text", false)]
    [InlineData("text", "varchar(100)", false)]
    [InlineData("tinytext", "longtext", true)]
    [InlineData("char(3)", "varchar(3)", true)]
    [InlineData("datetime", "datetime(3)", true)]
    [InlineData("datetime(6)", "datetime", false)]
    [InlineData("enum('a','b')", "enum('a')", false)]
    [InlineData("varchar(10)", "int(11)", false)]
    public void WideningDoesNotAskForConfirmation(string before, string after, bool widening)
    {
        Assert.Equal(widening, TableEditing.IsWidening(before, after));
    }

    [Fact]
    public void WarnsAboutDroppedColumnsNarrowedTypesAndCharsets()
    {
        var before = Table(Column("id", "int(11)"), Column("name", "varchar(60)", "utf8mb4_general_ci"), Column("notes", "text"), Column("price", "double"));
        var after = Table(
            Column("id", "bigint(20)"),
            Column("name", "varchar(60)", "latin1_swedish_ci"),
            Column("price", "float") with { Name = "cost" });

        Assert.Equal(
        [
            "Convert column \"name\" from utf8mb4 to latin1",
            "Drop column \"notes\" and all its values",
            "Change column \"price\" from double to float",
        ], TableEditing.DataLossWarnings(before, after));
        Assert.Empty(TableEditing.DataLossWarnings(null, after));
        Assert.Empty(TableEditing.DataLossWarnings(before, before));
    }

    [Fact]
    public void StrictModeForTheDdl()
    {
        var dialect = new MySqlDialect(MySqlConnectionFactory.ParseIdentity("10.4.32-MariaDB", null));
        var (enter, restore) = dialect.DdlSession("NO_ZERO_IN_DATE,NO_ZERO_DATE,NO_ENGINE_SUBSTITUTION", 10)!.Value;
        Assert.Equal("SET @EUNOMIA_LWT = @@SESSION.lock_wait_timeout, SESSION lock_wait_timeout = 10, SESSION sql_mode = 'NO_ENGINE_SUBSTITUTION,STRICT_TRANS_TABLES,STRICT_ALL_TABLES'", enter);
        Assert.Equal("SET SESSION lock_wait_timeout = @EUNOMIA_LWT, SESSION sql_mode = 'NO_ZERO_IN_DATE,NO_ZERO_DATE,NO_ENGINE_SUBSTITUTION'", restore);
        Assert.EndsWith("SESSION sql_mode = 'STRICT_TRANS_TABLES,STRICT_ALL_TABLES'", dialect.DdlSession(string.Empty, 10)!.Value.Enter, StringComparison.Ordinal);
        // Without strict mode (views, Drop, Empty): only lock_wait_timeout.
        Assert.Equal(("SET @EUNOMIA_LWT = @@SESSION.lock_wait_timeout, SESSION lock_wait_timeout = 5", "SET SESSION lock_wait_timeout = @EUNOMIA_LWT"), dialect.DdlSession(null, 5)!.Value);
        Assert.Equal("SELECT @@SESSION.sql_mode", dialect.SessionModeQuery);
    }
}
