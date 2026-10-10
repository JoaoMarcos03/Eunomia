using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Eunomia.App.Sessions;

namespace Eunomia.Infra.Settings;

/// <summary>
/// Sessões em <c>sessions.json</c> (secção 21.4), escrita atómica com a versão anterior em <c>sessions.json.bak</c>. Um
/// ficheiro corrompido fica de lado com o sufixo <c>.corrupt-&lt;data&gt;</c> e as sessões voltam do <c>.bak</c> (a lista
/// só arranca vazia se este também não servir). Sem passwords: estão no cofre de segredos.
/// <para>
/// Várias instâncias da aplicação (Fase 14): ao gravar, se o ficheiro mudou desde a última leitura ou escrita desta
/// instância, junta-se a versão do disco com a desta por propriedade de cada sessão (fusão a três: o que esta instância
/// não mudou vem do disco), em vez de a última instância a gravar apagar o trabalho da outra.
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

    /// <summary>As sessões vieram do <c>.bak</c> porque o ficheiro principal estava corrompido.</summary>
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
                    // Outra instância gravou entretanto: fusão a três com o que esta instância viu da última vez.
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
    /// Junta em <paramref name="ours"/> as alterações de <paramref name="theirs"/>: por sessão (pelo id) e por propriedade,
    /// o que esta instância não mudou desde a última leitura vem do disco; sessões novas do disco juntam-se; uma sessão
    /// apagada de um lado sai, salvo se o outro lado a mudou entretanto (assim nunca se perde trabalho por engano).
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
                // Apagada no disco: fica só se esta instância a criou ou mudou.
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
            // Nova no disco, ou apagada aqui: apagada aqui sai, salvo se a outra instância a mudou.
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

    /// <summary>Fusão a três por propriedade (recursiva nos objetos): o que não mudou em <paramref name="mine"/> vem de <paramref name="theirs"/>.</summary>
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
