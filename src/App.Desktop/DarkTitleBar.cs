using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;

namespace Eunomia.App.Desktop;

/// <summary>
/// Barra de título do Windows no tema da aplicação (Fase 13): no tema escuro pede ao DWM a barra escura
/// (<c>DWMWA_USE_IMMERSIVE_DARK_MODE</c>, atributo 20 desde o Windows 10 20H1; 19 nas versões 1809–1909). Em versões
/// sem o atributo a chamada falha e a barra fica clara, sem mais efeitos.
/// </summary>
internal static partial class DarkTitleBar
{
    private const int ImmersiveDarkMode = 20;
    private const int ImmersiveDarkModeBefore20H1 = 19;

    public static void Install()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) =>
        {
            Apply(window);
            window.ActualThemeVariantChanged += (_, _) => Apply(window);
        }, RoutingStrategies.Direct);
    }

    private static void Apply(Window window)
    {
        if (window.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero)
        {
            return;
        }
        var dark = window.ActualThemeVariant == ThemeVariant.Dark ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, ImmersiveDarkMode, ref dark, sizeof(int)) != 0)
        {
            _ = DwmSetWindowAttribute(hwnd, ImmersiveDarkModeBefore20H1, ref dark, sizeof(int));
        }
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
