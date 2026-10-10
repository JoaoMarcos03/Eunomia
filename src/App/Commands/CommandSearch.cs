namespace Eunomia.App.Commands;

/// <summary>A command in the palette: id, caption, category, shortcut (text) and whether it is on (toggles).</summary>
public sealed record CommandMatch(string Id, string Caption, string Category, string? Shortcut, bool IsChecked);

/// <summary>
/// Command palette search (Ctrl+Shift+P; roadmap 1.x #11): every typed word must appear in the caption or the
/// category, case-insensitively. First the captions that start with the typed text, then those with a word
/// starting with the first typed word, then the rest; within each group, by caption.
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
