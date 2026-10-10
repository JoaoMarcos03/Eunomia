using Avalonia.Input;

namespace Eunomia.UI.Controls.Common.Input;

/// <summary>
/// Teclas por sistema (Fase 14): no macOS a tecla Command (⌘, <see cref="KeyModifiers.Meta"/>) faz o papel do Ctrl dos
/// atalhos do HeidiSQL (⌘C, ⌘Enter, ⌘-clique para juntar à seleção), como nas outras aplicações do Mac; o Control físico
/// continua a funcionar. Noutros sistemas nada muda.
/// </summary>
public static class PlatformKeys
{
    /// <summary>O Ctrl dos atalhos é o Command (macOS).</summary>
    public static bool CommandIsMeta { get; } = OperatingSystem.IsMacOS();

    /// <summary>Modificadores com o Command tratado como Ctrl no macOS.</summary>
    public static KeyModifiers Normalize(KeyModifiers modifiers) =>
        CommandIsMeta && modifiers.HasFlag(KeyModifiers.Meta) ? (modifiers & ~KeyModifiers.Meta) | KeyModifiers.Control : modifiers;

    /// <summary>O "Ctrl" dos atalhos está carregado (Command no macOS, Ctrl em todos).</summary>
    public static bool IsCommand(KeyModifiers modifiers) => Normalize(modifiers).HasFlag(KeyModifiers.Control);

    /// <summary>Modificadores para mostrar um atalho num menu: Ctrl passa a Command no macOS.</summary>
    public static KeyModifiers ForDisplay(KeyModifiers modifiers) =>
        CommandIsMeta && modifiers.HasFlag(KeyModifiers.Control) ? (modifiers & ~KeyModifiers.Control) | KeyModifiers.Meta : modifiers;
}
