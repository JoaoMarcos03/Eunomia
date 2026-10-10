namespace Eunomia.Engine.Execution.Tests;

/// <summary>Transaction state of the primary connection tracked from its statements (section 14.5; phase 14).</summary>
public sealed class TransactionTrackerTests
{
    [Theory]
    [InlineData("BEGIN", TransactionEffect.Begin)]
    [InlineData("begin work", TransactionEffect.Begin)]
    [InlineData("BEGIN NOT ATOMIC SELECT 1; END", TransactionEffect.None)]
    [InlineData("START TRANSACTION READ ONLY", TransactionEffect.Begin)]
    [InlineData("/* x */ start transaction", TransactionEffect.Begin)]
    [InlineData("START SLAVE", TransactionEffect.ImplicitCommit)]
    [InlineData("XA START 'x'", TransactionEffect.Begin)]
    [InlineData("XA COMMIT 'x'", TransactionEffect.Commit)]
    [InlineData("COMMIT", TransactionEffect.Commit)]
    [InlineData("COMMIT WORK AND CHAIN", TransactionEffect.EndAndChain)]
    [InlineData("COMMIT AND NO CHAIN", TransactionEffect.Commit)]
    [InlineData("ROLLBACK", TransactionEffect.Rollback)]
    [InlineData("ROLLBACK TO SAVEPOINT s1", TransactionEffect.None)]
    [InlineData("ROLLBACK WORK TO s1", TransactionEffect.None)]
    [InlineData("SET autocommit = 0", TransactionEffect.AutocommitOff)]
    [InlineData("SET @@session.autocommit=OFF", TransactionEffect.AutocommitOff)]
    [InlineData("set names utf8mb4, autocommit := 1", TransactionEffect.AutocommitOn)]
    [InlineData("/*!40101 SET autocommit=0 */", TransactionEffect.AutocommitOff)]
    [InlineData("SET sql_mode = ''", TransactionEffect.None)]
    [InlineData("INSERT INTO t VALUES (1)", TransactionEffect.Change)]
    [InlineData("-- c\nupdate t set a = 1", TransactionEffect.Change)]
    [InlineData("REPLACE t VALUES (1)", TransactionEffect.Change)]
    [InlineData("LOAD DATA LOCAL INFILE 'x' INTO TABLE t", TransactionEffect.Change)]
    [InlineData("CALL p()", TransactionEffect.Change)]
    [InlineData("WITH c AS (SELECT 1) UPDATE t JOIN c SET t.a = 1", TransactionEffect.Change)]
    [InlineData("WITH c AS (SELECT 1) SELECT * FROM c", TransactionEffect.None)]
    [InlineData("(SELECT 1) UNION (SELECT 2)", TransactionEffect.None)]
    [InlineData("SELECT * FROM t FOR UPDATE", TransactionEffect.None)]
    [InlineData("CREATE TABLE t (a INT)", TransactionEffect.ImplicitCommit)]
    [InlineData("CREATE TEMPORARY TABLE t (a INT)", TransactionEffect.None)]
    [InlineData("DROP TEMPORARY TABLE t", TransactionEffect.None)]
    [InlineData("ALTER TABLE t ADD b INT", TransactionEffect.ImplicitCommit)]
    [InlineData("TRUNCATE t", TransactionEffect.ImplicitCommit)]
    [InlineData("LOCK TABLES t WRITE", TransactionEffect.ImplicitCommit)]
    [InlineData("", TransactionEffect.None)]
    public void ClassificaPelasPrimeirasPalavras(string sql, TransactionEffect expected)
    {
        Assert.Equal(expected, TransactionTracker.Classify(sql));
    }

    [Fact]
    public void BeginAlteracoesECommit()
    {
        var t = new TransactionTracker();
        var changes = 0;
        t.Changed += (_, _) => changes++;
        t.Observe(["SELECT 1"]);
        Assert.False(t.IsOpen);
        t.Observe(["INSERT INTO t VALUES (1)"]);
        Assert.False(t.IsOpen);
        t.Observe(["BEGIN"]);
        Assert.True(t.IsOpen);
        Assert.False(t.HasChanges);
        t.Observe(["UPDATE t SET a = 2"]);
        Assert.True(t.HasChanges);
        t.Observe(["COMMIT"]);
        Assert.False(t.IsOpen);
        Assert.False(t.HasChanges);
        Assert.Equal(3, changes);
    }

    [Fact]
    public void AutocommitDesligadoAbreTransacaoImplicita()
    {
        var t = new TransactionTracker();
        t.Observe(["SET autocommit = 0"]);
        Assert.True(t.AutocommitOff);
        Assert.False(t.IsOpen);
        t.Observe(["SELECT 1"]);
        Assert.True(t.IsOpen);
        Assert.False(t.HasChanges);
        t.Observe(["DELETE FROM t"]);
        Assert.True(t.HasChanges);
        // DDL commits implicitly; the next statement opens another one (autocommit stays off).
        t.Observe(["CREATE INDEX i ON t (a)"]);
        Assert.False(t.IsOpen);
        t.Observe(["INSERT INTO t VALUES (2)"]);
        Assert.True(t.HasChanges);
        t.Observe(["SET autocommit = 1"]);
        Assert.False(t.IsOpen);
        Assert.False(t.AutocommitOff);
    }

    [Fact]
    public void LigacaoPerdidaDizSeHaviaAlteracoes()
    {
        var t = new TransactionTracker();
        t.Observe(["START TRANSACTION", "INSERT INTO t VALUES (1)"]);
        Assert.True(t.Reset());
        Assert.False(t.IsOpen);
        Assert.False(t.Reset());
        t.Observe(["BEGIN", "SELECT 1"]);
        Assert.False(t.Reset());
        t.Observe(["BEGIN", "UPDATE t SET a = 1"]);
        Assert.True(t.RolledBackByServer());
        Assert.False(t.HasChanges);
    }
}
