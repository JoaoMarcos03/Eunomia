using Eunomia.Engine.Sql.Lexing;

namespace Eunomia.Engine.Sql.Completion;

public enum CompletionKind
{
    Keyword,
    DataType,
    Function,
    Table,
    Column,
    Database,
    View,
    Routine,
    Variable,
}

/// <summary>A suggestion: the name shown, the kind (icon and colour) and the text to insert.</summary>
public sealed record CompletionItem(string Text, CompletionKind Kind)
{
    /// <summary>Second column: column type in lower case, variable value, signature.</summary>
    public string? Detail { get; init; }

    /// <summary>Column comment or description (status bar).</summary>
    public string? Description { get; init; }

    /// <summary>Text inserted, when it differs from the one shown (identifiers in backticks).</summary>
    public string? InsertText { get; init; }

    /// <summary>Primary key column (key icon).</summary>
    public bool IsKey { get; init; }
}

/// <summary>Autocomplete result: where the text to replace starts and the suggestions in order.</summary>
public sealed record CompletionProposal(int ReplaceStart, IReadOnlyList<CompletionItem> Items);

/// <summary>
/// The spike's static completion list (no context analysis; Autocomplete A of section 16.1
/// is for the product). Searches with "Find matches in the middle" on, like HeidiSQL:
/// first the entries that start with the text, then those containing it; alphabetical order within each group.
/// </summary>
public sealed class CompletionCatalog
{
    private readonly CompletionItem[] _items;

    public CompletionCatalog(IEnumerable<CompletionItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = [.. items.OrderBy(i => i.Text, StringComparer.OrdinalIgnoreCase)];
    }

    public IReadOnlyList<CompletionItem> Items => _items;

    /// <summary>Catalog with MySQL keywords, types and functions plus the given names.</summary>
    public static CompletionCatalog CreateMySql(IEnumerable<string> tables, IEnumerable<string> columns)
    {
        var items = MySqlKeywords.Keywords.Select(k => new CompletionItem(k, CompletionKind.Keyword))
            .Concat(MySqlKeywords.DataTypes.Select(t => new CompletionItem(t, CompletionKind.DataType)))
            .Concat(MySqlKeywords.Functions.Select(f => new CompletionItem(f, CompletionKind.Function)))
            .Concat(tables.Select(t => new CompletionItem(t, CompletionKind.Table)))
            .Concat(columns.Select(c => new CompletionItem(c, CompletionKind.Column)));
        return new CompletionCatalog(items);
    }

    public IReadOnlyList<CompletionItem> Find(ReadOnlySpan<char> typed, int max = 200)
    {
        var prefix = new List<CompletionItem>();
        var middle = new List<CompletionItem>();
        foreach (var item in _items)
        {
            if (prefix.Count >= max)
            {
                break;
            }
            var index = item.Text.AsSpan().IndexOf(typed, StringComparison.OrdinalIgnoreCase);
            if (index == 0)
            {
                prefix.Add(item);
            }
            else if (index > 0 && middle.Count < max)
            {
                middle.Add(item);
            }
        }
        prefix.AddRange(middle.Take(max - prefix.Count));
        return prefix;
    }
}
