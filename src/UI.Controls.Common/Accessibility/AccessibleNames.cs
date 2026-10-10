using Avalonia.Automation;
using Avalonia.Controls;

namespace Eunomia.UI.Controls.Common.Accessibility;

/// <summary>
/// Names for screen readers (roadmap 4.4; NVDA, Narrator, VoiceOver): a field next to a label gets the label's text
/// as its accessible name, without the colon ("Hostname / IP:" → "Hostname / IP"), and the screen reader says
/// "Hostname / IP, edit" instead of just "edit". A name already given is not replaced.
/// </summary>
public static class AccessibleNames
{
    /// <summary>Gives <paramref name="control"/> the name <paramref name="label"/> (without the trailing colon), if it has none yet.</summary>
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
