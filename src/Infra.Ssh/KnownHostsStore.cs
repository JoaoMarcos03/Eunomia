using System.Globalization;
using System.Security.Cryptography;

namespace Eunomia.Infra.Ssh;

public enum HostKeyCheck
{
    Match,
    Unknown,
    Mismatch,

    /// <summary>No key of this type, but the host already has a key of another type saved.</summary>
    OtherTypeKnown,
}

/// <summary>
/// The application's own known_hosts (section 23.2: confirmation on first contact, blocking on a key change),
/// in OpenSSH format: <c>host type base64</c>, or <c>[host]:port type base64</c> when the port is not 22.
/// Atomic write; thread-safe.
/// </summary>
public sealed class KnownHostsStore(string path)
{
    private readonly Lock _gate = new();

    public string Path { get; } = path;

    public static string HostPattern(string host, int port) =>
        port == 22 ? host.ToLowerInvariant() : string.Create(CultureInfo.InvariantCulture, $"[{host.ToLowerInvariant()}]:{port}");

    public HostKeyCheck Check(string host, int port, string keyType, ReadOnlySpan<byte> key)
    {
        var pattern = HostPattern(host, port);
        var encoded = Convert.ToBase64String(key);
        var sawType = false;
        var sawOtherType = false;
        lock (_gate)
        {
            foreach (var entry in Read())
            {
                if (!entry.Hosts.Contains(pattern, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!string.Equals(entry.KeyType, keyType, StringComparison.Ordinal))
                {
                    sawOtherType = true;
                    continue;
                }
                sawType = true;
                if (string.Equals(entry.Key, encoded, StringComparison.Ordinal))
                {
                    return HostKeyCheck.Match;
                }
            }
        }
        return sawType ? HostKeyCheck.Mismatch : sawOtherType ? HostKeyCheck.OtherTypeKnown : HostKeyCheck.Unknown;
    }

    /// <summary>Saves the key (replaces the previous one for the same host and type).</summary>
    public void Trust(string host, int port, string keyType, ReadOnlySpan<byte> key)
    {
        var pattern = HostPattern(host, port);
        var line = $"{pattern} {keyType} {Convert.ToBase64String(key)}";
        lock (_gate)
        {
            var lines = File.Exists(Path) ? File.ReadAllLines(Path).ToList() : [];
            lines.RemoveAll(l =>
            {
                var e = Parse(l);
                return e is not null && e.KeyType == keyType && e.Hosts.Contains(pattern, StringComparer.OrdinalIgnoreCase);
            });
            lines.Add(line);
            var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var temp = Path + ".tmp";
            File.WriteAllLines(temp, lines);
            File.Move(temp, Path, overwrite: true);
        }
    }

    /// <summary>SHA256 fingerprint in OpenSSH format (base64 without padding).</summary>
    public static string Fingerprint(ReadOnlySpan<byte> key) => "SHA256:" + Convert.ToBase64String(SHA256.HashData(key)).TrimEnd('=');

    private sealed record Entry(string[] Hosts, string KeyType, string Key);

    private List<Entry> Read()
    {
        if (!File.Exists(Path))
        {
            return [];
        }
        return File.ReadAllLines(Path).Select(Parse).OfType<Entry>().ToList();
    }

    private static Entry? Parse(string line)
    {
        var text = line.Trim();
        if (text.Length == 0 || text.StartsWith('#'))
        {
            return null;
        }
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length < 3 ? null : new Entry(parts[0].Split(','), parts[1], parts[2]);
    }
}
