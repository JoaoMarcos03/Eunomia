using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Eunomia.Engine.Transfer;

public enum TransferJobKind
{
    SqlExport,
    SqlImport,
    CsvImport,
}

/// <summary>
/// State of an import/export job in the journal (sections 13.1 and 13.3): the input (size and fingerprint) and the last
/// confirmed checkpoint. Which checkpoint fields are used depends on the kind of job.
/// </summary>
public sealed record TransferJournalEntry
{
    public required Guid Id { get; init; }

    public required TransferJobKind Kind { get; init; }

    /// <summary>Name of the saved session (resuming connects to it again).</summary>
    public required string Session { get; init; }

    public string? Database { get; init; }

    /// <summary>Input file (imports) or output file (exports, the <c>.partial</c>).</summary>
    public required string Path { get; init; }

    public long InputSize { get; init; }

    public string? InputFingerprint { get; init; }

    public DateTimeOffset Started { get; init; }

    public DateTimeOffset Updated { get; init; }

    /// <summary>Job options needed to resume (encoding, Stop on errors, mode, table, columns…).</summary>
    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>();

    /// <summary>SQL import: input bytes already confirmed (resume position).</summary>
    public long Offset { get; init; }

    /// <summary>SQL import: resume position in characters, line and delimiter (the splitter starts again with them).</summary>
    public long CharOffset { get; init; }

    public long Line { get; init; } = 1;

    public string Delimiter { get; init; } = ";";

    /// <summary>Statements run and errors (SQL import), or records read (CSV, including skipped ones).</summary>
    public long Count { get; init; }

    public long Errors { get; init; }

    /// <summary>Statement in progress when the job stopped without a transaction (the UI shows it before resuming).</summary>
    public string? LastStatement { get; init; }

    /// <summary>Export: objects already fully written and the <c>.partial</c> bytes up to the end of the last one.</summary>
    public IReadOnlyList<string> CompletedObjects { get; init; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(TransferJournalEntry))]
internal sealed partial class TransferJournalJsonContext : JsonSerializerContext;

/// <summary>
/// Journal of the import/export jobs: one JSON per job in a folder (atomic write, temporary file and rename), to resume
/// after a cancel or a crash ("Resume" or "Discard" on the next start).
/// </summary>
public sealed class TransferJournal
{
    private readonly string _directory;

    public TransferJournal(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        _directory = directory;
    }

    public string PathOf(Guid id) => System.IO.Path.Combine(_directory, $"job-{id:N}.json");

    public void Save(TransferJournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Directory.CreateDirectory(_directory);
        var path = PathOf(entry.Id);
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, entry, TransferJournalJsonContext.Default.TransferJournalEntry);
        }
        File.Move(temp, path, overwrite: true);
    }

    public void Delete(Guid id)
    {
        var path = PathOf(id);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>Unfinished jobs; unreadable files are ignored.</summary>
    public IReadOnlyList<TransferJournalEntry> List()
    {
        if (!Directory.Exists(_directory))
        {
            return [];
        }
        var result = new List<TransferJournalEntry>();
        foreach (var file in Directory.EnumerateFiles(_directory, "job-*.json"))
        {
            try
            {
                using var stream = File.OpenRead(file);
                if (JsonSerializer.Deserialize(stream, TransferJournalJsonContext.Default.TransferJournalEntry) is { } entry)
                {
                    result.Add(entry);
                }
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                // A truncated journal does not get in the way of the others.
            }
        }
        return [.. result.OrderBy(e => e.Started)];
    }

    /// <summary>
    /// Input fingerprint: SHA-256 of the size, the first and the last MiB. Reading a whole 10 GB file just
    /// to resume would take too long; a change in the middle with the same size is not detected.
    /// </summary>
    public static string Fingerprint(string path)
    {
        const int Part = 1024 * 1024;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(BitConverter.GetBytes(stream.Length));
        var buffer = new byte[Part];
        var read = stream.ReadAtLeast(buffer, Part, throwOnEndOfStream: false);
        hash.AppendData(buffer, 0, read);
        if (stream.Length > Part)
        {
            stream.Seek(Math.Max(Part, stream.Length - Part), SeekOrigin.Begin);
            read = stream.ReadAtLeast(buffer, Part, throwOnEndOfStream: false);
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
