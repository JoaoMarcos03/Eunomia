using Eunomia.App.Settings;
using Eunomia.Presentation.Logging;

namespace Eunomia.Presentation.Tests;

public sealed class SqlLogBufferFix2Tests
{
    [Fact]
    public void ErrorQuotingTheSqlIsMasked()
    {
        var log = new SqlLogBuffer(new LogOptions());

        var line = log.Append(LogCategory.Error, "SQL Error (1064): near 'IDENTIFIED BY 'S3gr3do' WITH x' at line 1")!;

        Assert.DoesNotContain("S3gr3do", line.Text, StringComparison.Ordinal);
    }
}
