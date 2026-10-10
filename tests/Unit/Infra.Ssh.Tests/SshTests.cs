using System.Buffers.Binary;
using System.Text;
using Eunomia.Infra.Ssh;

namespace Eunomia.Infra.Ssh.Tests;

public sealed class KnownHostsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "eunomia-kh-" + Guid.NewGuid().ToString("N"), "known_hosts");

    public void Dispose()
    {
        var dir = Path.GetDirectoryName(_path)!;
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void PrimeiroContactoConfiancaEMudancaDeChave()
    {
        var store = new KnownHostsStore(_path);
        byte[] key = [1, 2, 3, 4];
        byte[] other = [9, 9, 9];

        Assert.Equal(HostKeyCheck.Unknown, store.Check("Bastion.example.com", 22, "ssh-ed25519", key));

        store.Trust("Bastion.example.com", 22, "ssh-ed25519", key);
        Assert.Equal(HostKeyCheck.Match, store.Check("bastion.example.com", 22, "ssh-ed25519", key));
        Assert.Equal(HostKeyCheck.Mismatch, store.Check("bastion.example.com", 22, "ssh-ed25519", other));
        // Another key type on a known host is not a first contact: asks for confirmation with a warning.
        Assert.Equal(HostKeyCheck.OtherTypeKnown, store.Check("bastion.example.com", 22, "ssh-rsa", key));
        Assert.Equal(HostKeyCheck.Unknown, store.Check("bastion.example.com", 2222, "ssh-ed25519", key));

        store.Trust("bastion.example.com", 22, "ssh-ed25519", other);
        Assert.Equal(HostKeyCheck.Match, store.Check("bastion.example.com", 22, "ssh-ed25519", other));
        Assert.Single(File.ReadAllLines(_path));
    }

    [Fact]
    public void FormatoDoOpenSsh()
    {
        var store = new KnownHostsStore(_path);
        store.Trust("db.example.com", 2222, "ssh-rsa", [7]);

        Assert.Equal("[db.example.com]:2222 ssh-rsa Bw==", File.ReadAllText(_path).Trim());
        Assert.Equal("SHA256:" + Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData([7])).TrimEnd('='), KnownHostsStore.Fingerprint([7]));
    }

    [Fact]
    public void PortaLocalLivre()
    {
        var port = SshTunnelFactory.FreeLocalPort();

        Assert.InRange(port, 1024, 65535);
    }
}

public sealed class SshAgentTests
{
    /// <summary>Fake agent: answers with two keys and records the signing requests.</summary>
    private sealed class FakeAgent : ISshAgentTransport
    {
        public List<byte[]> Requests { get; } = [];

        public byte[] Request(byte[] message)
        {
            Requests.Add(message);
            using var ms = new MemoryStream();
            if (message[0] == 11)
            {
                ms.WriteByte(12);
                WriteUInt(ms, 2);
                WriteString(ms, Blob("ssh-ed25519"));
                WriteString(ms, "work"u8.ToArray());
                WriteString(ms, Blob("ssh-rsa"));
                WriteString(ms, "legacy"u8.ToArray());
            }
            else if (message[0] == 13)
            {
                ms.WriteByte(14);
                WriteString(ms, "signature-blob"u8.ToArray());
            }
            return ms.ToArray();
        }

        public static byte[] Blob(string type)
        {
            using var ms = new MemoryStream();
            WriteString(ms, Encoding.ASCII.GetBytes(type));
            WriteString(ms, [0xAA, 0xBB]);
            return ms.ToArray();
        }

        private static void WriteUInt(Stream s, uint v)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b, v);
            s.Write(b);
        }

        private static void WriteString(Stream s, byte[] v)
        {
            WriteUInt(s, (uint)v.Length);
            s.Write(v);
        }
    }

    [Fact]
    public void ListaChavesDoAgent()
    {
        var agent = new SshAgentClient(new FakeAgent());

        var keys = agent.ListKeys();

        Assert.Equal(["ssh-ed25519", "ssh-rsa"], keys.Select(k => k.KeyType));
        Assert.Equal(["work", "legacy"], keys.Select(k => k.Comment));
    }

    [Fact]
    public void RsaComSha2PrimeiroEAssinaturaPeloAgent()
    {
        var transport = new FakeAgent();
        var agent = new SshAgentClient(transport);

        var algorithms = agent.HostAlgorithms();
        Assert.Equal(["ssh-ed25519", "rsa-sha2-512", "rsa-sha2-256", "ssh-rsa"], algorithms.Select(a => a.Name));

        var signature = algorithms[2].Sign([1, 2, 3]);

        Assert.Equal("signature-blob"u8.ToArray(), signature);
        var request = transport.Requests[^1];
        Assert.Equal(13, request[0]);
        Assert.Equal(SshAgentClient.RsaSha256, BinaryPrimitives.ReadUInt32BigEndian(request.AsSpan(request.Length - 4)));
        Assert.Equal(FakeAgent.Blob("ssh-rsa"), algorithms[2].Data);
    }
}
