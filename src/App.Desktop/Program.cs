using Avalonia;

namespace Eunomia.App.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        StartupTrace.Mark("main");
        // --timer-resolution N: pede resolução de N ms ao timer do Windows (para comparar nos benchmarks).
        var t = Array.IndexOf(args, "--timer-resolution");
        if (t >= 0 && t + 1 < args.Length && uint.TryParse(args[t + 1], out var period))
        {
            TimerResolution.TrySet(period);
        }
        var builder = BuildAvaloniaApp();
        var options = new Win32PlatformOptions();
        // --adapter texto: escolhe a GPU (ANGLE/D3D11) cuja descrição contém o texto; regista as disponíveis.
        var a = Array.IndexOf(args, "--adapter");
        var wanted = a >= 0 && a + 1 < args.Length ? args[a + 1] : null;
        options.GraphicsAdapterSelectionCallback = adapters =>
        {
            GraphicsInfo.Adapters = [.. adapters.Select(x => x.Description ?? "?")];
            var index = wanted is null ? 0 : adapters.ToList().FindIndex(x => x.Description?.Contains(wanted, StringComparison.OrdinalIgnoreCase) == true);
            index = Math.Max(0, index);
            GraphicsInfo.Selected = adapters.Count > 0 ? adapters[index].Description : null;
            return index;
        };
        // --composition winui|dcomp|dxgi|redirection: força o modo de composição do Win32 (para comparar).
        var c = Array.IndexOf(args, "--composition");
        if (c >= 0 && c + 1 < args.Length)
        {
            options.CompositionMode =
            [
                args[c + 1] switch
                {
                    "winui" => Win32CompositionMode.WinUIComposition,
                    "dcomp" => Win32CompositionMode.DirectComposition,
                    "dxgi" => Win32CompositionMode.LowLatencyDxgiSwapChain,
                    _ => Win32CompositionMode.RedirectionSurface,
                },
            ];
        }
        // --render angle|wgl|vulkan|software: força o modo de rendering do Win32 (para comparar nos benchmarks).
        var i = Array.IndexOf(args, "--render");
        if (i >= 0 && i + 1 < args.Length)
        {
            var mode = args[i + 1] switch
            {
                "software" => Win32RenderingMode.Software,
                "wgl" => Win32RenderingMode.Wgl,
                "vulkan" => Win32RenderingMode.Vulkan,
                _ => Win32RenderingMode.AngleEgl,
            };
            options.RenderingMode = [mode];
        }
        return builder.With(options).StartWithClassicDesktopLifetime(args);
    }

    /// <summary>GPUs vistas pelo Avalonia (só preenchido com ANGLE/D3D11).</summary>
    internal static class GraphicsInfo
    {
        public static string[] Adapters { get; set; } = [];

        public static string? Selected { get; set; }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect();
}
