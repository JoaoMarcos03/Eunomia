using Avalonia.Media;

namespace Eunomia.UI.Controls.Common.Theming;

/// <summary>
/// UI fonts per system (phase 14): Segoe UI and Consolas on Windows, like HeidiSQL; on macOS and Linux the UI font
/// is the system's and the monospaced one is the first available from a usual list (Menlo on macOS;
/// DejaVu Sans Mono, Liberation Mono, Noto Sans Mono or Ubuntu Mono on Linux). A requested font that does not exist falls
/// back to the next one on the list, never to a proportional font in the SQL editor.
/// </summary>
public static class UiFonts
{
    /// <summary>List of monospaced fonts to put after one chosen by the user.</summary>
    public static string MonospaceNames { get; } =
        OperatingSystem.IsWindows() ? "Consolas, Cascadia Mono, Courier New"
        : OperatingSystem.IsMacOS() ? "Menlo, SF Mono, Monaco, Courier New"
        : "DejaVu Sans Mono, Liberation Mono, Noto Sans Mono, Ubuntu Mono, Courier New";

    /// <summary>UI font (windows, dialogs, tree, grid by default).</summary>
    public static FontFamily Ui { get; } = OperatingSystem.IsWindows() ? new FontFamily("Segoe UI") : FontFamily.Default;

    /// <summary>Monospaced font (SQL editor, SQL log, code).</summary>
    public static FontFamily Monospace { get; } = new(MonospaceNames);

    /// <summary>A font chosen in the preferences, followed by the system alternatives.</summary>
    public static FontFamily MonospaceWith(string? preferred) =>
        string.IsNullOrWhiteSpace(preferred) ? Monospace : new FontFamily(preferred + ", " + MonospaceNames);
}
