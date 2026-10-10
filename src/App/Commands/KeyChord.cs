using System.Diagnostics.CodeAnalysis;

namespace Eunomia.App.Commands;

[Flags]
public enum ChordModifiers
{
    None = 0,
    Ctrl = 1,
    Shift = 2,
    Alt = 4,
}

/// <summary>
/// Keyboard shortcut independent of the UI framework (the App layer does not reference Avalonia, section 21):
/// modifiers and the key name, as in the text "Ctrl+Shift+F9". The UI converts it to the framework type.
/// </summary>
public readonly record struct KeyChord(ChordModifiers Modifiers, string Key)
{
    /// <summary>Reads "Ctrl+Shift+F9", "Alt+Enter", "Ins", "F5". Modifiers in any order, case-insensitive.</summary>
    public static KeyChord Parse(string text) =>
        TryParse(text, out var chord) ? chord : throw new FormatException($"Invalid shortcut: '{text}'.");

    public static bool TryParse(string? text, [NotNullWhen(true)] out KeyChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        var modifiers = ChordModifiers.None;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var m = parts[i].ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => ChordModifiers.Ctrl,
                "SHIFT" => ChordModifiers.Shift,
                "ALT" => ChordModifiers.Alt,
                _ => (ChordModifiers?)null,
            };
            if (m is null || modifiers.HasFlag(m.Value))
            {
                return false;
            }
            modifiers |= m.Value;
        }
        var key = Normalize(parts[^1]);
        if (key.Length == 0)
        {
            return false;
        }
        chord = new KeyChord(modifiers, key);
        return true;
    }

    public override string ToString()
    {
        var prefix = (Modifiers.HasFlag(ChordModifiers.Ctrl) ? "Ctrl+" : string.Empty)
            + (Modifiers.HasFlag(ChordModifiers.Shift) ? "Shift+" : string.Empty)
            + (Modifiers.HasFlag(ChordModifiers.Alt) ? "Alt+" : string.Empty);
        return prefix + Key;
    }

    private static string Normalize(string key)
    {
        if (key.Length == 0)
        {
            return string.Empty;
        }
        var upper = key.ToUpperInvariant();
        return upper switch
        {
            "INS" or "INSERT" => "Ins",
            "DEL" or "DELETE" => "Del",
            "ESC" or "ESCAPE" => "Esc",
            "RETURN" or "ENTER" => "Enter",
            "PGDN" or "PAGEDOWN" => "PgDn",
            "PGUP" or "PAGEUP" => "PgUp",
            "SPACE" => "Space",
            "TAB" => "Tab",
            "HOME" => "Home",
            "END" => "End",
            _ when upper.Length == 1 => upper,
            _ when upper[0] == 'F' && int.TryParse(upper.AsSpan(1), out var n) && n is >= 1 and <= 24 => upper,
            _ => char.ToUpperInvariant(key[0]) + key[1..],
        };
    }
}
