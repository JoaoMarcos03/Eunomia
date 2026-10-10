using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Eunomia.App.Sessions;

namespace Eunomia.Infra.Settings;

/// <summary>
/// Sessions in <c>sessions.json</c> (section 21.4), atomic write with the previous version in <c>sessions.json.bak</c>. A
/// corrupted file is set aside with the <c>.corrupt-&lt;date&gt;</c> suffix and the sessions come back from the <c>.bak</c>
/// (the list only starts empty if that one is no good either). No passwords: they are in the secret vault.
/// <para>
/// Several application instances (phase 14): when saving, if the file changed since this instance's last read or write,
/// the disk version is merged with this one per property of each session (three-way merge: what this instance did not
/// change comes from the disk), instead of the last instance to save wiping out the other's work.
/// </para>
/// </summary>
public sealed class JsonSessionStore(string path, TimeProvider? clock = null) : ISessionStore
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private Dictionary<Guid, JsonObject> _baseSessions = [];
    private HashSet<string> _baseFolders = [];
    private byte[]? _diskHash;

    public string Path { get; } = path;

    public string? LastCorruptBackup { get; private set; }

    /// <summary>The sessions came from the <c>.bak</c> because the main file was corrupted.</summary>
    public bool RestoredFromBackup { get; private set; }

    public SessionCatalog Load()
    {
        lock (_gate)
        {
            RestoredFromBackup = false;
            if (!File.Exists(Path))
            {
                Remember(new SessionCatalog(), null);
                return new SessionCatalog();
            }
            var bytes = File.ReadAllBytes(Path);
            if (TryParse(bytes) is { } catalog)
            {
                Remember(catalog, bytes);
                return catalog;
            }
            var backup = $"{Path}.corrupt-{_clock.GetUtcNow():yyyyMMddHHmmss}";
            File.Move(Path, backup, overwrite: true);
            LastCorruptBackup = backup;
            if (File.Exists(Path + ".bak") && TryParse(File.ReadAllBytes(Path + ".bak")) is { } previous)
            {
                RestoredFromBackup = true;
                Remember(previous, null);
                return previous;
            }
            Remember(new SessionCatalog(), null);
            return new SessionCatalog();
        }
    }

    public void Save(SessionCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        lock (_gate)
        {
            catalog.Version = SessionCatalog.CurrentVersion;
            if (File.Exists(Path))
            {
                var disk = File.ReadAllBytes(Path);
                if (_diskHash is null || !SHA256.HashData(disk).AsSpan().SequenceEqual(_diskHash))
                {
                    // Another instance saved in the meantime: three-way merge with what this instance saw last time.
                    if (TryParse(disk) is { } theirs)
                    {
                        Merge(catalog, theirs);
                    }
                }
            }
            var json = JsonSerializer.SerializeToUtf8Bytes(catalog, SessionsJsonContext.Default.SessionCatalog);
            AtomicFile.WriteAllBytes(Path, json, keepBackup: true);
            Remember(catalog, json);
        }
    }

    private static SessionCatalog? TryParse(byte[] bytes)
    {
        try
        {
            var catalog = JsonSerializer.Deserialize(bytes, SessionsJsonContext.Default.SessionCatalog) ?? new SessionCatalog();
            catalog.Sessions ??= [];
            catalog.Folders ??= [];
            foreach (var s in catalog.Sessions)
            {
                Normalize(s);
            }
            return catalog;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Normalize(SessionDefinition s)
    {
        s.Folder ??= [];
        s.Ssh ??= new SshSettings();
        s.Ssl ??= new SslSettings();
        s.Statistics ??= new SessionStatistics();
    }

    private void Remember(SessionCatalog catalog, byte[]? diskBytes)
    {
        _baseSessions = catalog.Sessions.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => ToNode(g.First()));
        _baseFolders = [.. catalog.Folders.Select(FolderKey)];
        _diskHash = diskBytes is null ? null : SHA256.HashData(diskBytes);
    }

    private static JsonObject ToNode(SessionDefinition session) =>
        JsonSerializer.SerializeToNode(session, SessionsJsonContext.Default.SessionDefinition)!.AsObject();

    private static string FolderKey(List<string> folder) => string.Join('\u0001', folder);

    /// <summary>
    /// Merges the changes of <paramref name="theirs"/> into <paramref name="ours"/>: per session (by id) and per property,
    /// what this instance did not change since its last read comes from the disk; new sessions on disk are added; a session
    /// deleted on one side goes, unless the other side changed it in the meantime (so work is never lost by mistake).
    /// </summary>
    private void Merge(SessionCatalog ours, SessionCatalog theirs)
    {
        var theirNodes = theirs.Sessions.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => ToNode(g.First()));
        var merged = new List<SessionDefinition>();
        foreach (var session in ours.Sessions)
        {
            var mine = ToNode(session);
            _baseSessions.TryGetValue(session.Id, out var original);
            if (!theirNodes.TryGetValue(session.Id, out var other))
            {
                // Deleted on disk: it stays only if this instance created or changed it.
                if (original is null || !JsonNode.DeepEquals(original, mine))
                {
                    merged.Add(session);
                }
                continue;
            }
            if (original is null || JsonNode.DeepEquals(original, mine))
            {
                merged.Add(FromNode(other));
                continue;
            }
            merged.Add(FromNode(MergeObject(original, mine, other)));
        }
        var known = ours.Sessions.Select(s => s.Id).ToHashSet();
        foreach (var other in theirs.Sessions.Where(s => !known.Contains(s.Id)))
        {
            // New on disk, or deleted here: deleted here goes, unless the other instance changed it.
            if (!_baseSessions.TryGetValue(other.Id, out var original) || !JsonNode.DeepEquals(original, ToNode(other)))
            {
                merged.Add(other);
            }
        }
        ours.Sessions = merged;

        var mineFolders = ours.Folders.ToDictionary(FolderKey);
        var theirFolders = theirs.Folders.ToDictionary(FolderKey);
        ours.Folders = [.. mineFolders.Concat(theirFolders.Where(f => !mineFolders.ContainsKey(f.Key)))
            .Where(f => !(_baseFolders.Contains(f.Key) && (!mineFolders.ContainsKey(f.Key) || !theirFolders.ContainsKey(f.Key))))
            .Select(f => f.Value)];
    }

    private static SessionDefinition FromNode(JsonNode node)
    {
        var session = node.Deserialize(SessionsJsonContext.Default.SessionDefinition) ?? new SessionDefinition();
        Normalize(session);
        return session;
    }

    /// <summary>Three-way merge per property (recursive into objects): what did not change in <paramref name="mine"/> comes from <paramref name="theirs"/>.</summary>
    private static JsonObject MergeObject(JsonObject original, JsonObject mine, JsonObject theirs)
    {
        var result = new JsonObject();
        foreach (var name in mine.Select(p => p.Key).Union(theirs.Select(p => p.Key)))
        {
            original.TryGetPropertyValue(name, out var b);
            mine.TryGetPropertyValue(name, out var m);
            theirs.TryGetPropertyValue(name, out var t);
            JsonNode? value;
            if (JsonNode.DeepEquals(m, b))
            {
                value = t;
            }
            else if (m is JsonObject mo && b is JsonObject bo && t is JsonObject to)
            {
                value = MergeObject(bo, mo, to);
            }
            else
            {
                value = m;
            }
            result[name] = value?.DeepClone();
        }
        return result;
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(SessionCatalog))]
[JsonSerializable(typeof(SessionDefinition))]
internal sealed partial class SessionsJsonContext : JsonSerializerContext;
