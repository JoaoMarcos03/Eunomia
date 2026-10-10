using Avalonia.Input;

namespace Eunomia.UI.Controls.Common.Input;

/// <summary>
/// Keys per system (phase 14): on macOS the Command key (⌘, <see cref="KeyModifiers.Meta"/>) plays the part of the Ctrl of
/// HeidiSQL's shortcuts (⌘C, ⌘Enter, ⌘-click to add to the selection), as in other Mac applications; the physical Control
/// key keeps working. Nothing changes on other systems.
/// </summary>
public static class PlatformKeys
{
    /// <summary>The shortcuts' Ctrl is Command (macOS).</summary>
    public static bool CommandIsMeta { get; } = OperatingSystem.IsMacOS();

    /// <summary>Modifiers with Command treated as Ctrl on macOS.</summary>
    public static KeyModifiers Normalize(KeyModifiers modifiers) =>
        CommandIsMeta && modifiers.HasFlag(KeyModifiers.Meta) ? (modifiers & ~KeyModifiers.Meta) | KeyModifiers.Control : modifiers;

    /// <summary>The shortcuts' "Ctrl" is pressed (Command on macOS, Ctrl everywhere).</summary>
    public static bool IsCommand(KeyModifiers modifiers) => Normalize(modifiers).HasFlag(KeyModifiers.Control);

    /// <summary>Modifiers to show a shortcut in a menu: Ctrl becomes Command on macOS.</summary>
    public static KeyModifiers ForDisplay(KeyModifiers modifiers) =>
        CommandIsMeta && modifiers.HasFlag(KeyModifiers.Control) ? (modifiers & ~KeyModifiers.Control) | KeyModifiers.Meta : modifiers;
}
