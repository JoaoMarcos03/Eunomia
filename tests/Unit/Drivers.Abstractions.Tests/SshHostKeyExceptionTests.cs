namespace Eunomia.Drivers.Abstractions.Tests;

/// <summary>The host key messages the user reads before trusting a server (also without an SSH test server).</summary>
public sealed class SshHostKeyExceptionTests
{
    [Theory]
    [InlineData(SshHostKeyProblem.Unknown, "can't be established")]
    [InlineData(SshHostKeyProblem.NewKeyType, "key of another type is already trusted")]
    [InlineData(SshHostKeyProblem.Changed, "has changed")]
    public void MessageAndDetailsPerProblem(SshHostKeyProblem problem, string expected)
    {
        byte[] key = [1, 2, 3];
        var ex = new SshHostKeyException(problem, "db.example", 22, "ssh-ed25519", "abc", key);

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.Contains("SHA256 fingerprint: abc", ex.Message, StringComparison.Ordinal);
        Assert.Equal((problem, "db.example", 22, "ssh-ed25519", "abc"), (ex.Problem, ex.Host, ex.Port, ex.KeyType, ex.FingerprintSha256));
        Assert.Same(key, ex.Key);
    }
}
