namespace Eunomia.Engine.Sql.Tests;

/// <summary>Passwords masked in the SQL log (section 23.2).</summary>
public sealed class SecretMaskingTests
{
    [Theory]
    [InlineData("CREATE USER 'u'@'%' IDENTIFIED BY 'S3gr3do'", "CREATE USER 'u'@'%' IDENTIFIED BY '***'")]
    [InlineData("ALTER USER u IDENTIFIED WITH mysql_native_password BY \"a'b\"", "ALTER USER u IDENTIFIED WITH mysql_native_password BY '***'")]
    [InlineData("CREATE USER u IDENTIFIED WITH caching_sha2_password AS '$A$005$hash'", "CREATE USER u IDENTIFIED WITH caching_sha2_password AS '***'")]
    [InlineData("GRANT ALL ON *.* TO u IDENTIFIED BY PASSWORD '*94BDCEBE'", "GRANT ALL ON *.* TO u IDENTIFIED BY PASSWORD '***'")]
    [InlineData("SET PASSWORD FOR 'u'@'localhost' = PASSWORD('x\\'y')", "SET PASSWORD FOR 'u'@'localhost' = PASSWORD('***')")]
    [InlineData("set password = 'new'", "set password = '***'")]
    [InlineData("CREATE USER u IDENTIFIED VIA ed25519 USING PASSWORD('segredo')", "CREATE USER u IDENTIFIED VIA ed25519 USING PASSWORD('***')")]
    [InlineData("SELECT OLD_PASSWORD('a'), PASSWORD(\"b\")", "SELECT OLD_PASSWORD('***'), PASSWORD('***')")]
    [InlineData("CHANGE MASTER TO MASTER_HOST='h', MASTER_PASSWORD='p'", "CHANGE MASTER TO MASTER_HOST='h', MASTER_PASSWORD='***'")]
    [InlineData("CREATE SERVER s FOREIGN DATA WRAPPER mysql OPTIONS (USER 'u', PASSWORD 'p')", "CREATE SERVER s FOREIGN DATA WRAPPER mysql OPTIONS (USER 'u', PASSWORD '***')")]
    public void MasksTheLiterals(string sql, string expected)
    {
        Assert.Equal(expected, SecretMasking.MaskSql(sql));
        Assert.True(SecretMasking.ContainsSecret(sql));
    }

    [Theory]
    [InlineData("SELECT * FROM users WHERE password_hash = 'x'")]
    [InlineData("SELECT 'IDENTIFIED BY' AS t")]
    [InlineData("UPDATE t SET name = 'password'")]
    public void LeavesTheRest(string sql)
    {
        Assert.Equal(sql, SecretMasking.MaskSql(sql));
        Assert.False(SecretMasking.ContainsSecret(sql));
    }
}
