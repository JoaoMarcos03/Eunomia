using System.Text;
using System.Text.RegularExpressions;

namespace Eunomia.App.Search;

/// <summary>Opções do "Search and replace text" (anexo C §4.7).</summary>
public sealed record SearchOptions
{
    public string Find { get; init; } = string.Empty;

    /// <summary>Texto de substituição; <c>null</c> = só procurar.</summary>
    public string? Replace { get; init; }

    public bool CaseSensitive { get; init; }

    public bool WholeWords { get; init; }

    public bool RegularExpression { get; init; }

    public bool PromptOnReplace { get; init; } = true;

    /// <summary>Direção Backward.</summary>
    public bool Backward { get; init; }

    /// <summary>Origem "Entire scope" (a omissão); falso = "From cursor".</summary>
    public bool EntireScope { get; init; } = true;

    /// <summary>Âmbito "Selection" em vez de "Global".</summary>
    public bool SelectionOnly { get; init; }
}

/// <summary>Uma ocorrência no texto.</summary>
public readonly record struct TextMatch(int Start, int Length, string Replacement);

/// <summary>
/// Procura e substituição no texto de um editor (anexo C §4.7; FAM-Q10): texto literal ou regex, maiúsculas, palavras
/// inteiras, para a frente ou para trás, dentro de um intervalo (o texto todo ou a seleção). A substituição aceita
/// <c>\n</c> e <c>\t</c> e, com regex, <c>$0</c>–<c>$9</c>.
/// </summary>
public static class TextSearch
{
    /// <summary>Regex das opções (ArgumentException se a expressão for inválida).</summary>
    public static Regex BuildRegex(SearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var pattern = options.RegularExpression ? options.Find : Regex.Escape(options.Find);
        if (options.WholeWords)
        {
            pattern = @"(?<!\w)(?:" + pattern + @")(?!\w)";
        }
        var flags = RegexOptions.Multiline | RegexOptions.CultureInvariant;
        if (!options.CaseSensitive)
        {
            flags |= RegexOptions.IgnoreCase;
        }
        return new Regex(pattern, flags, TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Próxima ocorrência a partir de <paramref name="from"/> (para trás: a última que acaba até <paramref name="from"/>),
    /// dentro de [<paramref name="scopeStart"/>, <paramref name="scopeEnd"/>). Ocorrências vazias não contam.
    /// </summary>
    public static TextMatch? FindNext(string text, int from, int scopeStart, int scopeEnd, SearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Find.Length == 0)
        {
            return null;
        }
        var regex = BuildRegex(options);
        scopeStart = Math.Clamp(scopeStart, 0, text.Length);
        scopeEnd = Math.Clamp(scopeEnd, scopeStart, text.Length);
        from = Math.Clamp(from, scopeStart, scopeEnd);
        Match? found = null;
        if (options.Backward)
        {
            for (var m = regex.Match(text, scopeStart); m.Success && m.Index + m.Length <= from; m = m.NextMatch())
            {
                if (m.Length > 0)
                {
                    found = m;
                }
            }
        }
        else
        {
            for (var m = regex.Match(text, from); m.Success && m.Index + m.Length <= scopeEnd; m = m.NextMatch())
            {
                if (m.Length > 0)
                {
                    found = m;
                    break;
                }
            }
        }
        return found is null ? null : new TextMatch(found.Index, found.Length, options.Replace is { } r ? Expand(found, r, options.RegularExpression) : string.Empty);
    }

    /// <summary>Substitui todas as ocorrências no intervalo; devolve o texto novo e quantas foram.</summary>
    public static (string Text, int Count) ReplaceAll(string text, int scopeStart, int scopeEnd, SearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Find.Length == 0 || options.Replace is null)
        {
            return (text, 0);
        }
        var regex = BuildRegex(options);
        scopeStart = Math.Clamp(scopeStart, 0, text.Length);
        scopeEnd = Math.Clamp(scopeEnd, scopeStart, text.Length);
        var count = 0;
        var result = new StringBuilder(text.Length);
        var last = scopeStart;
        result.Append(text, 0, scopeStart);
        for (var m = regex.Match(text, scopeStart); m.Success && m.Index + m.Length <= scopeEnd; m = m.NextMatch())
        {
            if (m.Length == 0)
            {
                continue;
            }
            result.Append(text, last, m.Index - last).Append(Expand(m, options.Replace, options.RegularExpression));
            last = m.Index + m.Length;
            count++;
        }
        result.Append(text, last, text.Length - last);
        return (result.ToString(), count);
    }

    /// <summary>Texto que substitui uma ocorrência: <c>\n</c>, <c>\t</c> e <c>\\</c>; com regex, os grupos <c>$n</c>.</summary>
    public static string Expand(Match match, string replacement, bool regularExpression)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(replacement);
        var unescaped = new StringBuilder(replacement.Length);
        for (var i = 0; i < replacement.Length; i++)
        {
            if (replacement[i] == '\\' && i + 1 < replacement.Length && replacement[i + 1] is 'n' or 't' or '\\')
            {
                unescaped.Append(replacement[i + 1] switch { 'n' => '\n', 't' => '\t', _ => '\\' });
                i++;
            }
            else
            {
                unescaped.Append(replacement[i]);
            }
        }
        return regularExpression ? match.Result(unescaped.ToString()) : unescaped.ToString();
    }

    /// <summary>Junta um texto ao topo de uma lista de recentes (sem repetidos, no máximo <paramref name="max"/>).</summary>
    public static void AddRecent(List<string> recent, string value, int max = 25)
    {
        ArgumentNullException.ThrowIfNull(recent);
        if (string.IsNullOrEmpty(value))
        {
            return;
        }
        recent.RemoveAll(r => string.Equals(r, value, StringComparison.Ordinal));
        recent.Insert(0, value);
        if (recent.Count > max)
        {
            recent.RemoveRange(max, recent.Count - max);
        }
    }
}
