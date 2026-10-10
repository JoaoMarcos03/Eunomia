using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Security;

namespace Eunomia.Infra.Ssh;

/// <summary>Message transport for the SSH agent protocol (request and response, without the length prefix).</summary>
public interface ISshAgentTransport
{
    byte[] Request(byte[] message);
}

/// <summary>An agent key: public blob (with the type inside) and comment.</summary>
public sealed record AgentKey(byte[] Blob, string Comment)
{
    public string KeyType => SshAgentClient.ReadString(Blob, 0, out _);
}

/// <summary>
/// SSH agent protocol client (draft-miller-ssh-agent): key listing and signing requests. The private
/// key never leaves the agent (ADR-09, 23.2).
/// </summary>
public sealed class SshAgentClient(ISshAgentTransport transport)
{
    private const byte RequestIdentities = 11;
    private const byte IdentitiesAnswer = 12;
    private const byte SignRequest = 13;
    private const byte SignResponse = 14;
    public const uint RsaSha256 = 2;
    public const uint RsaSha512 = 4;

    public IReadOnlyList<AgentKey> ListKeys()
    {
        var reply = transport.Request([RequestIdentities]);
        if (reply.Length == 0 || reply[0] != IdentitiesAnswer)
        {
            return [];
        }
        var count = BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(1));
        var offset = 5;
        var keys = new List<AgentKey>();
        for (var i = 0; i < count; i++)
        {
            var blob = ReadBytes(reply, offset, out offset);
            var comment = Encoding.UTF8.GetString(ReadBytes(reply, offset, out offset));
            keys.Add(new AgentKey(blob, comment));
        }
        return keys;
    }

    /// <summary>Signs <paramref name="data"/>; returns the signature blob (algorithm name + signature).</summary>
    public byte[] Sign(byte[] keyBlob, byte[] data, uint flags)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(SignRequest);
        WriteBytes(ms, keyBlob);
        WriteBytes(ms, data);
        Span<byte> f = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(f, flags);
        ms.Write(f);
        var reply = transport.Request(ms.ToArray());
        if (reply.Length == 0 || reply[0] != SignResponse)
        {
            throw new InvalidOperationException("The SSH agent refused to sign.");
        }
        return ReadBytes(reply, 1, out _);
    }

    /// <summary>SSH.NET algorithms for the agent's keys (RSA offered with SHA-2 first).</summary>
    public IReadOnlyList<HostAlgorithm> HostAlgorithms()
    {
        var result = new List<HostAlgorithm>();
        foreach (var key in ListKeys())
        {
            if (key.KeyType == "ssh-rsa")
            {
                result.Add(new AgentHostAlgorithm("rsa-sha2-512", key.Blob, this, RsaSha512));
                result.Add(new AgentHostAlgorithm("rsa-sha2-256", key.Blob, this, RsaSha256));
                result.Add(new AgentHostAlgorithm("ssh-rsa", key.Blob, this, 0));
            }
            else
            {
                result.Add(new AgentHostAlgorithm(key.KeyType, key.Blob, this, 0));
            }
        }
        return result;
    }

    /// <summary>Agent available on this machine: SSH_AUTH_SOCK, the OpenSSH for Windows service, or Pageant.</summary>
    public static SshAgentClient? TryConnect()
    {
        var sock = Environment.GetEnvironmentVariable("SSH_AUTH_SOCK");
        if (!string.IsNullOrEmpty(sock))
        {
            return new SshAgentClient(new StreamAgentTransport(sock));
        }
        if (OperatingSystem.IsWindows())
        {
            const string openSsh = @"\\.\pipe\openssh-ssh-agent";
            if (PipeExists("openssh-ssh-agent"))
            {
                return new SshAgentClient(new StreamAgentTransport(openSsh));
            }
            if (PageantTransport.IsRunning())
            {
                return new SshAgentClient(new PageantTransport());
            }
        }
        return null;
    }

    private static bool PipeExists(string name)
    {
        try
        {
            return Directory.EnumerateFiles(@"\\.\pipe\").Any(p => p.EndsWith(name, StringComparison.OrdinalIgnoreCase));
        }
        catch (IOException)
        {
            return false;
        }
    }

    internal static string ReadString(byte[] buffer, int offset, out int next) => Encoding.ASCII.GetString(ReadBytes(buffer, offset, out next));

    internal static byte[] ReadBytes(byte[] buffer, int offset, out int next)
    {
        var length = (int)BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset));
        next = offset + 4 + length;
        return buffer.AsSpan(offset + 4, length).ToArray();
    }

    internal static void WriteBytes(Stream stream, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)value.Length);
        stream.Write(length);
        stream.Write(value);
    }
}

/// <summary>Agent key as seen by SSH.NET: the signature is requested from the agent.</summary>
internal sealed class AgentHostAlgorithm(string name, byte[] publicKeyBlob, SshAgentClient agent, uint flags) : HostAlgorithm(name)
{
    public override byte[] Data => publicKeyBlob;

    public override byte[] Sign(byte[] data) => agent.Sign(publicKeyBlob, data, flags);

    public override bool VerifySignature(byte[] data, byte[] signature) =>
        throw new NotSupportedException("Agent keys are only used to sign.");
}

/// <summary>The agent's keys as a source of private keys for SSH.NET public key authentication.</summary>
internal sealed class AgentKeySource(IReadOnlyList<HostAlgorithm> algorithms) : IPrivateKeySource
{
    public IReadOnlyCollection<HostAlgorithm> HostKeyAlgorithms => algorithms;
}

/// <summary>OpenSSH agent: named pipe on Windows (<c>\\.\pipe\…</c>) or Unix socket (Linux, macOS, Windows 10+).</summary>
public sealed class StreamAgentTransport(string path) : ISshAgentTransport
{
    public byte[] Request(byte[] message)
    {
        using var stream = Open();
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)message.Length);
        stream.Write(header);
        stream.Write(message);
        stream.Flush();
        stream.ReadExactly(header);
        var reply = new byte[BinaryPrimitives.ReadUInt32BigEndian(header)];
        stream.ReadExactly(reply);
        return reply;
    }

    private Stream Open()
    {
        if (path.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase))
        {
            var pipe = new NamedPipeClientStream(".", path[9..], PipeDirection.InOut);
            pipe.Connect(2000);
            return pipe;
        }
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Connect(new UnixDomainSocketEndPoint(path));
        return new NetworkStream(socket, ownsSocket: true);
    }
}

/// <summary>
/// Pageant (PuTTY) through the classic protocol: the request in a named file mapping and <c>WM_COPYDATA</c> to the
/// Pageant window. Windows only.
/// </summary>
public sealed partial class PageantTransport : ISshAgentTransport
{
    private const uint AgentCopyDataId = 0x804e50ba;
    private const int MaxMessage = 8192;
    private const uint WmCopyData = 0x004A;
    private const uint PageReadWrite = 0x04;
    private const uint FileMapWrite = 0x0002;

    public static bool IsRunning() => OperatingSystem.IsWindows() && FindWindowW("Pageant", "Pageant") != IntPtr.Zero;

    public unsafe byte[] Request(byte[] message)
    {
        if (message.Length + 4 > MaxMessage)
        {
            throw new ArgumentException("Message too long for Pageant.", nameof(message));
        }
        var window = FindWindowW("Pageant", "Pageant");
        if (window == IntPtr.Zero)
        {
            throw new InvalidOperationException("Pageant is not running.");
        }
        var mapName = "EunomiaPageant" + Environment.CurrentManagedThreadId.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
        var mapping = CreateFileMappingW(new IntPtr(-1), IntPtr.Zero, PageReadWrite, 0, MaxMessage, mapName);
        if (mapping == IntPtr.Zero)
        {
            throw new InvalidOperationException("Could not create the Pageant file mapping.");
        }
        try
        {
            var view = MapViewOfFile(mapping, FileMapWrite, 0, 0, 0);
            if (view == IntPtr.Zero)
            {
                throw new InvalidOperationException("Could not map the Pageant file mapping.");
            }
            try
            {
                var span = new Span<byte>((void*)view, MaxMessage);
                BinaryPrimitives.WriteUInt32BigEndian(span, (uint)message.Length);
                message.CopyTo(span[4..]);
                var nameBytes = Encoding.ASCII.GetBytes(mapName + "\0");
                fixed (byte* name = nameBytes)
                {
                    var data = new CopyDataStruct { DwData = unchecked((IntPtr)AgentCopyDataId), CbData = nameBytes.Length, LpData = (IntPtr)name };
                    if (SendMessageW(window, WmCopyData, IntPtr.Zero, ref data) == IntPtr.Zero)
                    {
                        throw new InvalidOperationException("Pageant did not answer.");
                    }
                }
                var length = (int)BinaryPrimitives.ReadUInt32BigEndian(span);
                return span.Slice(4, Math.Min(length, MaxMessage - 4)).ToArray();
            }
            finally
            {
                UnmapViewOfFile(view);
            }
        }
        finally
        {
            CloseHandle(mapping);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyDataStruct
    {
        public IntPtr DwData;
        public int CbData;
        public IntPtr LpData;
    }

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr FindWindowW(string className, string windowName);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial IntPtr SendMessageW(IntPtr window, uint message, IntPtr wParam, ref CopyDataStruct lParam);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileMappingW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CreateFileMappingW(IntPtr file, IntPtr attributes, uint protect, uint sizeHigh, uint sizeLow, string name);

    [LibraryImport("kernel32.dll", EntryPoint = "MapViewOfFile", SetLastError = true)]
    private static partial IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, nuint bytes);

    [LibraryImport("kernel32.dll", EntryPoint = "UnmapViewOfFile")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnmapViewOfFile(IntPtr view);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
