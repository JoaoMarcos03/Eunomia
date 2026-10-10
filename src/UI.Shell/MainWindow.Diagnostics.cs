namespace Eunomia.UI.Shell;

/// <summary>Diagnostics (phase 12; section 21.5): the UI thread stall monitor, on when there is a diagnostic log.</summary>
public sealed partial class MainWindow
{
    private readonly UiStallMonitor? _stalls;

    /// <summary>UI thread stall monitor (only with a diagnostic log).</summary>
    internal UiStallMonitor? Stalls => _stalls;

    /// <summary>The global results budget with the current Preferences value.</summary>
    private Eunomia.Engine.Results.ResultMemoryPool GlobalResultPool()
    {
        _context.ResultPool.BudgetBytes = (long)Math.Max(64, _context.Settings.Data.GlobalResultMemoryMb) * 1024 * 1024;
        return _context.ResultPool;
    }
}
