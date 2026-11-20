namespace Eunomia.Engine.Sql.Tests;

/// <summary>Secrets of replication and the old password (<c>REPLACE</c>) masked in the log; <c>/*M!</c> comments read as code.</summary>
public sealed class SecretMaskingFix2Tests
{
    [Theory]
    [InlineData("CHANGE REPLICATION SOURCE TO SOURCE_HOST='h', SOURCE_PASSWORD='p'", "CHANGE REPLICATION SOURCE TO SOURCE_HOST='h', SOURCE_PASSWORD='***'")]
    [InlineData("START REPLICA USER='u' PASSWORD='p'", "START REPLICA USER='u' PASSWORD='***'")]
    [InlineData("ALTER USER u IDENTIFIED BY 'new' REPLACE 'old'", "ALTER USER u IDENTIFIED BY '***' REPLACE '***'")]
    [InlineData("SET PASSWORD = 'new' REPLACE 'old'", "SET PASSWORD = '***' REPLACE '***'")]
    public void MasksTheLiterals(string sql, string expected)
    {
        Assert.Equal(expected, SecretMasking.MaskSql(sql));
        Assert.True(SecretMasking.ContainsSecret(sql));
    }

    [Fact]
    public void MariaDbExecutableCommentCountsForUse()
    {
        Assert.Equal("legacy", SessionStatements.UseTarget("/*M!100100 USE legacy */"));
    }
}
