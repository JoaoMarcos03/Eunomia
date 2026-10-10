using System.Globalization;
using System.Text;
using Eunomia.Drivers.Abstractions;

namespace Eunomia.App.Data;

/// <summary>Uma coluna do ORDER BY do Data tab.</summary>
public sealed record DataSort(string Column, bool Descending);

/// <summary>SELECT de uma página do Data tab e as colunas que traz (nomes de origem, pela ordem do SELECT).</summary>
public sealed record DataSelect(string Sql, IReadOnlyList<string> Columns, IReadOnlySet<string> TruncatedColumns, IReadOnlyList<string> KeyColumns);

/// <summary>
/// SELECT do Data tab como o <c>DataGridBeforePaint</c> do HeidiSQL (anexo D B2, B3): colunas ocultas ficam de fora salvo
/// se forem chave ou se a tabela não tiver chave; colunas longas cortadas a 256 só com chave; <c>*</c> quando nada é
/// cortado nem escondido; <c>WHERE</c> do filtro, <c>ORDER BY</c> acumulado e <c>LIMIT off, n</c>.
/// </summary>
public static class DataTabQuery
{
    /// <summary><c>GRIDMAXDATA</c>: limite fixo do texto carregado por célula.</summary>
    public const int MaxData = 256;

    /// <summary>Linhas por passo (<c>DatagridRowsPerStep</c>) e máximo do Show all (<c>DatagridMaximumRows</c>).</summary>
    public const int DefaultRowsPerStep = 1000;

    public const int DefaultMaximumRows = 100_000;

    /// <summary>Colunas da chave: a PK; senão a primeira UNIQUE com todas as colunas NOT NULL; senão nenhuma.</summary>
    public static IReadOnlyList<string> KeyColumns(TableDefinition table)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (table.Indexes.FirstOrDefault(i => i.Kind == IndexKind.Primary) is { } primary)
        {
            return primary.Columns;
        }
        var nullable = table.Columns.Where(c => c.IsNullable).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return table.Indexes.FirstOrDefault(i => i.Kind == IndexKind.Unique && i.Columns.All(c => !nullable.Contains(c)))?.Columns ?? [];
    }

    /// <summary>Texto do filtro a usar no <c>WHERE</c>: sem um <c>WHERE</c> inicial; <c>null</c> se só tiver espaços ou comentários.</summary>
    public static string? NormalizeFilter(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(StripComments(text)))
        {
            return null;
        }
        var trimmed = text.Trim();
        if (trimmed.StartsWith("WHERE", StringComparison.OrdinalIgnoreCase) && (trimmed.Length == 5 || char.IsWhiteSpace(trimmed[5])))
        {
            trimmed = trimmed[5..].TrimStart();
        }
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>
    /// Filtro pronto para o SQL do Data tab: o de <see cref="NormalizeFilter"/>, sem o <c>;</c> final, e só uma condição.
    /// O WHERE vai na mesma ligação que aceita várias instruções: um <c>;</c> a meio (de um texto colado, por exemplo)
    /// correria o resto como outra instrução, por isso é recusado (<see cref="InvalidOperationException"/>).
    /// </summary>
    public static string? WhereClause(string? text)
    {
        if (NormalizeFilter(text) is not { } filter)
        {
            return null;
        }
        if (FilterError(filter) is { } error)
        {
            throw new InvalidOperationException(error);
        }
        return Eunomia.Engine.Sql.Splitting.SqlSplitter.Split(filter) is [{ } single] ? single.Text : filter;
    }

    /// <summary>Erro de um filtro com mais de uma instrução (um <c>;</c> fora de strings e comentários), ou <c>null</c>.</summary>
    public static string? FilterError(string? text) =>
        NormalizeFilter(text) is { } filter && Eunomia.Engine.Sql.Splitting.SqlSplitter.Split(filter).Count > 1
            ? "The filter must be a single WHERE condition: remove the \";\" that separates it into several statements."
            : null;

    private static string StripComments(string text)
    {
        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '#' || (text[i] == '-' && i + 1 < text.Length && text[i + 1] == '-'))
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }
            }
            else if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 1;
            }
            else
            {
                result.Append(text[i]);
            }
        }
        return result.ToString();
    }

    /// <summary>
    /// SELECT de <paramref name="count"/> linhas a partir de <paramref name="offset"/>. Com <paramref name="fullRows"/>,
    /// nada é cortado (Export e Copy as reexecutam assim, anexo D B4).
    /// </summary>
    public static DataSelect Build(IDialect dialect, TableDefinition table, IReadOnlyCollection<string> hidden, string? filter,
        IReadOnlyList<DataSort> sort, long offset, int count, bool fullRows = false)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(hidden);
        ArgumentNullException.ThrowIfNull(sort);
        var keys = KeyColumns(table);
        var keySet = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hiddenSet = hidden.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var columns = new List<string>();
        var expressions = new List<string>();
        var truncated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var star = true;
        foreach (var column in table.Columns)
        {
            var isKey = keySet.Contains(column.Name);
            if (hiddenSet.Contains(column.Name) && !isKey && keys.Count > 0)
            {
                star = false;
                continue;
            }
            if (column.IsInvisible)
            {
                star = false;
            }
            var cut = !fullRows && keys.Count > 0 && !isKey ? dialect.LoadPartExpression(column, MaxData) : null;
            if (cut is not null)
            {
                truncated.Add(column.Name);
                star = false;
            }
            columns.Add(column.Name);
            expressions.Add(cut ?? dialect.QuoteIdentifier(column.Name));
        }

        var sql = new StringBuilder("SELECT ");
        sql.Append(star ? "*" : string.Join(", ", expressions));
        sql.Append(" FROM ").Append(dialect.QualifiedName(table.Ref));
        if (WhereClause(filter) is { } where)
        {
            sql.Append(" WHERE ").Append(where);
        }
        var known = table.Columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var order = sort.Where(s => known.Contains(s.Column)).ToList();
        if (order.Count > 0)
        {
            sql.Append(" ORDER BY ").Append(string.Join(", ", order.Select(s => dialect.QuoteIdentifier(s.Column) + (s.Descending ? " DESC" : " ASC"))));
        }
        sql.Append(' ').Append(dialect.Paging(offset, count));
        return new DataSelect(sql.ToString(), columns, truncated, keys);
    }

    /// <summary>Clique no header (anexo D B7): ASC → DESC → removida; coluna nova acrescentada (DESC com Shift).</summary>
    public static IReadOnlyList<DataSort> ToggleSort(IReadOnlyList<DataSort> sort, string column, bool shift)
    {
        ArgumentNullException.ThrowIfNull(sort);
        var list = sort.ToList();
        var index = list.FindIndex(s => string.Equals(s.Column, column, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            list.Add(new DataSort(column, shift));
        }
        else if (!list[index].Descending)
        {
            list[index] = list[index] with { Descending = true };
        }
        else
        {
            list.RemoveAt(index);
        }
        return list;
    }

    /// <summary>Valor <c>Sort</c> gravado por tabela: <c>&lt;ordem&gt;_&lt;coluna&gt;|…</c>, 0 = ASC.</summary>
    public static string FormatSort(IReadOnlyList<DataSort> sort)
    {
        ArgumentNullException.ThrowIfNull(sort);
        return string.Join("|", sort.Select(s => (s.Descending ? "1_" : "0_") + s.Column));
    }

    public static IReadOnlyList<DataSort> ParseSort(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return [];
        }
        var result = new List<DataSort>();
        foreach (var item in value.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var underscore = item.IndexOf('_', StringComparison.Ordinal);
            if (underscore > 0 && item.Length > underscore + 1)
            {
                result.Add(new DataSort(item[(underscore + 1)..], item[..underscore] != "0"));
            }
        }
        return result;
    }

    /// <summary>Valor <c>HiddenColumns</c> gravado por tabela, separado por <c>|</c>.</summary>
    public static string FormatHidden(IEnumerable<string> hidden) => string.Join("|", hidden);

    public static IReadOnlyList<string> ParseHidden(string? value) =>
        string.IsNullOrEmpty(value) ? [] : value.Split('|', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Label do Data tab (anexo D B6): "db.t: N rows total (exact)"; com limite ou filtro, o total vem da contagem
    /// aproximada (ou exata) e acrescenta ", limited to N" ou ", N rows match to filter". O sufixo (exact/approximately)
    /// só em InnoDB. Views: "db.v: N rows".
    /// </summary>
    public static string CountLabel(string database, string table, DataCount count, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(count);
        culture ??= CultureInfo.CurrentCulture;
        string N(long n) => n.ToString("N0", culture);
        var head = $"{database}.{table}: ";
        if (count.IsView)
        {
            return head + N(count.Loaded) + " rows";
        }
        if (!count.Limited && !count.Filtered)
        {
            return head + N(count.Loaded) + " rows total" + (count.InnoDb ? " (exact)" : string.Empty);
        }
        var total = count.Total ?? count.Loaded;
        var text = head + N(total) + " rows total";
        if (count.InnoDb)
        {
            text += count.TotalExact ? " (exact)" : " (approximately)";
        }
        if (count.Filtered)
        {
            return text + (count.Loaded == total ? ", all rows match to filter" : $", {N(count.Loaded)} rows match to filter");
        }
        return text + ", limited to " + N(count.Loaded);
    }
}

/// <summary>Números da label do Data tab.</summary>
/// <param name="Loaded">Linhas carregadas no grid.</param>
/// <param name="Limited">O SELECT pode ter deixado linhas de fora (carregou tantas como pediu).</param>
/// <param name="Filtered">Há filtro WHERE.</param>
/// <param name="Total">Contagem da tabela (aproximada do <c>SHOW TABLE STATUS</c>, ou exata do <c>COUNT(*)</c>).</param>
/// <param name="TotalExact">A contagem é exata.</param>
/// <param name="InnoDb">Engine InnoDB (só aí aparece o sufixo).</param>
/// <param name="IsView">View: só as linhas carregadas.</param>
public sealed record DataCount(long Loaded, bool Limited, bool Filtered, long? Total, bool TotalExact, bool InnoDb, bool IsView = false);
