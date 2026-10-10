namespace Eunomia.UI.Shell;

/// <summary>Startup timestamps inside the shell (<c>App.Desktop</c> wires them to <c>--startup-probe</c>).</summary>
public static class ShellTrace
{
    public static Action<string>? Mark { get; set; }

    internal static void At(string name) => Mark?.Invoke(name);

    /// <summary>Diagnostics: parts of the shell not to build (<c>EUNOMIA_SKIP</c> variable, comma-separated).</summary>
    internal static bool Skip(string part) =>
        (Environment.GetEnvironmentVariable("EUNOMIA_SKIP") ?? string.Empty).Split(',').Contains(part, StringComparer.OrdinalIgnoreCase);
}
