using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Eunomia.Infra.Observability;

/// <summary>
/// Local crash reports (section 21.5): one JSON per crash with the version, the system, the process's uptime and
/// memory and the exception's type and stack, without messages (they may carry SQL, values, host names or users). Nothing
/// leaves the machine. On the next start, <see cref="TakeUnseen"/> reports that there was a crash (once).
/// </summary>
public static class CrashReports
{
    private const string Prefix = "crash-";
    private const string Seen = ".seen";

    public static string Write(string directory, Exception exception, string version)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(exception);
        Directory.CreateDirectory(directory);
        var now = DateTimeOffset.Now;
        var path = Path.Combine(directory, Prefix + now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".json");
        using var process = Process.GetCurrentProcess();
        using var stream = File.Create(path);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("time", now.ToString("O", CultureInfo.InvariantCulture));
        writer.WriteString("version", version);
        writer.WriteString("os", Environment.OSVersion.VersionString);
        writer.WriteString("runtime", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
        writer.WriteNumber("uptimeSeconds", Math.Round((DateTime.Now - process.StartTime).TotalSeconds));
        writer.WriteNumber("workingSetMb", process.WorkingSet64 / (1024 * 1024));
        writer.WriteNumber("managedMb", GC.GetTotalMemory(false) / (1024 * 1024));
        writer.WriteString("exception", exception.GetType().FullName);
        writer.WriteNumber("hresult", exception.HResult);
        writer.WriteString("stack", JsonLinesDiagnosticLog.StackOf(exception));
        writer.WriteEndObject();
        return path;
    }

    /// <summary>The most recent crash report not seen yet, marked as seen; <c>null</c> if there is none.</summary>
    public static string? TakeUnseen(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        if (!Directory.Exists(directory))
        {
            return null;
        }
        var reports = Directory.EnumerateFiles(directory, Prefix + "*.json").Where(f => !File.Exists(f + Seen)).Order(StringComparer.Ordinal).ToList();
        foreach (var report in reports)
        {
            try
            {
                File.WriteAllText(report + Seen, string.Empty);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return reports.Count == 0 ? null : reports[^1];
    }
}
