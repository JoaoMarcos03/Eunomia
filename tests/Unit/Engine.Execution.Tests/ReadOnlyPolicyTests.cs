namespace Eunomia.Engine.Execution.Tests;

/// <summary>What a read-only session lets through (section 23.2; phase 14): an allow list.</summary>
public sealed class ReadOnlyPolicyTests
{
    [Theory]
    [InlineData("SELECT * FROM t")]
    [InlineData("  -- c\n select 1")]
    [InlineData("(SELECT 1) UNION (SELECT 2)")]
    [InlineData("SELECT * FROM t FOR UPDATE")]
    [InlineData("SELECT 'DELETE FROM t; DROP TABLE x' AS s")]
    [InlineData("SELECT `update` FROM t")]
    [InlineData("WITH c AS (SELECT 1) SELECT * FROM c")]
    [InlineData("SHOW CREATE TABLE t")]
    [InlineData("DESCRIBE t")]
    [InlineData("EXPLAIN UPDATE t SET a = 1")]
    [InlineData("USE shop")]
    [InlineData("HELP 'select'")]
    [InlineData("SET NAMES utf8mb4")]
    [InlineData("SET @x = 1, SESSION sql_mode = ''")]
    [InlineData("SET SESSION TRANSACTION READ ONLY")]
    [InlineData("BEGIN")]
    [InlineData("START TRANSACTION READ ONLY")]
    [InlineData("COMMIT")]
    [InlineData("ROLLBACK TO SAVEPOINT s")]
    [InlineData("TABLE t")]
    [InlineData("SELECT 1; SELECT 2;")]
    [InlineData("")]
    public void ReadsGoThrough(string sql)
    {
        Assert.True(ReadOnlyPolicy.IsAllowed(sql), sql);
    }

    [Theory]
    [InlineData("INSERT INTO t VALUES (1)")]
    [InlineData("update t set a = 1")]
    [InlineData("DELETE FROM t")]
    [InlineData("REPLACE t VALUES (1)")]
    [InlineData("TRUNCATE t")]
    [InlineData("DROP TABLE t")]
    [InlineData("CREATE TABLE t (a INT)")]
    [InlineData("ALTER TABLE t ADD b INT")]
    [InlineData("RENAME TABLE a TO b")]
    [InlineData("CALL p()")]
    [InlineData("DO f()")]
    [InlineData("PREPARE s FROM 'DELETE FROM t'")]
    [InlineData("EXECUTE s")]
    [InlineData("LOCK TABLES t WRITE")]
    [InlineData("KILL 42")]
    [InlineData("GRANT ALL ON *.* TO x")]
    [InlineData("LOAD DATA LOCAL INFILE 'x' INTO TABLE t")]
    [InlineData("SET GLOBAL max_connections = 10")]
    [InlineData("SET @@global.read_only = 0")]
    [InlineData("SET PERSIST x = 1")]
    [InlineData("SET PASSWORD = 'x'")]
    [InlineData("SET SESSION TRANSACTION READ WRITE")]
    [InlineData("START TRANSACTION READ WRITE")]
    [InlineData("SELECT * FROM t INTO OUTFILE '/tmp/x'")]
    [InlineData("WITH c AS (SELECT 1) DELETE t FROM t JOIN c")]
    [InlineData("SELECT 1; DELETE FROM t")]
    [InlineData("SELECT 1 /* x */; UPDATE t SET a = 1")]
    [InlineData("/*!40101 DELETE FROM t */")]
    [InlineData("BEGIN NOT ATOMIC UPDATE t SET a = 1; END")]
    [InlineData("EXPLAIN ANALYZE DELETE FROM t")]
    [InlineData("ANALYZE TABLE t")]
    [InlineData("FLUSH PRIVILEGES")]
    [InlineData("XA START 'x'")]
    [InlineData("HANDLER t OPEN")]
    public void WritesAreRefused(string sql)
    {
        Assert.False(ReadOnlyPolicy.IsAllowed(sql), sql);
    }
}
