using System.Globalization;
using System.Text;
using Eunomia.Drivers.Abstractions;

namespace Eunomia.App.Data;

/// <summary>Operador de um item do Quick Filter (anexo D B10).</summary>
public enum QuickFilterOperator
{
    Equal,
    NotEqual,
    StartsWith,
    EndsWith,
    Contains,
    Greater,
    Less,

    /// <summary>Clipboard: <c>col IN (&lt;texto tal como está&gt;)</c>, sem escaping.</summary>
    InVerbatim,
    IsNull,
    IsNotNull,
}

/// <summary>
/// SQL dos filtros do Data tab (anexo D B9, B10): multi column filter (<c>LIKE '%x%'</c> em todas as colunas, OR) e
/// Quick Filter (valores das linhas selecionadas, valor pedido, clipboard), com o escaping do HeidiSQL: números sem aspas,
/// binário em hex, BIT como <c>b'…'</c>, texto entre aspas.
/// </summary>
public static partial class DataFilters
{
    /// <summary>Limite dos valores recolhidos das linhas selecionadas (1 KB).</summary>
    public const int MaxValuesLength = 1024;

    /// <summary>
    /// "Create multi column filter": <c>CastAsText(col) LIKE '%texto%'</c> para todas as colunas (ocultas incluídas),
    /// unidas por OR com quebras de linha; <c>ILIKE</c> onde o motor o tiver. Texto vazio dá filtro vazio.
    /// </summary>
    public static string MultiColumn(IDialect dialect, IEnumerable<ColumnDefinition> columns, string text)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(columns);
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }
        var like = dialect.CaseInsensitiveLike == LikeOperator.ILike ? " ILIKE " : " LIKE ";
        var literal = dialect.StringLiteral("%" + text + "%");
        var result = new StringBuilder();
        var lineLength = 0;
        foreach (var column in columns)
        {
            var condition = dialect.CastAsText(column) + like + literal;
            if (result.Length > 0)
            {
                if (lineLength + condition.Length > 100)
                {
                    result.Append(" OR\n");
                    lineLength = 0;
                }
                else
                {
                    result.Append(" OR ");
                    lineLength += 4;
                }
            }
            result.Append(condition);
            lineLength += condition.Length;
        }
        return result.ToString();
    }

    /// <summary>
    /// Lista do "IN (clipboard)" do Quick Filter: o texto do clipboard separado por vírgulas, tabs e quebras de linha (as
    /// aspas à volta de um item saem) e cada item como literal da coluna. Antes ia tal e qual para o SQL: um texto colado
    /// com <c>)</c> e <c>;</c> fechava o IN e corria outra instrução.
    /// </summary>
    public static string InList(IDialect dialect, ColumnDefinition column, string clipboard)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(clipboard);
        var items = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        void Flush()
        {
            var item = current.ToString().Trim();
            current.Clear();
            if (item.Length >= 2 && item[0] is '\'' or '"' && item[^1] == item[0])
            {
                var q = item[0];
                item = item[1..^1].Replace(new string(q, 2), q.ToString(), StringComparison.Ordinal);
            }
            if (item.Length > 0)
            {
                items.Add(Escape(dialect, column, item));
            }
        }
        foreach (var c in clipboard)
        {
            if (quote is { } open)
            {
                current.Append(c);
                if (c == open)
                {
                    quote = null;
                }
            }
            else if (c is '\'' or '"')
            {
                quote = c;
                current.Append(c);
            }
            else if (c is ',' or '\t' or '\n' or '\r')
            {
                Flush();
            }
            else
            {
                current.Append(c);
            }
        }
        Flush();
        return items.Count == 0 ? "NULL" : string.Join(", ", items);
    }

    /// <summary>
    /// Padrão LIKE em que <paramref name="text"/> conta letra a letra (<c>%</c>, <c>_</c> e <c>!</c> escapados com
    /// <c>ESCAPE '!'</c>, que vale em qualquer <c>sql_mode</c>): <c>'%texto%' ESCAPE '!'</c>. Sem isto, um valor com
    /// <c>_</c> ou <c>%</c> encontrava outras linhas e <c>C:\temp</c> nunca era encontrado.
    /// </summary>
    public static string LikePattern(IDialect dialect, string text, bool anyBefore, bool anyAfter)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(text);
        var escaped = text.Replace("!", "!!", StringComparison.Ordinal).Replace("%", "!%", StringComparison.Ordinal).Replace("_", "!_", StringComparison.Ordinal);
        return dialect.StringLiteral((anyBefore ? "%" : string.Empty) + escaped + (anyAfter ? "%" : string.Empty)) + " ESCAPE '!'";
    }

    /// <summary>Valor como literal SQL segundo o tipo da coluna (<c>EscapeString(v, Datatype)</c>).</summary>
    public static string Escape(IDialect dialect, ColumnDefinition column, string value)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(value);
        switch (column.Type.Kind)
        {
            case LogicalTypeKind.Integer or LogicalTypeKind.Decimal or LogicalTypeKind.Float or LogicalTypeKind.Boolean
                when Number().IsMatch(value):
                return value;
            case LogicalTypeKind.Bit when value.All(c => c is '0' or '1') && value.Length > 0:
                return "b'" + value + "'";
            case LogicalTypeKind.Binary when value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && value.Length > 2 && value.Skip(2).All(char.IsAsciiHexDigit):
                return value;
            default:
                return dialect.StringLiteral(value);
        }
    }

    /// <summary>
    /// Bloco 1 do Quick Filter: valores da coluna focada em todas as linhas selecionadas (sem repetidos, até 1 KB).
    /// <c>=</c> dá <c>col IN (…)</c> (+ <c>OR col IS NULL</c>), <c>!=</c> dá <c>col NOT IN (…)</c> (+ <c>AND col IS NOT NULL</c>);
    /// os outros juntam uma condição por valor com OR. <c>null</c> se não houver valores para o operador.
    /// </summary>
    public static string? FromValues(IDialect dialect, ColumnDefinition column, IEnumerable<string?> values, QuickFilterOperator op)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(values);
        var distinct = new List<string>();
        var hasNull = false;
        var length = 0;
        foreach (var value in values)
        {
            if (value is null)
            {
                hasNull = true;
                continue;
            }
            if (distinct.Contains(value, StringComparer.Ordinal))
            {
                continue;
            }
            if (length + value.Length > MaxValuesLength && distinct.Count > 0)
            {
                break;
            }
            distinct.Add(value);
            length += value.Length;
        }
        var col = dialect.QuoteIdentifier(column.Name);
        switch (op)
        {
            case QuickFilterOperator.Equal:
                return distinct.Count == 0
                    ? hasNull ? col + " IS NULL" : null
                    : $"{col} IN ({string.Join(", ", distinct.Select(v => Escape(dialect, column, v)))})" + (hasNull ? $" OR {col} IS NULL" : string.Empty);
            case QuickFilterOperator.NotEqual:
                return distinct.Count == 0
                    ? hasNull ? col + " IS NOT NULL" : null
                    : $"{col} NOT IN ({string.Join(", ", distinct.Select(v => Escape(dialect, column, v)))})" + (hasNull ? $" AND {col} IS NOT NULL" : string.Empty);
            default:
                return distinct.Count == 0 ? null : string.Join(" OR ", distinct.Select(v => Single(dialect, column, op, v)));
        }
    }

    /// <summary>Blocos 2 e 3 do Quick Filter: um valor escrito ou do clipboard.</summary>
    public static string Single(IDialect dialect, ColumnDefinition column, QuickFilterOperator op, string value)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(value);
        var col = dialect.QuoteIdentifier(column.Name);
        return op switch
        {
            QuickFilterOperator.Equal => $"{col} = {Escape(dialect, column, value)}",
            QuickFilterOperator.NotEqual => $"{col} != {Escape(dialect, column, value)}",
            QuickFilterOperator.StartsWith => $"{col} LIKE {LikePattern(dialect, value, anyBefore: false, anyAfter: true)}",
            QuickFilterOperator.EndsWith => $"{col} LIKE {LikePattern(dialect, value, anyBefore: true, anyAfter: false)}",
            QuickFilterOperator.Contains => $"{col} LIKE {LikePattern(dialect, value, anyBefore: true, anyAfter: true)}",
            QuickFilterOperator.Greater => $"{col} > {Escape(dialect, column, value)}",
            QuickFilterOperator.Less => $"{col} < {Escape(dialect, column, value)}",
            QuickFilterOperator.InVerbatim => $"{col} IN ({InList(dialect, column, value)})",
            QuickFilterOperator.IsNull => col + " IS NULL",
            _ => col + " IS NOT NULL",
        };
    }

    /// <summary>
    /// Shift ao escolher um Quick Filter: junta com AND ao filtro atual, salvo se um já contiver o outro. Cada parte vai
    /// entre parênteses quando tem OR, para o AND não mudar o sentido.
    /// </summary>
    public static string Combine(string? existing, string added)
    {
        ArgumentNullException.ThrowIfNull(added);
        var current = DataTabQuery.NormalizeFilter(existing);
        if (current is null || current.Contains(added, StringComparison.Ordinal))
        {
            return current ?? added;
        }
        if (added.Contains(current, StringComparison.Ordinal))
        {
            return added;
        }
        return Wrap(current) + "\nAND " + Wrap(added);
    }

    private static string Wrap(string condition) =>
        condition.Contains(" OR ", StringComparison.OrdinalIgnoreCase) || condition.Contains("\nOR", StringComparison.OrdinalIgnoreCase) || condition.Contains("OR\n", StringComparison.OrdinalIgnoreCase)
            ? "(" + condition + ")"
            : condition;

    /// <summary>Caption de um item do menu: o SQL, cortado a 100 caracteres (<c>StrEllipsis(Hint, 100)</c>).</summary>
    public static string Caption(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var single = sql.Replace("\n", " ", StringComparison.Ordinal);
        return single.Length <= 100 ? single : single[..99] + "…";
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^[+-]?(\d+(\.\d*)?|\.\d+)([eE][+-]?\d+)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex Number();
}
