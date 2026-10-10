using System.Diagnostics;
using Avalonia.Threading;
using Eunomia.App.Diagnostics;

namespace Eunomia.UI.Shell;

/// <summary>
/// Stalls da UI thread (secção 21.5; critério 27.3: 0 stalls &gt; 250 ms): um timer de 100 ms na UI thread mede o atraso
/// com que corre; um atraso acima de <see cref="Threshold"/> é um stall, contado e registado no log de diagnóstico.
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
