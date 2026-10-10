using System.Runtime.InteropServices;

namespace Eunomia.App.Desktop;

/// <summary>
/// DWM composition information (<c>DwmGetCompositionTimingInfo</c> with a null hwnd, Windows 8+,
/// present on Windows 10). The benchmark only uses the refresh rate: on this machine's Windows 10 the
/// global frame counters do not advance.
/// </summary>
internal static partial class DwmTiming
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Ratio
    {
        public uint Numerator;
        public uint Denominator;
    }

    // DWM_TIMING_INFO (dwmapi.h), fields up to cFramesDropped; the total size is that of the full structure.
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

    /// <summary>(composed frames, refreshes, refresh rate in Hz) or <c>null</c> if the call fails.</summary>
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
