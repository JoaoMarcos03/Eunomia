using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Eunomia.App.Diagnostics;
using Eunomia.UI.Shell;

namespace Eunomia.UI.Headless.Tests;

/// <summary>UI thread stalls &gt; 250 ms (phase 12; section 21.5; criterion 27.3).</summary>
public sealed class UiStallMonitorTests
{
    private sealed class Capture : IDiagnosticLog
    {
        public List<string> Lines { get; } = [];

        public void Write(DiagnosticLevel level, string category, string message, Exception? exception = null) => Lines.Add($"{level} {category} {message}");
    }

    private static async Task PumpAsync(TimeSpan duration)
    {
        var until = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task A400MsBlockCountsAsAStall()
    {
        var log = new Capture();
        using var monitor = new UiStallMonitor(log);
        await PumpAsync(TimeSpan.FromMilliseconds(350));
        Assert.Equal(0, monitor.Stalls);

        // Synchronous work on the UI thread: the next tick arrives late.
        Thread.Sleep(450);
        await PumpAsync(TimeSpan.FromMilliseconds(250));

        Assert.Equal(1, monitor.Stalls);
        Assert.True(monitor.Longest > UiStallMonitor.Threshold);
        Assert.Contains(log.Lines, l => l.StartsWith("Warning ui UI thread stalled for ", StringComparison.Ordinal));
    }
}
