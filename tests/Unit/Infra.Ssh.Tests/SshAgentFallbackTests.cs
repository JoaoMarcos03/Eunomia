using Eunomia.Drivers.Abstractions;
using Eunomia.Infra.Ssh;

namespace Eunomia.Infra.Ssh.Tests;

/// <summary>A broken SSH agent must not stop the tunnel: it moves on to the key, the password or "none".</summary>
public sealed class SshAgentFallbackTests
{
    private sealed class BrokenAgent : ISshAgentTransport
    {
        public bool Called { get; private set; }

        public byte[] Request(byte[] message)
        {
            Called = true;
            throw new TimeoutException("agent pipe did not answer");
        }
    }

    [Fact]
    public async Task AgentErrorOtherThanIoFallsBackToConnectingWithoutTheAgent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "eunomia-kh-" + Guid.NewGuid().ToString("N"));
        try
        {
            var transport = new BrokenAgent();
            var factory = new SshTunnelFactory(new KnownHostsStore(Path.Combine(dir, "known_hosts")), () => new SshAgentClient(transport));
            // Nothing listens on this port: without the fallback the agent's TimeoutException escapes before connecting.
            var options = new SshTunnelOptions("127.0.0.1", SshTunnelFactory.FreeLocalPort(), "user", null, null) { Timeout = TimeSpan.FromSeconds(3) };

            var error = await Assert.ThrowsAnyAsync<Exception>(() => factory.OpenAsync(
                options, "127.0.0.1", 9, (_, _) => ValueTask.FromResult(string.Empty), TestContext.Current.CancellationToken));

            Assert.True(transport.Called);
            Assert.NotEqual("agent pipe did not answer", error.Message);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
