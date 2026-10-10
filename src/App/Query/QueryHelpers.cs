using System.Globalization;
using Eunomia.App.Settings;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Sql;

namespace Eunomia.App.Query;

public enum HelperKind
{
    Root,
    Column,
    Function,
    Keyword,
    Snippet,
    HistoryDay,
    HistoryItem,
}

/// <summary>Um nó do painel de helpers: texto, segunda coluna e o que representa (coluna, snippet, entrada do histórico).</summary>
public sealed record HelperItem(HelperKind Kind, string Text, string Attribute = "", object? Payload = null)
{
    public IReadOnlyList<HelperItem> Children { get; init; } = [];
}

/// <summary>
/// Painel de helpers da query tab (anexo C §1.11, §3.9; FAM-Q06): "Columns in tbl", "SQL functions", "SQL keywords",
/// "Snippets" e "Query history" (dias, o mais recente primeiro, com "today"/"yesterday"; entradas <c>hh:nn: sql</c> e a
/// duração). "Query profile" e "Bind parameters" ficam para depois do MVP (secção 27.2). Também gera
/// SELECT/INSERT/UPDATE/DELETE para as colunas escolhidas.
/// </summary>
public static class QueryHelpers
{
    public const int ColumnsRoot = 0;
    public const int FunctionsRoot = 1;
    public const int KeywordsRoot = 2;
    public const int SnippetsRoot = 3;
    public const int HistoryRoot = 4;

    /// <param name="language">Linguagem da sessão da tab (funções e palavras-chave).</param>
    /// <param name="table">Tabela ou view selecionada na árvore (null: sem colunas).</param>
    /// <param name="snippets">Caminhos dos ficheiros <c>.sql</c> da pasta dos snippets.</param>
    /// <param name="today">Data local de hoje (para "today"/"yesterday").</param>
    public static IReadOnlyList<HelperItem> Build(ISqlLanguage language, DbObjectRef? table, IReadOnlyList<ColumnDefinition>? columns,
        IEnumerable<string> snippets, IEnumerable<QueryHistoryEntry> history, DateTime today)
    {
        ArgumentNullException.ThrowIfNull(language);
        ArgumentNullException.ThrowIfNull(snippets);
        ArgumentNullException.ThrowIfNull(history);
        var columnItems = table is not null && columns is not null
            ? columns.Select(c => new HelperItem(HelperKind.Column, c.Name, c.NativeType.ToLowerInvariant(), table)).ToList()
            : [];
        var days = history
            .GroupBy(h => h.ExecutedAt.LocalDateTime.Date)
            .OrderByDescending(g => g.Key)
            .Select(g => new HelperItem(HelperKind.HistoryDay, DayCaption(g.Key, today), string.Empty, g.Key)
            {
                Children = [.. g.OrderByDescending(h => h.ExecutedAt).Select(HistoryItem)],
            })
            .ToList();
        return
        [
            new HelperItem(HelperKind.Root, table is null ? "Columns" : $"Columns in {table.Name}") { Children = columnItems },
            new HelperItem(HelperKind.Root, "SQL functions")
            {
                Children = [.. language.Functions.Order(StringComparer.OrdinalIgnoreCase).Select(f => new HelperItem(HelperKind.Function, f))],
            },
            new HelperItem(HelperKind.Root, "SQL keywords")
            {
                Children = [.. language.Keywords.Order(StringComparer.OrdinalIgnoreCase).Select(k => new HelperItem(HelperKind.Keyword, k))],
            },
            new HelperItem(HelperKind.Root, "Snippets")
            {
                Children = [.. snippets.Order(StringComparer.CurrentCultureIgnoreCase).Select(s => new HelperItem(HelperKind.Snippet, Path.GetFileNameWithoutExtension(s), string.Empty, s))],
            },
            new HelperItem(HelperKind.Root, "Query history") { Children = days },
        ];
    }

    private static string DayCaption(DateTime day, DateTime today)
    {
        var text = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return day == today.Date ? text + " (today)" : day == today.Date.AddDays(-1) ? text + " (yesterday)" : text;
    }

    private static HelperItem HistoryItem(QueryHistoryEntry entry)
    {
        var sql = entry.Sql.ReplaceLineEndings(" ");
        return new HelperItem(HelperKind.HistoryItem,
            entry.ExecutedAt.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture) + ": " + (sql.Length > 200 ? sql[..200] : sql),
            string.Create(CultureInfo.InvariantCulture, $"{entry.Duration.TotalSeconds:0.000}s"),
            entry);
    }

    /// <summary>Texto que "Insert at cursor" (ou o duplo clique) insere: o nome, o SQL do histórico ou o snippet.</summary>
    public static string? InsertText(ISqlLanguage language, HelperItem item, Func<string, string> readSnippet)
    {
        ArgumentNullException.ThrowIfNull(language);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(readSnippet);
        return item.Kind switch
        {
            HelperKind.Column => language.EditorIdentifier(item.Text),
            HelperKind.Function or HelperKind.Keyword => item.Text,
            HelperKind.Snippet when item.Payload is string path => readSnippet(path),
            HelperKind.HistoryItem when item.Payload is QueryHistoryEntry entry => entry.Sql,
            _ => null,
        };
    }

    /// <summary>Generate SELECT/INSERT/UPDATE/DELETE para as colunas escolhidas (todas, se nenhuma).</summary>
    public static string Generate(ISqlLanguage language, string kind, DbObjectRef table, IReadOnlyList<string> columns)
    {
        ArgumentNullException.ThrowIfNull(language);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(columns);
        var name = language.EditorQualifiedName(table.Catalog, table.Name);
        var quoted = columns.Select(language.EditorIdentifier).ToList();
        var nl = Environment.NewLine;
        return kind.ToUpperInvariant() switch
        {
            "SELECT" => $"SELECT {string.Join(", ", quoted)}{nl}\tFROM {name};",
            "INSERT" => $"INSERT INTO {name}{nl}\t({string.Join(", ", quoted)}){nl}\tVALUES ({string.Join(", ", quoted.Select(_ => "''"))});",
            "UPDATE" => $"UPDATE {name} SET{nl}\t{string.Join($",{nl}\t", quoted.Select(c => c + "=''"))}{nl}\tWHERE {quoted[0]}='';",
            "DELETE" => $"DELETE FROM {name}{nl}\tWHERE {string.Join(" AND ", quoted.Select(c => c + "=''"))};",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    /// <summary>Pasta dos snippets: a das settings, senão <c>Documents\Eunomia\Snippets</c>.</summary>
    public static string SnippetsDirectory(string? configured) =>
        !string.IsNullOrWhiteSpace(configured)
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Eunomia", "Snippets");

    /// <summary>Nome de ficheiro válido para um snippet (sem caracteres proibidos, com <c>.sql</c>).</summary>
    public static string SnippetFileName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string([.. name.Trim().Select(c => invalid.Contains(c) ? '_' : c)]);
        return clean.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) ? clean : clean + ".sql";
    }
}
