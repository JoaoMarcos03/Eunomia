using System.Runtime.InteropServices;

namespace Eunomia.App.Desktop;

/// <summary>
/// Windows timer resolution for this process (<c>timeBeginPeriod</c>, winmm). By default the tick is
/// 15.625 ms, which rounds the render loop's ~16 ms waits to 15.6 or 31.2 ms. Since
/// Windows 10 2004 the request only affects the calling process.
/// </summary>
internal static partial class TimerResolution
{
    [LibraryImport("winmm.dll")]
    private static partial uint timeBeginPeriod(uint period);

    public static bool TrySet(uint milliseconds)
    {
        try
        {
            return timeBeginPeriod(milliseconds) == 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }
}
