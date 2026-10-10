using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eunomia.App.Query;

namespace Eunomia.Infra.Settings.Journal;

/// <summary>State of a tab on disk, with its owner (process and start time, so recycled PIDs are not confused).</summary>
internal sealed record TabStateFile(TabState State, int Pid, long ProcessStart);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TabStateFile))]
internal sealed partial class TabJournalJsonContext : JsonSerializerContext;

/// <summary>
/// Query tab journal on disk (section 21.4; TR-31; PR-21). Per tab, <c>query-tab-&lt;id&gt;.json</c> with the state
/// (atomic write) and <c>query-tab-&lt;id&gt;.journal</c> with the text: append-only records
/// <c>[size][crc32][type][data]</c>, a snapshot and then the changes; <see cref="Flush"/> appends the pending ones
/// and forces them to disk. A record cut by a crash in the middle of a write fails the CRC and is ignored, along with
/// everything after it. When there are many changes, the journal is rewritten as a snapshot (temporary file and rename).
/// Tabs of another running instance are not restored (like the <c>pid</c> in HeidiSQL's <c>tabs.ini</c>).
/// </summary>
public sealed class TabJournal : ITabJournal
{
    private const byte SnapshotRecord = 1;
    private const byte EditRecord = 2;

    /// <summary>Changes after the last snapshot from which the journal is compacted.</summary>
    public const int CompactAfterEdits = 2000;

    private readonly string _directory;
    private readonly Lock _gate = new();
    private readonly Lock _io = new();
    private readonly Dictionary<Guid, List<TextEdit>> _pending = [];
    private readonly Dictionary<Guid, string> _resets = [];
    private readonly Dictionary<Guid, int> _editsSinceSnapshot = [];
    private readonly HashSet<Guid> _removed = [];
    private readonly int _pid;
    private readonly long _processStart;

    public TabJournal(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        _directory = directory;
        Directory.CreateDirectory(directory);
        using var current = Process.GetCurrentProcess();
        _pid = current.Id;
        _processStart = current.StartTime.ToUniversalTime().Ticks;
    }

    private string StatePath(Guid tab) => Path.Combine(_directory, $"query-tab-{tab:N}.json");

    private string JournalPath(Guid tab) => Path.Combine(_directory, $"query-tab-{tab:N}.journal");

    public IReadOnlyList<RestoredTab> Restore()
    {
        var result = new List<RestoredTab>();
        foreach (var file in Directory.EnumerateFiles(_directory, "query-tab-*.json"))
        {
            TabStateFile? saved;
            try
            {
                saved = JsonSerializer.Deserialize(File.ReadAllText(file), TabJournalJsonContext.Default.TabStateFile);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                continue;
            }
            if (saved is null || OwnedByOtherInstance(saved))
            {
                continue;
            }
            var text = Replay(JournalPath(saved.State.Id));
            result.Add(new RestoredTab(saved.State, text));
            SaveState(saved.State);
        }
        return [.. result.OrderBy(t => t.State.Order)];
    }

    /// <summary>Another live instance (not this one) with the same PID and start time.</summary>
    private bool OwnedByOtherInstance(TabStateFile saved)
    {
        if (saved.Pid == _pid && saved.ProcessStart == _processStart)
        {
            return false;
        }
        try
        {
            using var process = Process.GetProcessById(saved.Pid);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == saved.ProcessStart;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public void SaveState(TabState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            if (_removed.Contains(state.Id))
            {
                return;
            }
        }
        lock (_io)
        {
            AtomicFile.WriteAllText(StatePath(state.Id), JsonSerializer.Serialize(new TabStateFile(state, _pid, _processStart), TabJournalJsonContext.Default.TabStateFile));
        }
    }

    public void Append(Guid tab, TextEdit edit)
    {
        lock (_gate)
        {
            if (!_pending.TryGetValue(tab, out var edits))
            {
                edits = [];
                _pending[tab] = edits;
            }
            edits.Add(edit);
        }
    }

    public void Reset(Guid tab, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_gate)
        {
            _pending.Remove(tab);
            _resets[tab] = text;
            _removed.Remove(tab);
        }
    }

    public void Flush()
    {
        Dictionary<Guid, List<TextEdit>> edits;
        Dictionary<Guid, string> resets;
        lock (_gate)
        {
            edits = new Dictionary<Guid, List<TextEdit>>(_pending);
            resets = new Dictionary<Guid, string>(_resets);
            _pending.Clear();
            _resets.Clear();
        }
        lock (_io)
        {
            foreach (var (tab, text) in resets)
            {
                WriteSnapshot(tab, text);
            }
            foreach (var (tab, list) in edits)
            {
                lock (_gate)
                {
                    if (_removed.Contains(tab))
                    {
                        continue;
                    }
                }
                using (var stream = new FileStream(JournalPath(tab), FileMode.Append, FileAccess.Write, FileShare.Read))
                {
                    foreach (var edit in list)
                    {
                        WriteRecord(stream, EncodeEdit(edit));
                    }
                    stream.Flush(flushToDisk: true);
                }
                var count = _editsSinceSnapshot.GetValueOrDefault(tab) + list.Count;
                _editsSinceSnapshot[tab] = count;
                if (count >= CompactAfterEdits)
                {
                    WriteSnapshot(tab, Replay(JournalPath(tab)));
                }
            }
        }
    }

    public void Remove(Guid tab)
    {
        lock (_gate)
        {
            _pending.Remove(tab);
            _resets.Remove(tab);
            _removed.Add(tab);
        }
        lock (_io)
        {
            _editsSinceSnapshot.Remove(tab);
            File.Delete(StatePath(tab));
            File.Delete(JournalPath(tab));
        }
    }

    /// <summary>Rewrites the journal with just a snapshot (temporary file and rename: a crash leaves the previous one).</summary>
    private void WriteSnapshot(Guid tab, string text)
    {
        using var buffer = new MemoryStream();
        var payload = new byte[1 + Encoding.UTF8.GetByteCount(text)];
        payload[0] = SnapshotRecord;
        Encoding.UTF8.GetBytes(text, payload.AsSpan(1));
        WriteRecord(buffer, payload);
        AtomicFile.WriteAllBytes(JournalPath(tab), buffer.ToArray());
        _editsSinceSnapshot[tab] = 0;
    }

    private static byte[] EncodeEdit(TextEdit edit)
    {
        var inserted = Encoding.UTF8.GetByteCount(edit.Inserted);
        var payload = new byte[1 + 8 + inserted];
        payload[0] = EditRecord;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(1), edit.Offset);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(5), edit.RemovedLength);
        Encoding.UTF8.GetBytes(edit.Inserted, payload.AsSpan(9));
        return payload;
    }

    private static void WriteRecord(Stream stream, byte[] payload)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], Crc32(payload));
        stream.Write(header);
        stream.Write(payload);
    }

    /// <summary>Journal text: the last snapshot and the changes after it, up to the first invalid record.</summary>
    public static string Replay(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
        {
            return string.Empty;
        }
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return string.Empty;
        }
        var text = new StringBuilder();
        var position = 0;
        while (position + 8 <= data.Length)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(position));
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position + 4));
            if (length < 1 || position + 8 + length > data.Length)
            {
                break;
            }
            var payload = data.AsSpan(position + 8, length);
            if (Crc32(payload) != crc)
            {
                break;
            }
            if (payload[0] == SnapshotRecord)
            {
                text.Clear().Append(Encoding.UTF8.GetString(payload[1..]));
            }
            else if (payload[0] == EditRecord && payload.Length >= 9)
            {
                var offset = BinaryPrimitives.ReadInt32LittleEndian(payload[1..]);
                var removed = BinaryPrimitives.ReadInt32LittleEndian(payload[5..]);
                if (offset < 0 || removed < 0 || offset + removed > text.Length)
                {
                    break;
                }
                text.Remove(offset, removed).Insert(offset, Encoding.UTF8.GetString(payload[9..]));
            }
            else
            {
                break;
            }
            position += 8 + length;
        }
        return text.ToString();
    }

    private static readonly uint[] CrcTable = CreateCrcTable();

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
            table[i] = c;
        }
        return table;
    }

    /// <summary>CRC-32 (IEEE 802.3).</summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        return ~crc;
    }
}
