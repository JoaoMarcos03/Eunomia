namespace Eunomia.Engine.Execution.Tests;

/// <summary>Connections that do not leave the computer (no "not encrypted" warning without TLS; section 23.2).</summary>
public sealed class LocalHostTests
{
    [Theory]
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.1.1")]
    [InlineData("::1")]
    [InlineData("[::1]")]
    [InlineData(".")]
    [InlineData("/var/run/mysqld/mysqld.sock")]
    [InlineData(" 127.0.0.1 ")]
    public void Local(string host)
    {
        Assert.True(SessionHost.IsLocalHost(host));
    }

    [Theory]
    [InlineData("db.example.com")]
    [InlineData("10.0.0.5")]
    [InlineData("192.168.1.20")]
    [InlineData("localhost.example.com")]
    [InlineData("2001:db8::1")]
    public void Remote(string host)
    {
        Assert.False(SessionHost.IsLocalHost(host));
    }
}
