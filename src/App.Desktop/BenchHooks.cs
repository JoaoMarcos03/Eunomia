using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Eunomia.UI.Shell;

namespace Eunomia.App.Desktop;

/// <summary>Command-line options used by the <c>bench/Scenarios</c> scripts.</summary>
internal sealed record BenchOptions(
    string? Theme,
    double Scale,
    string? StartupProbe,
    string? ReadyFile,
    string? ScrollOutput,
    string? RenderMode,
    double ScrollSeconds,
    bool ExitAfterProbe,
    bool NoMarkers)
{
    /// <summary><c>--connect-probe f</c>: connects the command-line session without dialogs and writes the result (PR-06 and tests of the AOT binary).</summary>
    public string? ConnectProbe { get; init; }

    /// <summary>Some measurement was requested: the app runs with in-memory settings.</summary>
    public bool IsBench => StartupProbe is not null || ReadyFile is not null || ScrollOutput is not null || ConnectProbe is not null;

    public static BenchOptions Parse(string[] args)
    {
        string? Value(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return new BenchOptions(
            Value("--theme"),
            Value("--scale") is { } s ? double.Parse(s, inv) : 1.0,
            Value("--startup-probe"),
            Value("--ready-file"),
            Value("--bench-scroll"),
            Value("--render"),
            Value("--seconds") is { } sec ? double.Parse(sec, inv) : 10,
            args.Contains("--exit"),
            args.Contains("--no-markers"))
        {
            ConnectProbe = Value("--connect-probe"),
        };
    }
}

/// <summary>
/// Measurement points inside the executable (to measure the real NativeAOT binary):
/// <list type="bullet">
/// <item>PR-01: <c>--startup-probe f</c> writes the process creation time and the time the UI thread is free
/// for input after the first rendered frame;</item>
/// <item>PR-16: <c>--ready-file f</c> tells the script the window is ready (the working set is read from outside);</item>
/// <item>PR-04: <c>--bench-scroll f</c> scrolls programmatically and writes the frame times.</item>
/// </list>
/// </summary>
internal static class BenchHooks
{
    public static void Attach(MainWindow window, BenchOptions options)
    {
        if (options.ConnectProbe is { } connectProbe)
        {
            window.Opened += async (_, _) => await ConnectProbeAsync(window, connectProbe, options);
        }
        if (options.StartupProbe is null && options.ReadyFile is null && options.ScrollOutput is null)
        {
            return;
        }
        window.Opened += (_, _) =>
        {
            StartupTrace.Mark("window-opened");
            window.RequestAnimationFrame(_ => OnFirstFrame(window, options));
        };
    }

    /// <summary>Connects the session given on the command line, without dialogs, and writes whether it connected, the server and the time.</summary>
    private static async Task ConnectProbeAsync(MainWindow window, string output, BenchOptions options)
    {
        var clock = Stopwatch.StartNew();
        string? error;
        var request = window.StartupRequest;
        var sessions = window.Context.Sessions;
        if (request is null || sessions is null || Eunomia.App.Sessions.CommandLine.Resolve(request, sessions.Catalog) is not { } session)
        {
            error = "No session on the command line.";
        }
        else
        {
            var credentials = request.Password is not null ? new Eunomia.App.Sessions.LoginCredentials(session.User, request.Password) : null;
            error = await window.ConnectQuietAsync(session, credentials);
        }
        var open = sessions?.Connector.Open.FirstOrDefault();
        WriteJson(output, w =>
        {
            w.WriteBoolean("connected", error is null);
            w.WriteString("error", error);
            w.WriteString("server", open?.Host.Server?.VersionText);
            w.WriteNumber("connectMs", Math.Round(clock.Elapsed.TotalMilliseconds, 1));
            w.WriteBoolean("nativeAot", IsNativeAot);
        });
        if (options.ExitAfterProbe)
        {
            window.Close();
        }
    }

    private static void OnFirstFrame(MainWindow window, BenchOptions options)
    {
        StartupTrace.Mark("first-frame");
        var compositor = ElementComposition.GetElementVisual(window)!.Compositor;
        // Waits for the first frame to reach the screen and for the UI thread to be free for input.
        compositor.RequestCompositionBatchCommitAsync().Rendered.ContinueWith(
            _ => Dispatcher.UIThread.Post(() => OnInteractive(window, options), DispatcherPriority.Input),
            TaskScheduler.Default);
    }

    private static async void OnInteractive(MainWindow window, BenchOptions options)
    {
        var ready = DateTime.UtcNow;
        if (options.StartupProbe is { } probe)
        {
            var start = Process.GetCurrentProcess().StartTime.ToUniversalTime();
            WriteJson(probe, w =>
            {
                w.WriteString("processStartUtc", start.ToString("O"));
                w.WriteString("interactiveUtc", ready.ToString("O"));
                w.WriteNumber("startupMs", (ready - start).TotalMilliseconds);
                w.WriteNumber("processors", Environment.ProcessorCount);
                w.WriteBoolean("nativeAot", IsNativeAot);
                w.WriteStartObject("marksMs");
                foreach (var (name, utc) in StartupTrace.All)
                {
                    w.WriteNumber(name, Math.Round((utc - start).TotalMilliseconds, 1));
                }
                w.WriteNumber("interactive", Math.Round((ready - start).TotalMilliseconds, 1));
                w.WriteEndObject();
            });
        }
        if (options.ReadyFile is { } readyFile)
        {
            File.WriteAllText(readyFile, ready.ToString("O"));
        }
        if (options.ScrollOutput is { } output)
        {
            await ScrollBench.RunAsync(window, output, options);
            window.Close();
            return;
        }
        if (options.ExitAfterProbe)
        {
            window.Close();
        }
    }

    /// <summary>
    /// NativeAOT has no dynamic compilation. (<c>IsDynamicCodeSupported</c> does not help: <c>PublishAot</c>
    /// turns it off in the normal build too.)
    /// </summary>
    internal static bool IsNativeAot => !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeCompiled;

    internal static void WriteJson(string path, Action<Utf8JsonWriter> body)
    {
        using var stream = File.Create(path);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        body(writer);
        writer.WriteEndObject();
    }
}

/// <summary>
/// PR-04: programmatic scroll in the grid with resident data (1M × 60), in three phases: wheel (3 rows/frame),
/// page (25 rows/frame, every row new on each frame) and horizontal (40 px/frame + 1 row/frame).
/// Per frame it measures: the interval between frames (pacing, what the user sees), the cost on the UI thread (from the
/// animation callback to the end of the grid's <c>Render</c>) and the drawing cost on the render thread (between two
/// marker composition visuals, below and above the content).
/// </summary>
internal static class ScrollBench
{
    private sealed record Phase(string Name, double Fraction, int RowsPerFrame, double PixelsPerFrame);

    private static readonly Phase[] Phases =
    [
        new("wheel", 0.4, 3, 0),
        new("page", 0.3, 25, 0),
        new("horizontal", 0.3, 1, 40),
    ];

    public static async Task RunAsync(MainWindow window, string output, BenchOptions options)
    {
        var seconds = options.ScrollSeconds;
        var scale = options.Scale;
        window.ShowDataTab();
        var grid = window.DataGrid;
        grid.Focus();
        var source = grid.Source!;

        // Resident data: loads up front every row the scroll will go through (with a margin).
        var rowsNeeded = (int)(Phases.Sum(p => p.Fraction * seconds * 60 * 1.5 * p.RowsPerFrame) + (grid.ViewportRows * 6));
        await source.RequestRangeAsync(0, rowsNeeded);

        var compositor = ElementComposition.GetElementVisual(window)!.Compositor;
        var startMarker = new MarkerHandler();
        var endMarker = new MarkerHandler();
        if (!options.NoMarkers)
        {
            AttachMarker(compositor, window.RenderStartMarker, startMarker);
            AttachMarker(compositor, window.RenderEndMarker, endMarker);
        }

        // Control: 2 s of frames without scrolling (the platform's pacing without grid work).
        var idle = new List<FrameSample>(200);
        await Drive(window, grid, 2, [new Phase("idle", 1, 0, 0)], idle);

        // Warm-up (0.5 s) outside the measured window, then back to the top.
        await Drive(window, grid, 0.5, [new Phase("warmup", 1, 3, 0)], null);
        grid.TopRow = 0;
        grid.ScrollX = 0;
        await Task.Delay(200);
        startMarker.Clear();
        endMarker.Clear();

        var frames = new List<FrameSample>(1200);
        var layoutsBefore = grid.TextLayoutsCreated;
        var shapedBefore = grid.TextLayoutsShaped;
        var gcBefore = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        var allocatedBefore = GC.GetTotalAllocatedBytes();
        var measureStart = Stopwatch.GetTimestamp();
        await Drive(window, grid, seconds, Phases, frames);
        var dwmAfter = DwmTiming.Sample();
        var measured = Stopwatch.GetElapsedTime(measureStart).TotalSeconds;
        var layoutsCreated = grid.TextLayoutsCreated - layoutsBefore;
        var shaped = grid.TextLayoutsShaped - shapedBefore;
        var gc = (GC.CollectionCount(0) - gcBefore.Item1, GC.CollectionCount(1) - gcBefore.Item2, GC.CollectionCount(2) - gcBefore.Item3);
        var allocatedMB = (GC.GetTotalAllocatedBytes() - allocatedBefore) / 1024.0 / 1024.0;
        await Task.Delay(300);

        var renderCosts = startMarker.Pair(endMarker);
        BenchHooks.WriteJson(output, w =>
        {
            w.WriteNumber("seconds", seconds);
            w.WriteNumber("scale", scale);
            w.WriteNumber("renderScaling", window.RenderScaling);
            w.WriteNumber("processors", Environment.ProcessorCount);
            w.WriteBoolean("nativeAot", BenchHooks.IsNativeAot);
            w.WriteNumber("viewportRows", grid.ViewportRows);
            w.WriteNumber("visibleWidth", grid.DataViewportWidth);
            w.WriteNumber("frames", frames.Count);
            w.WriteNumber("textLayoutsCreated", layoutsCreated);
            w.WriteNumber("textLayoutsShaped", shaped);
            w.WriteBoolean("markers", !options.NoMarkers);
            w.WriteString("renderMode", options.RenderMode ?? "default");
            w.WriteNumber("gc0", gc.Item1);
            w.WriteNumber("gc1", gc.Item2);
            w.WriteNumber("gc2", gc.Item3);
            w.WriteNumber("allocatedMB", Math.Round(allocatedMB, 1));
            WriteStats(w, "idleIntervalMs", idle.Skip(1).Select(f => f.IntervalMs));
            w.WriteString("adapter", Program.GraphicsInfo.Selected ?? "n/a");
            // The DWM frame counters (null hwnd) do not advance on Windows 10; only the refresh rate is useful.
            if (dwmAfter is { } dwm)
            {
                w.WriteNumber("refreshHz", Math.Round(dwm.RefreshHz, 2));
            }
            w.WriteNumber("appFramesPerSecond", Math.Round(frames.Count / measured, 1));
            w.WriteStartArray("adapters");
            foreach (var name in Program.GraphicsInfo.Adapters)
            {
                w.WriteStringValue(name);
            }
            w.WriteEndArray();
            w.WriteStartArray("intervalsMs");
            foreach (var f in frames.Skip(1))
            {
                w.WriteNumberValue(Math.Round(f.IntervalMs, 2));
            }
            w.WriteEndArray();
            w.WriteStartArray("idleIntervalsMs");
            foreach (var f in idle.Skip(1))
            {
                w.WriteNumberValue(Math.Round(f.IntervalMs, 2));
            }
            w.WriteEndArray();
            WriteStats(w, "intervalMs", frames.Skip(1).Select(f => f.IntervalMs));
            WriteStats(w, "uiMs", frames.Select(f => f.UiMs));
            WriteStats(w, "gridRenderMs", frames.Select(f => f.GridRenderMs));
            WriteStats(w, "renderThreadMs", renderCosts);
            w.WriteNumber("droppedFrames", frames.Skip(1).Count(f => f.IntervalMs > 1.5 * 1000.0 / 60));
            w.WriteStartObject("phases");
            foreach (var phase in Phases)
            {
                var subset = frames.Where(f => f.Phase == phase.Name).ToList();
                w.WriteStartObject(phase.Name);
                WriteStats(w, "intervalMs", subset.Skip(1).Select(f => f.IntervalMs));
                WriteStats(w, "uiMs", subset.Select(f => f.UiMs));
                w.WriteEndObject();
            }
            w.WriteEndObject();
        });
    }

    private static Task Drive(MainWindow window, UI.Controls.Grid.DataGridControl grid, double seconds, Phase[] phases, List<FrameSample>? frames)
    {
        var done = new TaskCompletionSource();
        var started = Stopwatch.GetTimestamp();
        long previous = 0;
        FrameSample? pending = null;
        var direction = 1;

        void Frame(TimeSpan _)
        {
            var now = Stopwatch.GetTimestamp();
            if (pending is not null && grid.LastRenderEndTimestamp > pending.Start)
            {
                pending.UiMs = Stopwatch.GetElapsedTime(pending.Start, grid.LastRenderEndTimestamp).TotalMilliseconds;
                pending.GridRenderMs = grid.LastRenderTime.TotalMilliseconds;
            }
            var elapsed = Stopwatch.GetElapsedTime(started, now).TotalSeconds;
            if (elapsed >= seconds)
            {
                done.TrySetResult();
                return;
            }
            var phase = PhaseAt(phases, elapsed / seconds);
            if (phase.RowsPerFrame == 0 && phase.PixelsPerFrame == 0)
            {
                // Control phase: redraws the grid without changing the scroll (all text already cached).
                grid.InvalidateVisual();
            }
            else if (phase.PixelsPerFrame > 0)
            {
                var x = grid.ScrollX + (direction * phase.PixelsPerFrame);
                if (x >= grid.MaxScrollX || x <= 0)
                {
                    direction = -direction;
                }
                grid.ScrollX = x;
            }
            grid.TopRow += phase.RowsPerFrame;
            var sample = new FrameSample(phase.Name, now)
            {
                IntervalMs = previous == 0 ? 0 : Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds,
            };
            frames?.Add(sample);
            pending = sample;
            previous = now;
            window.RequestAnimationFrame(Frame);
        }

        window.RequestAnimationFrame(Frame);
        return done.Task;
    }

    private static Phase PhaseAt(Phase[] phases, double progress)
    {
        double acc = 0;
        foreach (var p in phases)
        {
            acc += p.Fraction;
            if (progress < acc)
            {
                return p;
            }
        }
        return phases[^1];
    }

    private static void AttachMarker(Compositor compositor, Visual host, MarkerHandler handler)
    {
        var visual = compositor.CreateCustomVisual(handler);
        visual.Size = new Vector(1, 1);
        ElementComposition.SetElementChildVisual(host, visual);
        visual.SendHandlerMessage("start");
    }

    private static void WriteStats(Utf8JsonWriter w, string name, IEnumerable<double> values)
    {
        var sorted = values.Where(v => v > 0).Order().ToArray();
        w.WriteStartObject(name);
        w.WriteNumber("count", sorted.Length);
        if (sorted.Length > 0)
        {
            w.WriteNumber("p50", Percentile(sorted, 0.50));
            w.WriteNumber("p95", Percentile(sorted, 0.95));
            w.WriteNumber("p99", Percentile(sorted, 0.99));
            w.WriteNumber("max", sorted[^1]);
            w.WriteNumber("mean", sorted.Average());
        }
        w.WriteEndObject();
    }

    private static double Percentile(double[] sorted, double p)
    {
        var rank = p * (sorted.Length - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        return Math.Round(sorted[lo] + ((sorted[hi] - sorted[lo]) * (rank - lo)), 3);
    }

    private sealed class FrameSample(string phase, long start)
    {
        public string Phase { get; } = phase;

        public long Start { get; } = start;

        public double IntervalMs { get; set; }

        public double UiMs { get; set; }

        public double GridRenderMs { get; set; }
    }

    /// <summary>Empty composition visual that records on the render thread the time it is drawn in each frame.</summary>
    private sealed class MarkerHandler : CompositionCustomVisualHandler
    {
        private readonly ConcurrentQueue<long> _stamps = new();

        public override void OnMessage(object message) => RegisterForNextAnimationFrameUpdate();

        public override void OnAnimationFrameUpdate()
        {
            Invalidate();
            RegisterForNextAnimationFrameUpdate();
        }

        public override void OnRender(ImmediateDrawingContext drawingContext) => _stamps.Enqueue(Stopwatch.GetTimestamp());

        public void Clear() => _stamps.Clear();

        /// <summary>Cost per frame: top marker minus bottom marker, paired in order.</summary>
        public double[] Pair(MarkerHandler end)
        {
            var a = _stamps.ToArray();
            var b = end._stamps.ToArray();
            var result = new List<double>();
            var j = 0;
            for (var i = 0; i < a.Length; i++)
            {
                while (j < b.Length && b[j] < a[i])
                {
                    j++;
                }
                if (j < b.Length && (i + 1 >= a.Length || b[j] < a[i + 1]))
                {
                    result.Add(Stopwatch.GetElapsedTime(a[i], b[j]).TotalMilliseconds);
                }
            }
            return [.. result];
        }
    }
}
