using System.Runtime.InteropServices;

namespace Eunomia.App.Desktop;

/// <summary>
/// Informação de composição do DWM (<c>DwmGetCompositionTimingInfo</c> com hwnd nulo, Windows 8+,
/// existe no Windows 10). No benchmark só se usa a taxa de refresh: no Windows 10 desta máquina os
/// contadores de frames globais não avançam.
/// </summary>
internal static partial class DwmTiming
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Ratio
    {
        public uint Numerator;
        public uint Denominator;
    }

    // DWM_TIMING_INFO (dwmapi.h), campos até cFramesDropped; o tamanho total é o da estrutura completa.
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct TimingInfo
    {
        public uint cbSize;
        public Ratio rateRefresh;
        public ulong qpcRefreshPeriod;
        public Ratio rateCompose;
        public ulong qpcVBlank;
        public ulong cRefresh;
        public uint cDXRefresh;
        public ulong qpcCompose;
        public ulong cFrame;
        public uint cDXPresent;
        public ulong cRefreshFrame;
        public ulong cFrameSubmitted;
        public uint cDXPresentSubmitted;
        public ulong cFrameConfirmed;
        public uint cDXPresentConfirmed;
        public ulong cRefreshConfirmed;
        public uint cDXRefreshConfirmed;
        public ulong cFramesLate;
        public uint cFramesOutstanding;
        public ulong cFrameDisplayed;
        public ulong qpcFrameDisplayed;
        public ulong cRefreshFrameDisplayed;
        public ulong cFrameComplete;
        public ulong qpcFrameComplete;
        public ulong cFramePending;
        public ulong qpcFramePending;
        public ulong cFramesDisplayed;
        public ulong cFramesComplete;
        public ulong cFramesPending;
        public ulong cFramesAvailable;
        public ulong cFramesDropped;
        public ulong cFramesMissed;
        public ulong cRefreshNextDisplayed;
        public ulong cRefreshNextPresented;
        public ulong cRefreshesDisplayed;
        public ulong cRefreshesPresented;
        public ulong cRefreshStarted;
        public ulong cPixelsReceived;
        public ulong cPixelsDrawn;
        public ulong cBuffersEmpty;
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetCompositionTimingInfo(IntPtr hwnd, ref TimingInfo info);

    /// <summary>(frames compostos, refreshes, taxa de refresh em Hz) ou <c>null</c> se a chamada falhar.</summary>
    public static (ulong Frames, ulong Refreshes, double RefreshHz)? Sample()
    {
        var info = new TimingInfo { cbSize = (uint)Marshal.SizeOf<TimingInfo>() };
        try
        {
            if (DwmGetCompositionTimingInfo(IntPtr.Zero, ref info) != 0)
            {
                return null;
            }
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        var hz = info.rateRefresh.Denominator == 0 ? 0 : (double)info.rateRefresh.Numerator / info.rateRefresh.Denominator;
        return (info.cFrame, info.cRefresh, hz);
    }
}
