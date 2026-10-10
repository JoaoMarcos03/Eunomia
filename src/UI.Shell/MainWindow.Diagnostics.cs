namespace Eunomia.UI.Shell;

/// <summary>Diagnóstico (Fase 12; secção 21.5): o monitor de stalls da UI thread, ligado quando há log de diagnóstico.</summary>
public sealed partial class MainWindow
{
    private readonly UiStallMonitor? _stalls;

    /// <summary>Monitor de stalls da UI thread (só com log de diagnóstico).</summary>
    internal UiStallMonitor? Stalls => _stalls;

    /// <summary>O orçamento global dos resultados com o valor atual das Preferences.</summary>
    private Eunomia.Engine.Results.ResultMemoryPool GlobalResultPool()
    {
        _context.ResultPool.BudgetBytes = (long)Math.Max(64, _context.Settings.Data.GlobalResultMemoryMb) * 1024 * 1024;
        return _context.ResultPool;
    }
}
