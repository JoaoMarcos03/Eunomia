namespace Eunomia.App.Commands;

/// <summary>Um comando na paleta: id, legenda, categoria, atalho (texto) e se está ligado (toggles).</summary>
public sealed record CommandMatch(string Id, string Caption, string Category, string? Shortcut, bool IsChecked);

/// <summary>
/// Pesquisa da paleta de comandos (Ctrl+Shift+P; roadmap 1.x #11): cada palavra escrita tem de aparecer na legenda ou na
/// categoria, sem distinguir maiúsculas. Primeiro as legendas que começam pelo texto escrito, depois as que têm uma palavra
/// a começar pela primeira palavra escrita, depois as outras; dentro de cada grupo, pela legenda.
/// </summary>
public static class CommandSearch
{
    public static IReadOnlyList<CommandMatch> Filter(IEnumerable<CommandMatch> commands, string? query, int max = 50)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max);
        var text = (query ?? string.Empty).Trim();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return [.. commands
            .Where(c => words.All(w => c.Caption.Contains(w, StringComparison.OrdinalIgnoreCase) || c.Category.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(c => Rank(c.Caption, text, words))
            .ThenBy(c => c.Caption, StringComparer.CurrentCultureIgnoreCase)
            .Take(max)];
    }

    private static int Rank(string caption, string text, string[] words)
    {
        if (text.Length == 0 || caption.StartsWith(text, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }
        return caption.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(w => w.StartsWith(words[0], StringComparison.OrdinalIgnoreCase)) ? 1 : 2;
    }
}
