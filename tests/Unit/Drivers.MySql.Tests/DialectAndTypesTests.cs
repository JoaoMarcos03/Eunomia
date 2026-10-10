using Eunomia.Drivers.Abstractions;
using Eunomia.Drivers.MySql;
using Eunomia.Engine.Results;

namespace Eunomia.Drivers.MySql.Tests;

public sealed class DialectAndTypesTests
{
    private static readonly ServerIdentity Maria = MySqlConnectionFactory.ParseIdentity("10.4.32-MariaDB", "mariadb.org binary distribution");
    private static readonly ServerIdentity MySql8 = MySqlConnectionFactory.ParseIdentity("8.0.36", "MySQL Community Server - GPL");

    [Theory]
    [InlineData("10.4.32-MariaDB", null, DbEngine.MariaDb, "10.4.32")]
    [InlineData("5.5.5-10.6.12-MariaDB-log", null, DbEngine.MariaDb, "10.6.12")]
    [InlineData("8.0.36", "MySQL Community Server - GPL", DbEngine.MySql, "8.0.36")]
    [InlineData("5.7.44-log", null, DbEngine.MySql, "5.7.44")]
    [InlineData("11.4.2-MariaDB-ubu2404", null, DbEngine.MariaDb, "11.4.2")]
    public void IdentificaMotorEVersao(string text, string? comment, DbEngine engine, string version)
    {
        var id = MySqlConnectionFactory.ParseIdentity(text, comment);

        Assert.Equal(engine, id.Engine);
        Assert.Equal(Version.Parse(version), id.Version);
    }

    [Fact]
    public void CitaIdentificadoresELiterais()
    {
        var d = new MySqlDialect(Maria);

        Assert.Equal("`we``ird`", d.QuoteIdentifier("we`ird"));
        Assert.Equal("`db`.`t`", d.QualifiedName(new DbObjectRef("db", "db", "t", DbObjectKind.Table)));
        // Expression literals: the same with and without NO_BACKSLASH_ESCAPES (doubled quotes; backslashes, NUL and Ctrl+Z as hex).
        Assert.Equal("'it''s a\nb'", d.StringLiteral("it's a\nb"));
        Assert.Equal("_utf8mb4 X'69742773205C2061'", d.StringLiteral(@"it's \ a"));
        Assert.Equal("_utf8mb4 X'61005A1A'", d.StringLiteral("a\0Z\u001A"));
        Assert.Equal(d.StringLiteral(@"it's \ a"), new MySqlDialect(Maria, noBackslashEscapes: true).StringLiteral(@"it's \ a"));
        Assert.Equal("_utf8 X'5C'", new MySqlDialect(MySqlConnectionFactory.ParseIdentity("5.1.73", null)).StringLiteral("\\"));
        Assert.Equal("''", d.StringLiteral(string.Empty));

        // Simple literals (COMMENT, SHOW LIKE, HELP): the escaping of the connection's mode.
        Assert.Equal(@"'it''s \\ a'", d.PlainStringLiteral(@"it's \ a"));
        Assert.Equal(@"'it''s \ a'", new MySqlDialect(Maria, noBackslashEscapes: true).PlainStringLiteral(@"it's \ a"));
        Assert.Equal("'sem barras'", d.PlainStringLiteral("sem barras"));
    }

    [Fact]
    public void PaginacaoKeysetEKill()
    {
        var d = new MySqlDialect(MySql8);

        Assert.Equal("LIMIT 1000", d.Paging(0, 1000));
        Assert.Equal("LIMIT 2000, 1000", d.Paging(2000, 1000));
        Assert.Equal("`id` > 5", d.KeysetPredicate(["id"], ["5"], descending: false));
        Assert.Equal("(`a`, `b`) < (1, 'x')", d.KeysetPredicate(["a", "b"], ["1", "'x'"], descending: true));
        Assert.Equal("KILL QUERY 42", d.KillQuery(42));
        Assert.Equal("CALL mysql.rds_kill_query(42)", new MySqlDialect(MySql8, isRds: true).KillQuery(42));
        Assert.Equal("USE `shop`", d.ChangeDatabase("shop"));
        Assert.Equal("RENAME TABLE `db`.`a` TO `db`.`b`", d.Rename(new DbObjectRef("db", "db", "a", DbObjectKind.Table), "b"));
        Assert.Equal(@"SHOW TABLE STATUS FROM `db` LIKE 'my\\_t'", d.EstimatedCount(new DbObjectRef("db", "db", "my_t", DbObjectKind.Table)));
        // With NO_BACKSLASH_ESCAPES the LIKE has no escape: the name is compared.
        Assert.Equal("SHOW TABLE STATUS FROM `db` WHERE `Name` = 'my_t'",
            new MySqlDialect(MySql8, noBackslashEscapes: true).EstimatedCount(new DbObjectRef("db", "db", "my_t", DbObjectKind.Table)));
    }

    [Theory]
    [InlineData("int(11) unsigned zerofill", LogicalTypeKind.Integer, StorageKind.Int64)]
    [InlineData("bigint(20) unsigned", LogicalTypeKind.Integer, StorageKind.String)]
    [InlineData("decimal(12,2)", LogicalTypeKind.Decimal, StorageKind.Decimal)]
    [InlineData("decimal(40,10)", LogicalTypeKind.Decimal, StorageKind.String)]
    [InlineData("double", LogicalTypeKind.Float, StorageKind.Double)]
    [InlineData("varchar(255)", LogicalTypeKind.Text, StorageKind.String)]
    [InlineData("longblob", LogicalTypeKind.Binary, StorageKind.Binary)]
    [InlineData("datetime(6)", LogicalTypeKind.DateTime, StorageKind.String)]
    [InlineData("point", LogicalTypeKind.Spatial, StorageKind.Binary)]
    [InlineData("bit(8)", LogicalTypeKind.Bit, StorageKind.Int64)]
    [InlineData("json", LogicalTypeKind.Json, StorageKind.String)]
    public void MapeiaTiposNativos(string native, LogicalTypeKind kind, StorageKind storage)
    {
        var mapper = new MySqlTypeMapper();
        var type = mapper.Map(native);

        Assert.Equal(kind, type.Kind);
        Assert.Equal(storage, mapper.StorageFor(type));
    }

    [Fact]
    public void EnumESetComAspasEEspacos()
    {
        var type = new MySqlTypeMapper().Map("enum('a','b c','it''s',' x ')");

        Assert.Equal(["a", "b c", "it's", " x "], type.Values);
        Assert.Equal("enum('a','b c','it''s',' x ')", new MySqlTypeMapper().ToNative(type));
        Assert.Equal(["r", "w"], new MySqlTypeMapper().Map("set('r','w')").Values);
    }

    [Fact]
    public void FormataTimeDoMySql()
    {
        Assert.Equal("-00:00:03", MySqlQueryExecutor.FormatTime(TimeSpan.FromSeconds(-3), 0));
        Assert.Equal("00:00:01.500", MySqlQueryExecutor.FormatTime(TimeSpan.FromMilliseconds(1500), 3));
        Assert.Equal("26:02:03", MySqlQueryExecutor.FormatTime(new TimeSpan(1, 2, 2, 3), 0));
        Assert.Equal("838:59:59", MySqlQueryExecutor.FormatTime(new TimeSpan(838, 59, 59), 0));
    }

    [Theory]
    [InlineData(2006, DbErrorCategory.ConnectionLost, true)]
    [InlineData(2013, DbErrorCategory.ConnectionLost, true)]
    [InlineData(1213, DbErrorCategory.Deadlock, false)]
    [InlineData(1317, DbErrorCategory.Cancelled, false)]
    [InlineData(1062, DbErrorCategory.Constraint, false)]
    [InlineData(1142, DbErrorCategory.Permission, false)]
    public void ClassificaErros(int code, DbErrorCategory category, bool reconnect)
    {
        var c = new MySqlErrorClassifier().Classify(new DbError(code, null, "x"));

        Assert.Equal(category, c.Category);
        Assert.Equal(reconnect, c.CanReconnect);
    }

    [Fact]
    public void CapabilitiesPorVersao()
    {
        Assert.True(new MySqlCapabilities(Maria).Supports(Capability.InvisibleColumns));
        Assert.False(new MySqlCapabilities(Maria).Supports(Capability.IndexVisibility));
        Assert.True(new MySqlCapabilities(MySql8).Supports(Capability.IndexVisibility));
        Assert.Equal(["TRADITIONAL", "JSON"], new MySqlCapabilities(MySql8).ExplainFormats);
    }
}
