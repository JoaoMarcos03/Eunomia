namespace Eunomia.UI.Shell;

/// <summary>Marcas de tempo do arranque dentro do shell (o <c>App.Desktop</c> liga-as ao <c>--startup-probe</c>).</summary>
public static class ShellTrace
{
    public static Action<string>? Mark { get; set; }

    internal static void At(string name) => Mark?.Invoke(name);

    /// <summary>Diagnóstico: partes do shell a não construir (variável <c>EUNOMIA_SKIP</c>, separada por vírgulas).</summary>
    internal static bool Skip(string part) =>
        (Environment.GetEnvironmentVariable("EUNOMIA_SKIP") ?? string.Empty).Split(',').Contains(part, StringComparer.OrdinalIgnoreCase);
}
