namespace Eunomia.Engine.Sql.Tests;

/// <summary>USE and session SETs of a script, replayed when Run SQL file resumes on a new connection.</summary>
public sealed class SessionStatementsTests
{
    [Theory]
    [InlineData("USE shop", SessionStatementKind.Use)]
    [InlineData("use `my db`", SessionStatementKind.Use)]
    [InlineData("-- select the database\nUSE shop", SessionStatementKind.Use)]
    [InlineData("SET NAMES utf8mb4", SessionStatementKind.Set)]
    [InlineData("/*!40101 SET NAMES utf8mb4 */", SessionStatementKind.Set)]
    [InlineData("/*!40014 SET @OLD_FOREIGN_KEY_CHECKS=@@FOREIGN_KEY_CHECKS, FOREIGN_KEY_CHECKS=0 */", SessionStatementKind.Set)]
    [InlineData("/*!40103 SET TIME_ZONE='+00:00' */", SessionStatementKind.Set)]
    [InlineData("SET SESSION sql_mode = 'NO_AUTO_VALUE_ON_ZERO'", SessionStatementKind.Set)]
    [InlineData("SET @@session.unique_checks = 0", SessionStatementKind.Set)]
    [InlineData("SET @x := 'global'", SessionStatementKind.Set)]
    [InlineData("set autocommit=0", SessionStatementKind.Set)]
    [InlineData("SET GLOBAL max_connections = 500", SessionStatementKind.None)]
    [InlineData("SET @@GLOBAL.read_only = 1", SessionStatementKind.None)]
    [InlineData("SET sql_mode = '', GLOBAL event_scheduler = ON", SessionStatementKind.None)]
    [InlineData("SET PERSIST innodb_buffer_pool_size = 1073741824", SessionStatementKind.None)]
    [InlineData("SET PASSWORD FOR 'u'@'%' = 'secret'", SessionStatementKind.None)]
    [InlineData("SET DEFAULT ROLE ALL TO 'u'@'%'", SessionStatementKind.None)]
    [InlineData("SET STATEMENT max_statement_time=1 FOR SELECT 1", SessionStatementKind.None)]
    [InlineData("INSERT INTO t VALUES ('USE x; SET y=1')", SessionStatementKind.None)]
    [InlineData("/* SET NAMES latin1 */ SELECT 1", SessionStatementKind.None)]
    [InlineData("UPDATE t SET a = 1", SessionStatementKind.None)]
    [InlineData("CREATE DATABASE shop", SessionStatementKind.None)]
    [InlineData("", SessionStatementKind.None)]
    public void Classifies(string statement, SessionStatementKind expected) =>
        Assert.Equal(expected, SessionStatements.Classify(statement));

    [Theory]
    [InlineData("USE shop", "shop")]
    [InlineData("USE `my db`", "my db")]
    [InlineData("USE `we``ird`;", "we`ird")]
    [InlineData("/*!40000 USE /* old */ legacy */", "legacy")]
    [InlineData("SET NAMES utf8mb4", null)]
    public void ReadsTheDatabaseOfAUse(string statement, string? expected) =>
        Assert.Equal(expected, SessionStatements.UseTarget(statement));

    [Theory]
    [InlineData("USE shop", true)]
    [InlineData("CREATE DATABASE IF NOT EXISTS `shop` /*!40100 DEFAULT CHARACTER SET utf8mb4 */", true)]
    [InlineData("create schema shop", true)]
    [InlineData("/*!40000 DROP DATABASE IF EXISTS `shop`*/", true)]
    [InlineData("ALTER DATABASE `shop` CHARACTER SET latin1 COLLATE latin1_swedish_ci", true)]
    [InlineData("-- the database\nCREATE DATABASE shop", true)]
    [InlineData("CREATE TABLE `database` (id INT)", false)]
    [InlineData("CREATE DEFINER=`root`@`%` PROCEDURE p() SELECT 1", false)]
    [InlineData("DROP TABLE IF EXISTS `schema`", false)]
    [InlineData("INSERT INTO t VALUES ('CREATE DATABASE x')", false)]
    [InlineData("/* CREATE DATABASE x */ SELECT 1", false)]
    [InlineData("SET NAMES utf8mb4", false)]
    public void FindsTheStatementsOnDatabases(string statement, bool expected) =>
        Assert.Equal(expected, SessionStatements.IsDatabaseStatement(statement));

    [Fact]
    public void KeepsOnlyTheLastUseAndMovesRepeatedSetsToTheEnd()
    {
        var log = new SessionStateLog();
        string[] script =
        [
            "/*!40101 SET NAMES utf8mb4 */",
            "CREATE DATABASE a",
            "USE a",
            "SET @saved_cs_client = @@character_set_client",
            "SET character_set_client = utf8mb4",
            "CREATE TABLE t1 (id INT)",
            "SET character_set_client = @saved_cs_client",
            "INSERT INTO t1 VALUES (1)",
            "USE `b`",
            "SET @saved_cs_client = @@character_set_client",
            "SET character_set_client = utf8mb4",
            "CREATE TABLE t2 (id INT)",
            "SET character_set_client = @saved_cs_client",
            "SET GLOBAL general_log = 1",
        ];
        foreach (var statement in script)
        {
            log.Record(statement);
        }

        Assert.Equal(
            [
                "/*!40101 SET NAMES utf8mb4 */",
                "USE `b`",
                "SET @saved_cs_client = @@character_set_client",
                "SET character_set_client = utf8mb4",
                "SET character_set_client = @saved_cs_client",
            ],
            log.Statements);
    }

    [Fact]
    public void StaysBounded()
    {
        var log = new SessionStateLog(["USE first"]);
        for (var i = 0; i < SessionStateLog.MaxStatements * 3; i++)
        {
            log.Record($"SET @v{i} = {i}");
        }
        Assert.False(log.Record("SET @big = '" + new string('x', SessionStateLog.MaxStatementLength) + "'"));
        Assert.False(log.Record("SELECT 1"));

        Assert.Equal(SessionStateLog.MaxStatements, log.Statements.Count);
        // The USE is never the one that goes; the newest SETs stay.
        Assert.Equal("USE first", log.Statements[0]);
        Assert.Equal($"SET @v{(SessionStateLog.MaxStatements * 3) - 1} = {(SessionStateLog.MaxStatements * 3) - 1}", log.Statements[^1]);
    }
}
