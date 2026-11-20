namespace Eunomia.Engine.Execution.Tests;

/// <summary>Read-only policy and transaction tracker: whitespace, SET STATEMENT, read-only variables and <c>/*M!</c>.</summary>
public sealed class SafeguardsFix2Tests
{
    [Theory]
    [InlineData("SELECT 1 INTO\nOUTFILE '/tmp/x'")]
    [InlineData("SELECT 1 INTO  DUMPFILE '/tmp/x'")]
    [InlineData("WITH c AS (SELECT 1) SELECT * FROM c INTO\tOUTFILE '/tmp/x'")]
    [InlineData("SET SESSION TRANSACTION READ\tWRITE")]
    [InlineData("SET STATEMENT max_statement_time=1 FOR DELETE FROM t")]
    [InlineData("SET tx_read_only = 0")]
    [InlineData("SET @@session.transaction_read_only = 0")]
    [InlineData("/*M! DROP TABLE t */")]
    [InlineData("SELECT 1; /*M!100100 DELETE FROM t */")]
    public void WritesAreRefused(string sql)
    {
        Assert.False(ReadOnlyPolicy.IsAllowed(sql));
    }

    [Theory]
    [InlineData("SELECT 1 /*M!100100 , 2 */")]
    [InlineData("SET\nNAMES utf8mb4")]
    public void ReadsStillGoThrough(string sql)
    {
        Assert.True(ReadOnlyPolicy.IsAllowed(sql));
    }

    [Fact]
    public void AutocommitOnWhenAlreadyOnKeepsTheTransaction()
    {
        var t = new TransactionTracker();
        t.Observe(["BEGIN", "UPDATE t SET a = 1"]);
        t.Observe(["SET autocommit = 1"]);
        t.Observe(["SET autocommit = 1"]);
        Assert.True(t.IsOpen);
        Assert.True(t.HasChanges);
    }

    [Fact]
    public void MariaDbExecutableCommentIsClassified()
    {
        Assert.Equal(TransactionEffect.AutocommitOff, TransactionTracker.Classify("/*M!100100 SET autocommit=0 */"));
    }
}
