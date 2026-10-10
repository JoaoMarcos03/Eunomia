using Avalonia.Controls;
using Avalonia.Threading;
using Eunomia.App.Diagnostics;
using Eunomia.Infra.Observability;
using Eunomia.UI.Controls.Common.Dialogs;

namespace Eunomia.App.Desktop;

/// <summary>
/// Unhandled exceptions (phase 12; section 21.5): on the UI thread they go to the diagnostic log, show a dialog (at
/// most one every 10 s) and the application carries on; in an unobserved task they only go to the log; in the domain (the
/// process is about to end) they go to a crash report and the log is flushed. Query tabs come back on the next start via the journal.
/// </summary>
internal static class CrashHandling
{
    private static DateTime _lastDialog = DateTime.MinValue;

    public static void Install(JsonLinesDiagnosticLog log, string directory, string version, Func<Window?> owner)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception)
            {
                log.Write(DiagnosticLevel.Error, "crash", "Unhandled exception; the application is closing", exception);
                try
                {
                    CrashReports.Write(directory, exception, version);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
                log.Flush(TimeSpan.FromSeconds(1));
            }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.Write(DiagnosticLevel.Warning, "task", "Unobserved task exception", e.Exception);
            e.SetObserved();
        };
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            log.Write(DiagnosticLevel.Error, "ui", "Unhandled exception on the UI thread", e.Exception);
            e.Handled = true;
            if (DateTime.UtcNow - _lastDialog < TimeSpan.FromSeconds(10) || owner() is not { } window)
            {
                return;
            }
            _lastDialog = DateTime.UtcNow;
            _ = MessageDialog.ShowAsync(window, "Unexpected error",
                $"{e.Exception.GetType().Name}: {e.Exception.Message}\n\nEunomia will try to continue. The details are in {log.CurrentPath}.",
                MessageDialogButtons.Ok);
        };
    }
}
