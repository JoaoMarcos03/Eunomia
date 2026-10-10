using Eunomia.App.Settings;
using Eunomia.Presentation.Logging;
using Eunomia.Presentation.Shell;

namespace Eunomia.Presentation.Tests;

public sealed class SqlLogBufferTests
{
    [Fact]
    public void SqlGetsTheDelimiterAndMessagesGoInComments()
    {
        var log = new SqlLogBuffer(new LogOptions { Script = true });

        log.Append(LogCategory.UserSql, "SELECT 1");
        log.Append(LogCategory.InternalSql, "SHOW DATABASES;");
        log.Append(LogCategory.Info, "Affected rows: 0  Found rows: 1  Warnings: 0  Duration for 1 query: 0,001 sec.");
        log.Append(LogCategory.Script, "CREATE PROCEDURE p() BEGIN END", "$$");

        Assert.Equal(
            ["SELECT 1;", "SHOW DATABASES;", "/* Affected rows: 0  Found rows: 1  Warnings: 0  Duration for 1 query: 0,001 sec. */", "CREATE PROCEDURE p() BEGIN END$$"],
            log.Snapshot().Select(l => l.Text));
    }

    [Theory]
    [InlineData(LogCategory.Error, "SQL Error (1064): You have an error", LogLineKind.Error)]
    [InlineData(LogCategory.Info, "Warning: Data truncated for column 'x'", LogLineKind.Warning)]
    [InlineData(LogCategory.Info, "Note: Table 'x' already exists", LogLineKind.Note)]
    [InlineData(LogCategory.Info, "Info: Records: 3", LogLineKind.Info)]
    [InlineData(LogCategory.Info, "Connecting to localhost ...", LogLineKind.Comment)]
    [InlineData(LogCategory.UserSql, "SELECT 1", LogLineKind.Sql)]
    public void LineKindGivesTheColour(LogCategory category, string message, LogLineKind kind)
    {
        var log = new SqlLogBuffer(new LogOptions());

        Assert.Equal(kind, log.Append(category, message)!.Kind);
    }

    [Fact]
    public void KeepsOnlyTheLastLinesWithRunningNumbers()
    {
        var log = new SqlLogBuffer(new LogOptions { MaxLines = 300 });
        LogChange? last = null;
        log.Changed += (_, c) => last = c;

        for (var i = 1; i <= 1234; i++)
        {
            log.Append(LogCategory.UserSql, $"SELECT {i}");
        }

        var lines = log.Snapshot();
        Assert.Equal(300, lines.Count);
        Assert.Equal(935, log.FirstLineNumber);
        Assert.Equal("SELECT 935;", lines[0].Text);
        Assert.Equal(1234, lines[^1].Number);
        Assert.Equal(1, last!.TrimmedFromStart);
    }

    [Fact]
    public void CutsLongLinesWithAnEllipsis()
    {
        var log = new SqlLogBuffer(new LogOptions { MaxLineLength = 20 });

        var line = log.Append(LogCategory.UserSql, "SELECT 'abcdefghijklmnopqrstuvwxyz'");

        Assert.Equal("SELECT 'abcdefghijkl...", line!.Text);
    }

    [Fact]
    public void ZeroDoesNotCut()
    {
        var log = new SqlLogBuffer(new LogOptions { MaxLineLength = 0 });
        var sql = "SELECT '" + new string('x', 5000) + "'";

        Assert.Equal(sql + ";", log.Append(LogCategory.UserSql, sql)!.Text);
    }

    [Fact]
    public void DisabledCategoriesAreLeftOut()
    {
        var log = new SqlLogBuffer(new LogOptions { InternalSql = false });

        Assert.Null(log.Append(LogCategory.InternalSql, "SHOW TABLES"));
        Assert.Null(log.Append(LogCategory.Debug, "x"));
        Assert.Null(log.Append(LogCategory.Script, "x"));
        Assert.Empty(log.Snapshot());
    }

    [Fact]
    public void LineBreaksBecomeSpacesAndOptionalTimestamp()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 5, 14, 3, 9, 45, TimeSpan.Zero));
        var log = new SqlLogBuffer(new LogOptions { Timestamps = true }, clock);

        var line = log.Append(LogCategory.UserSql, "SELECT *\r\nFROM t\nWHERE 1");

        Assert.Equal("[14:03:09.045] SELECT * FROM t WHERE 1;", line!.Text);
    }

    [Fact]
    public void ClearKeepsTheNumbering()
    {
        var log = new SqlLogBuffer(new LogOptions());
        log.Append(LogCategory.UserSql, "SELECT 1");
        log.Append(LogCategory.UserSql, "SELECT 2");

        log.Clear();
        var line = log.Append(LogCategory.UserSql, "SELECT 3");

        Assert.Equal(3, line!.Number);
        Assert.Equal(3, log.FirstLineNumber);
    }

    [Theory]
    [InlineData(0, "00:00:00 h")]
    [InlineData(312, "00:05:12 h")]
    [InlineData(86_400 + 600, "1 day, 00:10 h")]
    [InlineData(4 * 86_400 + 600, "4 days, 00:10 h")]
    [InlineData(10 * 86_400 + 6 * 3600 + 52 * 60 + 30, "10 days, 06:52 h")]
    public void DurationsAsInHeidiSql(int seconds, string expected)
    {
        Assert.Equal(expected, DurationFormatter.Format(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void StatusBarIdleAndBusy()
    {
        var status = new StatusBarModel();
        Assert.False(status.IsBusy);

        status.Busy = "Executing query #1 of 3 ...";
        status.Progress = 0.3;
        Assert.True(status.IsBusy);

        status.SetIdle();
        Assert.False(status.IsBusy);
        Assert.Null(status.Progress);
        Assert.Equal("Idle.", status.Busy);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
