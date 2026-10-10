namespace Eunomia.App.Diagnostics;

public enum DiagnosticLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>
/// Diagnostic log port (section 21.5): to a file, never with data values or secrets (the implementation
/// redacts the text). Thread-safe; never throws.
/// </summary>
public interface IDiagnosticLog
{
    void Write(DiagnosticLevel level, string category, string message, Exception? exception = null);
}

public sealed class NullDiagnosticLog : IDiagnosticLog
{
    public static NullDiagnosticLog Instance { get; } = new();

    public void Write(DiagnosticLevel level, string category, string message, Exception? exception = null)
    {
    }
}
