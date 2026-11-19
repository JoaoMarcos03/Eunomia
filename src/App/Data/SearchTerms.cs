using System.Text;
using System.Text.RegularExpressions;
using Eunomia.App.Search;
using Eunomia.Drivers.Abstractions;

namespace Eunomia.App.Data;

/// <summary>
/// Text of the filter panel (Ctrl+Alt+F; appendix D B11): terms separated by <c>;</c>, any of them matches (OR);
/// <c>\;</c> is a literal <c>;</c>. Each term is plain text matched anywhere in the value without case, or a regular
/// expression with <see cref="IsRegex"/> (the <c>.*</c> button). Spaces around the <c>;</c> are ignored and empty terms
/// are left out. The Data grid searches the whole table on the server (<see cref="Where"/>); the other lists, the rows
/// shown (<see cref="LocalRegex"/>).
/// </summary>
public sealed record SearchTerms
{
    private SearchTerms(IReadOnlyList<string> terms, bool isRegex)
    {
        Terms = terms;
        IsRegex = isRegex;
    }

    public IReadOnlyList<string> Terms { get; }

    public bool IsRegex { get; }

    /// <summary>
    /// The terms of <paramref name="text"/>, or <c>null</c> if there are none (empty, only spaces or <c>;</c>). From left to
    /// right: <c>\;</c> gives <c>;</c>, a <c>\</c> with any other character stays as it is (in a regex, <c>\\;</c> is
    /// <c>\\</c> and then the separator), and <c>;</c> ends the term.
    /// </summary>
    public static SearchTerms? Parse(string? text, bool regex)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var terms = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                var next = text[++i];
                if (next != ';')
                {
                    current.Append(c);
                }
                current.Append(next);
            }
            else if (c == ';')
            {
                Add();
            }
            else
            {
                current.Append(c);
            }
        }
        Add();
        return terms.Count == 0 ? null : new SearchTerms(terms, regex);

        void Add()
        {
            var term = current.ToString().Trim();
            if (term.Length > 0)
            {
                terms.Add(term);
            }
            current.Clear();
        }
    }

    /// <summary>
    /// One .NET regex for the lists filtered in memory: plain terms escaped, regex terms in <c>(?:…)</c>, joined by <c>|</c>;
    /// without case, 250 ms per match. An invalid regex throws <see cref="ArgumentException"/>.
    /// </summary>
    public Regex LocalRegex() =>
        new(string.Join("|", Terms.Select(t => IsRegex ? "(?:" + t + ")" : Regex.Escape(t))),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    /// <summary>
    /// Condition for the Data grid: any term in any visible column (<paramref name="hidden"/> ones out), joined by <c>OR</c>;
    /// <c>null</c> without columns. Plain text: <c>LOWER(CONVERT(col USING utf8mb4)) LIKE '%term%'</c>, with <c>%</c> and
    /// <c>_</c> matched as themselves (the same as Find text on server); regex: <c>CONVERT(col USING utf8mb4) REGEXP 'term'</c>
    /// (the <c>CONVERT</c> avoids MySQL 8's error 3995 on binary columns; the default collation does not tell case apart).
    /// </summary>
    public string? Where(IDialect dialect, TableDefinition table, IReadOnlyCollection<string> hidden)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(hidden);
        var hiddenSet = hidden.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var visible = table.Columns.Where(c => !hiddenSet.Contains(c.Name)).ToList();
        if (visible.Count == 0)
        {
            return null;
        }
        var conditions = IsRegex
            ? Terms.SelectMany(t => visible.Select(c => $"CONVERT({dialect.QuoteIdentifier(c.Name)} USING utf8mb4) REGEXP {dialect.StringLiteral(t)}"))
            : Terms.Select(t => ServerTextSearch.Where(dialect, table with { Columns = visible }, new ServerTextSearchOptions(t, false, null, ServerTextMatch.Contains, false)))
                .OfType<string>();
        return string.Join(" OR ", conditions) is { Length: > 0 } where ? where : null;
    }

    public bool Equals(SearchTerms? other) =>
        other is not null && IsRegex == other.IsRegex && Terms.SequenceEqual(other.Terms, StringComparer.Ordinal);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(IsRegex);
        foreach (var term in Terms)
        {
            hash.Add(term, StringComparer.Ordinal);
        }
        return hash.ToHashCode();
    }
}
