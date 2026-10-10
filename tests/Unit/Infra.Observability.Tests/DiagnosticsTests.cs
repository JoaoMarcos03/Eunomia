using System.Text.Json;
using Eunomia.App.Diagnostics;

namespace Eunomia.Infra.Observability.Tests;

/// <summary>Diagnostic log and crash reports (phase 12; section 21.5): JSON lines, redaction, rotation, no messages in the crash.</summary>
public sealed class DiagnosticsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "eunomia-diag-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void JsonLinesWithRedactionAndMinimumLevel()
    {
        using (var log = new JsonLinesDiagnosticLog(_directory, DiagnosticLevel.Info))
        {
            log.Write(DiagnosticLevel.Debug, "x", "not shown");
            log.Write(DiagnosticLevel.Info, "session", "Connecting with Server=h;User ID=u;Password=Segredo1;Database=d");
            log.Write(DiagnosticLevel.Error, "query", "Failed: CREATE USER 'a'@'%' IDENTIFIED BY 'Segredo2'", new InvalidOperationException("pwd=Segredo3 rejected"));
            Assert.True(log.Flush(TimeSpan.FromSeconds(10)));
        }
        var lines = File.ReadAllLines(Path.Combine(_directory, JsonLinesDiagnosticLog.FileName));

        Assert.Equal(2, lines.Length);
        var text = string.Join("\n", lines);
        Assert.DoesNotContain("Segredo", text, StringComparison.Ordinal);
        using var first = JsonDocument.Parse(lines[0]);
        Assert.Equal("Info", first.RootElement.GetProperty("lvl").GetString());
        Assert.Equal("session", first.RootElement.GetProperty("cat").GetString());
        Assert.Contains("Password=***", first.RootElement.GetProperty("msg").GetString(), StringComparison.Ordinal);
        using var second = JsonDocument.Parse(lines[1]);
        var exc = second.RootElement.GetProperty("exc");
        Assert.Equal("System.InvalidOperationException", exc.GetProperty("type").GetString());
        Assert.Equal("pwd=*** rejected", exc.GetProperty("msg").GetString());
        Assert.Contains("IDENTIFIED BY '***'", second.RootElement.GetProperty("msg").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RotatesBySize()
    {
        using (var log = new JsonLinesDiagnosticLog(_directory, DiagnosticLevel.Info, maxBytes: 2_000, maxFiles: 3))
        {
            for (var i = 0; i < 200; i++)
            {
                log.Write(DiagnosticLevel.Info, "x", "line " + new string('x', 50) + " " + i);
            }
            Assert.True(log.Flush(TimeSpan.FromSeconds(10)));
        }
        var files = Directory.GetFiles(_directory, "diagnostic*.jsonl").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(["diagnostic.1.jsonl", "diagnostic.2.jsonl", "diagnostic.jsonl"], files);
        Assert.All(files, f => Assert.True(new FileInfo(Path.Combine(_directory, f!)).Length < 2_200));
        Assert.Contains("line", File.ReadAllText(Path.Combine(_directory, "diagnostic.jsonl")), StringComparison.Ordinal);
        Assert.EndsWith(" 199\"}", File.ReadAllLines(Path.Combine(_directory, "diagnostic.jsonl"))[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void CrashReportWithoutMessageAndSeenOnce()
    {
        Exception exception;
        try
        {
            throw new InvalidOperationException("SELECT * FROM customers WHERE tax_id = '123456789'", new IOException("host db.internal"));
        }
        catch (InvalidOperationException e)
        {
            exception = e;
        }
        var path = CrashReports.Write(_directory, exception, "0.9.0");
        var json = File.ReadAllText(path);

        using var document = JsonDocument.Parse(json);
        Assert.Equal("System.InvalidOperationException", document.RootElement.GetProperty("exception").GetString());
        Assert.Equal("0.9.0", document.RootElement.GetProperty("version").GetString());
        Assert.Contains("System.IO.IOException", document.RootElement.GetProperty("stack").GetString(), StringComparison.Ordinal);
        Assert.Contains(nameof(CrashReportWithoutMessageAndSeenOnce), document.RootElement.GetProperty("stack").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("123456789", json, StringComparison.Ordinal);
        Assert.DoesNotContain("db.interno", json, StringComparison.Ordinal);

        Assert.Equal(path, CrashReports.TakeUnseen(_directory));
        Assert.Null(CrashReports.TakeUnseen(_directory));
    }
}
