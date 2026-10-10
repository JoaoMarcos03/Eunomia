using Avalonia.Input;
using Eunomia.App.Commands;
using Eunomia.UI.Controls.Common.Input;

namespace Eunomia.UI.Shell.Input;

/// <summary>Conversion between <see cref="KeyChord"/> (no UI, App layer) and Avalonia keys.</summary>
public static class KeyChordMapper
{
    public static KeyChord? FromKey(Key key, KeyModifiers modifiers)
    {
        var name = NameOf(key);
        if (name is null)
        {
            return null;
        }
        // On macOS, Command (⌘) acts as Ctrl.
        modifiers = PlatformKeys.Normalize(modifiers);
        var m = ChordModifiers.None;
        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            m |= ChordModifiers.Ctrl;
        }
        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            m |= ChordModifiers.Shift;
        }
        if (modifiers.HasFlag(KeyModifiers.Alt))
        {
            m |= ChordModifiers.Alt;
        }
        return new KeyChord(m, name);
    }

    public static KeyGesture? ToGesture(KeyChord? chord)
    {
        if (chord is not { } c || KeyOf(c.Key) is not { } key)
        {
            return null;
        }
        var m = KeyModifiers.None;
        if (c.Modifiers.HasFlag(ChordModifiers.Ctrl))
        {
            m |= KeyModifiers.Control;
        }
        if (c.Modifiers.HasFlag(ChordModifiers.Shift))
        {
            m |= KeyModifiers.Shift;
        }
        if (c.Modifiers.HasFlag(ChordModifiers.Alt))
        {
            m |= KeyModifiers.Alt;
        }
        return new KeyGesture(key, PlatformKeys.ForDisplay(m));
    }

    /// <summary>Canonical key name (the <see cref="KeyChord"/> one), or <c>null</c> for keys without a shortcut (modifiers).</summary>
    public static string? NameOf(Key key) => key switch
    {
        >= Key.A and <= Key.Z => ((char)('A' + (key - Key.A))).ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.F1 and <= Key.F24 => "F" + (key - Key.F1 + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
        Key.Insert => "Ins",
        Key.Delete => "Del",
        Key.Escape => "Esc",
        Key.Enter => "Enter",
        Key.PageDown => "PgDn",
        Key.PageUp => "PgUp",
        Key.Space => "Space",
        Key.Tab => "Tab",
        Key.Home => "Home",
        Key.End => "End",
        Key.Back => "Backspace",
        Key.Left => "Left",
        Key.Right => "Right",
        Key.Up => "Up",
        Key.Down => "Down",
        _ => null,
    };

    public static Key? KeyOf(string name)
    {
        if (name.Length == 1 && char.IsAsciiLetterUpper(name[0]))
        {
            return Key.A + (name[0] - 'A');
        }
        if (name.Length == 1 && char.IsAsciiDigit(name[0]))
        {
            return Key.D0 + (name[0] - '0');
        }
        if (name.Length > 1 && name[0] == 'F' && int.TryParse(name.AsSpan(1), out var f) && f is >= 1 and <= 24)
        {
            return Key.F1 + (f - 1);
        }
        return name switch
        {
            "Ins" => Key.Insert,
            "Del" => Key.Delete,
            "Esc" => Key.Escape,
            "Enter" => Key.Enter,
            "PgDn" => Key.PageDown,
            "PgUp" => Key.PageUp,
            "Space" => Key.Space,
            "Tab" => Key.Tab,
            "Home" => Key.Home,
            "End" => Key.End,
            "Backspace" => Key.Back,
            "Left" => Key.Left,
            "Right" => Key.Right,
            "Up" => Key.Up,
            "Down" => Key.Down,
            _ => null,
        };
    }
}
