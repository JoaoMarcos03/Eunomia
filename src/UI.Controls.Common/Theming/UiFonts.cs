using Avalonia.Media;

namespace Eunomia.UI.Controls.Common.Theming;

/// <summary>
/// Fontes da interface por sistema (Fase 14): Segoe UI e Consolas no Windows, como o HeidiSQL; no macOS e no Linux a
/// fonte da interface é a do sistema e a monoespaçada a primeira que existir de uma lista habitual (Menlo no macOS;
/// DejaVu Sans Mono, Liberation Mono, Noto Sans Mono ou Ubuntu Mono no Linux). Uma fonte pedida que não existe cai na
/// seguinte da lista, nunca numa proporcional no editor de SQL.
/// </summary>
public static class UiFonts
{
    /// <summary>Lista de fontes monoespaçadas para pôr a seguir a uma escolhida pelo utilizador.</summary>
    public static string MonospaceNames { get; } =
        OperatingSystem.IsWindows() ? "Consolas, Cascadia Mono, Courier New"
        : OperatingSystem.IsMacOS() ? "Menlo, SF Mono, Monaco, Courier New"
        : "DejaVu Sans Mono, Liberation Mono, Noto Sans Mono, Ubuntu Mono, Courier New";

    /// <summary>Fonte da interface (janelas, diálogos, árvore, grid por omissão).</summary>
    public static FontFamily Ui { get; } = OperatingSystem.IsWindows() ? new FontFamily("Segoe UI") : FontFamily.Default;

    /// <summary>Fonte monoespaçada (editor de SQL, SQL log, código).</summary>
    public static FontFamily Monospace { get; } = new(MonospaceNames);

    /// <summary>Uma fonte escolhida nas preferências com as alternativas do sistema a seguir.</summary>
    public static FontFamily MonospaceWith(string? preferred) =>
        string.IsNullOrWhiteSpace(preferred) ? Monospace : new FontFamily(preferred + ", " + MonospaceNames);
}
