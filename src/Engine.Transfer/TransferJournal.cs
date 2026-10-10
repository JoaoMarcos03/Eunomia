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
/// Estado de um job de import/export no journal (secção 13.1 e 13.3): o input (tamanho e impressão digital) e o último
/// checkpoint confirmado. Os campos de checkpoint usados dependem do tipo de job.
/// </summary>
public sealed record TransferJournalEntry
{
    public required Guid Id { get; init; }

    public required TransferJobKind Kind { get; init; }

    /// <summary>Nome da sessão gravada (a retoma liga-se de novo a ela).</summary>
    public required string Session { get; init; }

    public string? Database { get; init; }

    /// <summary>Ficheiro de input (imports) ou de output (exports, o <c>.partial</c>).</summary>
    public required string Path { get; init; }

    public long InputSize { get; init; }

    public string? InputFingerprint { get; init; }

    public DateTimeOffset Started { get; init; }

    public DateTimeOffset Updated { get; init; }

    /// <summary>Opções do job necessárias para retomar (encoding, Stop on errors, modo, tabela, colunas…).</summary>
    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>();

    /// <summary>Import SQL: bytes do input já confirmados (posição de retoma).</summary>
    public long Offset { get; init; }

    /// <summary>Import SQL: posição de retoma em caracteres, linha e delimitador (o splitter recomeça com eles).</summary>
    public long CharOffset { get; init; }

    public long Line { get; init; } = 1;

    public string Delimiter { get; init; } = ";";

    /// <summary>Instruções executadas e erros (import SQL), ou registos lidos (CSV, incluindo os ignorados).</summary>
    public long Count { get; init; }

    public long Errors { get; init; }

    /// <summary>Instrução em curso quando o job parou sem transação (a UI mostra-a antes de retomar).</summary>
    public string? LastStatement { get; init; }

    /// <summary>Export: objetos já escritos por inteiro e bytes do <c>.partial</c> até ao fim do último.</summary>
    public IReadOnlyList<string> CompletedObjects { get; init; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(TransferJournalEntry))]
internal sealed partial class TransferJournalJsonContext : JsonSerializerContext;

/// <summary>
/// Journal dos jobs de import/export: um JSON por job numa pasta (escrita atómica, temporário e rename), para retomar
/// depois de um cancel ou de um crash ("Retomar" ou "Descartar" no arranque seguinte).
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

    /// <summary>Jobs por acabar; ficheiros ilegíveis são ignorados.</summary>
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
                // Um journal cortado não impede os outros.
            }
        }
        return [.. result.OrderBy(e => e.Started)];
    }

    /// <summary>
    /// Impressão digital do input: SHA-256 do tamanho, do primeiro e do último MiB. Ler um ficheiro de 10 GB inteiro só
    /// para a retoma demoraria; uma alteração no meio com o mesmo tamanho não é detetada.
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
