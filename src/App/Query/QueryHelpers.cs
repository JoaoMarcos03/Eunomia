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

/// <summary>A node of the helpers panel: text, second column and what it represents (column, snippet, history entry).</summary>
public sealed record HelperItem(HelperKind Kind, string Text, string Attribute = "", object? Payload = null)
{
    public IReadOnlyList<HelperItem> Children { get; init; } = [];
}

/// <summary>
/// Query tab helpers panel (appendix C §1.11, §3.9; FAM-Q06): "Columns in tbl", "SQL functions", "SQL keywords",
/// "Snippets" and "Query history" (days, most recent first, with "today"/"yesterday"; entries <c>hh:nn: sql</c> and the
/// duration). "Query profile" and "Bind parameters" come after the MVP (section 27.2). It also generates
/// SELECT/INSERT/UPDATE/DELETE for the chosen columns.
/// </summary>
public static class QueryHelpers
{
    public const int ColumnsRoot = 0;
    public const int FunctionsRoot = 1;
    public const int KeywordsRoot = 2;
    public const int SnippetsRoot = 3;
    public const int HistoryRoot = 4;

    /// <param name="language">Language of the tab's session (functions and keywords).</param>
    /// <param name="table">Table or view selected in the tree (null: no columns).</param>
    /// <param name="snippets">Paths of the <c>.sql</c> files in the snippets folder.</param>
    /// <param name="today">Today's local date (for "today"/"yesterday").</param>
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

    /// <summary>Text that "Insert at cursor" (or a double click) inserts: the name, the history SQL or the snippet.</summary>
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

    /// <summary>Generate SELECT/INSERT/UPDATE/DELETE for the chosen columns (all of them, if none).</summary>
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

    /// <summary>Snippets folder: the one in the settings, otherwise <c>Documents\Eunomia\Snippets</c>.</summary>
    public static string SnippetsDirectory(string? configured) =>
        !string.IsNullOrWhiteSpace(configured)
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Eunomia", "Snippets");

    /// <summary>Valid file name for a snippet (without forbidden characters, with <c>.sql</c>).</summary>
    public static string SnippetFileName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string([.. name.Trim().Select(c => invalid.Contains(c) ? '_' : c)]);
        return clean.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) ? clean : clean + ".sql";
    }
}
