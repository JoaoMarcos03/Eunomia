namespace Eunomia.App.Diagnostics;

public enum DiagnosticLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>
/// Porta do log de diagnóstico (secção 21.5): em ficheiro, nunca com valores de dados nem segredos (quem implementa
/// redige o texto). Thread-safe; nunca lança.
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
