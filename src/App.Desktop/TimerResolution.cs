using System.Runtime.InteropServices;

namespace Eunomia.App.Desktop;

/// <summary>
/// Resolução do timer do Windows para este processo (<c>timeBeginPeriod</c>, winmm). Por omissão o tick é
/// de 15,625 ms, o que arredonda as esperas de ~16 ms do render loop para 15,6 ou 31,2 ms. Desde o
/// Windows 10 2004 o pedido só afeta o próprio processo.
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
