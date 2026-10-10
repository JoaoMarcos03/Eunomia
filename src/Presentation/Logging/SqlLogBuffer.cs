using System.Globalization;
using Eunomia.App.Settings;

namespace Eunomia.Presentation.Logging;

/// <summary>SQL log categories (Errors / User SQL / Internal SQL / Script / Info / Debug preferences).</summary>
public enum LogCategory
{
    Error,
    UserSql,
    InternalSql,
    Script,
    Info,
    Debug,
}

/// <summary>Line kind, which gives the background colour (appendix C §1.10).</summary>
public enum LogLineKind
{
    Sql,
    Error,
    Warning,
    Note,
    Info,
    Comment,
}

/// <summary>SQL log line with the running number shown in the gutter.</summary>
public sealed record LogLine(long Number, string Text, LogLineKind Kind);

/// <summary>Log change: new lines at the end and/or lines cut at the start (or everything cleared).</summary>
public sealed record LogChange(IReadOnlyList<LogLine> Appended, int TrimmedFromStart, bool Cleared);

/// <summary>
/// SQL log content with the rules of HeidiSQL's <c>LogSQL</c> (appendix C §1.10, FAM-Q04): filter by category,
/// optional <c>[hh:nn:ss.zzz]</c> prefix, lines cut at <c>logsqlwidth</c> with <c>...</c>, delimiter
/// appended to SQL, non-SQL messages as <c>/* … */</c>, only the last <c>logsqlnum</c> lines with running
/// numbering. Thread-safe: the engine writes from any thread; the UI applies <see cref="Changed"/> on the UI thread.
/// </summary>
public sealed class SqlLogBuffer(LogOptions options, TimeProvider? clock = null)
{
    private readonly Lock _gate = new();
    private readonly LinkedList<LogLine> _lines = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private long _nextNumber = 1;

    public LogOptions Options { get; } = options;

    public event EventHandler<LogChange>? Changed;

    public long FirstLineNumber
    {
        get
        {
            lock (_gate)
            {
                return _lines.First?.Value.Number ?? _nextNumber;
            }
        }
    }

    public IReadOnlyList<LogLine> Snapshot()
    {
        lock (_gate)
        {
            return [.. _lines];
        }
    }

    /// <summary>
    /// Appends a message. Returns the line written, or <c>null</c> if the category is turned off in the
    /// preferences.
    /// </summary>
    public LogLine? Append(LogCategory category, string message, string delimiter = ";")
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!IsEnabled(category))
        {
            return null;
        }
        var (text, kind) = Format(category, message, delimiter);
        LogLine line;
        var trimmed = 0;
        lock (_gate)
        {
            line = new LogLine(_nextNumber++, text, kind);
            _lines.AddLast(line);
            var max = Math.Max(1, Options.MaxLines);
            while (_lines.Count > max)
            {
                _lines.RemoveFirst();
                trimmed++;
            }
        }
        Changed?.Invoke(this, new LogChange([line], trimmed, Cleared: false));
        return line;
    }

    /// <summary>Clear (Ctrl+Q). The numbering continues.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _lines.Clear();
        }
        Changed?.Invoke(this, new LogChange([], 0, Cleared: true));
    }

    /// <summary>Text of the whole log (Save as textfile).</summary>
    public string ToText()
    {
        lock (_gate)
        {
            return string.Join(Environment.NewLine, _lines.Select(l => l.Text));
        }
    }

    private bool IsEnabled(LogCategory category) => category switch
    {
        LogCategory.Error => Options.Errors,
        LogCategory.UserSql => Options.UserSql,
        LogCategory.InternalSql => Options.InternalSql,
        LogCategory.Script => Options.Script,
        LogCategory.Info => Options.Info,
        LogCategory.Debug => Options.Debug,
        _ => true,
    };

    private (string Text, LogLineKind Kind) Format(LogCategory category, string message, string delimiter)
    {
        // The log memo has one line per entry: line breaks become spaces.
        var text = message.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ').Trim();
        LogLineKind kind;
        if (category is LogCategory.UserSql or LogCategory.InternalSql or LogCategory.Script)
        {
            kind = LogLineKind.Sql;
            // Passwords in CREATE USER / SET PASSWORD stay out of the log (section 23.2).
            text = Eunomia.Engine.Sql.SecretMasking.MaskSql(text);
            if (delimiter.Length > 0 && !text.EndsWith(delimiter, StringComparison.Ordinal))
            {
                text += delimiter;
            }
        }
        else
        {
            kind = category == LogCategory.Error ? LogLineKind.Error
                : text.StartsWith("Warning:", StringComparison.OrdinalIgnoreCase) ? LogLineKind.Warning
                : text.StartsWith("Note:", StringComparison.OrdinalIgnoreCase) ? LogLineKind.Note
                : text.StartsWith("Info:", StringComparison.OrdinalIgnoreCase) ? LogLineKind.Info
                : LogLineKind.Comment;
            text = "/* " + text + " */";
        }
        if (Options.MaxLineLength > 0 && text.Length > Options.MaxLineLength)
        {
            text = text[..Options.MaxLineLength] + "...";
        }
        if (Options.Timestamps)
        {
            text = "[" + _clock.GetLocalNow().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "] " + text;
        }
        return (text, kind);
    }
}

/// <summary>Session (engine) messages for the SQL log, with HeidiSQL's categories.</summary>
public sealed class SessionLogAdapter(SqlLogBuffer log) : Eunomia.Engine.Execution.ISessionLog
{
    public void Write(string session, Eunomia.Engine.Execution.SessionLogCategory category, string message) =>
        log.Append(category switch
        {
            Eunomia.Engine.Execution.SessionLogCategory.Error => LogCategory.Error,
            Eunomia.Engine.Execution.SessionLogCategory.UserSql => LogCategory.UserSql,
            Eunomia.Engine.Execution.SessionLogCategory.InternalSql => LogCategory.InternalSql,
            Eunomia.Engine.Execution.SessionLogCategory.Script => LogCategory.Script,
            Eunomia.Engine.Execution.SessionLogCategory.Debug => LogCategory.Debug,
            _ => LogCategory.Info,
        }, message);
}
