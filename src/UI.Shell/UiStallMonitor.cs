using System.Diagnostics;
using Avalonia.Threading;
using Eunomia.App.Diagnostics;

namespace Eunomia.UI.Shell;

/// <summary>
/// UI thread stalls (section 21.5; criterion 27.3: 0 stalls &gt; 250 ms): a 100 ms timer on the UI thread measures how late
/// it runs; a delay above <see cref="Threshold"/> is a stall, counted and recorded in the diagnostic log.
/// </summary>
public sealed class UiStallMonitor : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    public static readonly TimeSpan Threshold = TimeSpan.FromMilliseconds(250);

    private readonly IDiagnosticLog _log;
    private readonly DispatcherTimer _timer;
    private long _last;

    public UiStallMonitor(IDiagnosticLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
        _timer = new DispatcherTimer(Interval, DispatcherPriority.Input, OnTick);
        _last = Stopwatch.GetTimestamp();
        _timer.Start();
    }

    public int Stalls { get; private set; }

    public TimeSpan Longest { get; private set; }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var late = Stopwatch.GetElapsedTime(_last, now) - Interval;
        _last = now;
        if (late <= Threshold)
        {
            return;
        }
        Stalls++;
        if (late > Longest)
        {
            Longest = late;
        }
        _log.Write(DiagnosticLevel.Warning, "ui", $"UI thread stalled for {late.TotalMilliseconds:0} ms");
    }

    public void Dispose() => _timer.Stop();
}
