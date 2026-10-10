using Avalonia.Automation;
using Avalonia.Controls;

namespace Eunomia.UI.Controls.Common.Accessibility;

/// <summary>
/// Nomes para leitores de ecrã (roadmap 4.4; NVDA, Narrator, VoiceOver): um campo ao lado de um rótulo leva o texto do
/// rótulo como nome acessível, sem os dois pontos ("Hostname / IP:" → "Hostname / IP"), e o leitor de ecrã lê
/// "Hostname / IP, edit" em vez de só "edit". Um nome já dado não é substituído.
/// </summary>
public static class AccessibleNames
{
    /// <summary>Dá a <paramref name="control"/> o nome <paramref name="label"/> (sem os dois pontos finais), se ainda não tiver.</summary>
    public static T Named<T>(this T control, string label)
        where T : Control
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(label);
        var name = label.Trim().TrimEnd(':').TrimEnd();
        if (name.Length > 0 && string.IsNullOrWhiteSpace(AutomationProperties.GetName(control)))
        {
            AutomationProperties.SetName(control, name);
        }
        return control;
    }
}
