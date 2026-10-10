using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Eunomia.App.Diagnostics;
using Eunomia.Engine.Sql;

namespace Eunomia.Infra.Observability;

/// <summary>
/// Diagnostic log in a file (section 21.5): one JSON line per entry (<c>t</c>, <c>lvl</c>, <c>cat</c>,
/// <c>msg</c> and, with an exception, <c>exc</c> with type, message and stack), written by a background task so the
/// writer never blocks; rotation by size (<c>diagnostic.jsonl</c>, <c>diagnostic.1.jsonl</c>…). The text is redacted:
/// passwords in SQL and <c>password=…</c> / <c>pwd=…</c> pairs from connection strings become <c>***</c>.
/// </summary>
public sealed partial class JsonLinesDiagnosticLog : IDiagnosticLog, IDisposable
{
    public const string FileName = "diagnostic.jsonl";

    private readonly string _directory;
    private readonly long _maxBytes;
    private readonly int _maxFiles;
    private readonly DiagnosticLevel _minimum;
    // Queue full: the new line is lost (writers never wait).
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly Task _writer;
    private long _pending;
    private long _size = -1;

    public JsonLinesDiagnosticLog(string directory, DiagnosticLevel minimum = DiagnosticLevel.Info, long maxBytes = 5 * 1024 * 1024, int maxFiles = 5)
    {
        ArgumentNullException.ThrowIfNull(directory);
        _directory = directory;
        _minimum = minimum;
        _maxBytes = maxBytes;
        _maxFiles = Math.Max(1, maxFiles);
        Directory.CreateDirectory(directory);
        _writer = Task.Run(WriteLoopAsync);
    }

    public string CurrentPath => Path.Combine(_directory, FileName);

    public void Write(DiagnosticLevel level, string category, string message, Exception? exception = null)
    {
        if (level < _minimum)
        {
            return;
        }
        try
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("t", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteString("lvl", level.ToString());
                writer.WriteString("cat", category);
                writer.WriteString("msg", Redact(message));
                if (exception is not null)
                {
                    writer.WriteStartObject("exc");
                    writer.WriteString("type", exception.GetType().FullName);
                    writer.WriteString("msg", Redact(exception.Message));
                    writer.WriteString("stack", Redact(StackOf(exception)));
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            Interlocked.Increment(ref _pending);
            if (!_queue.Writer.TryWrite(Encoding.UTF8.GetString(stream.ToArray())))
            {
                Interlocked.Decrement(ref _pending);
            }
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            // The log never brings down whoever writes.
        }
    }

    /// <summary>Waits for the accepted lines to reach the disk (shutdown and crash); <c>false</c> if the time runs out.</summary>
    public bool Flush(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (Interlocked.Read(ref _pending) > 0)
        {
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }
            Thread.Sleep(10);
        }
        return true;
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        try
        {
            _writer.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }
    }

    /// <summary>Writes in batches (whatever is in the queue), rotating the file when the batch would take it over the limit.</summary>
    private async Task WriteLoopAsync()
    {
        var batch = new StringBuilder();
        while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            var lines = 0;
            if (_size < 0)
            {
                _size = File.Exists(CurrentPath) ? new FileInfo(CurrentPath).Length : 0;
            }
            while (_queue.Reader.TryRead(out var line))
            {
                lines++;
                var bytes = Encoding.UTF8.GetByteCount(line) + 1;
                if (_size + batch.Length + bytes > _maxBytes && _size + batch.Length > 0)
                {
                    await AppendAsync(batch).ConfigureAwait(false);
                    Rotate();
                    _size = 0;
                }
                batch.Append(line).Append('\n');
                if (batch.Length > 256 * 1024)
                {
                    await AppendAsync(batch).ConfigureAwait(false);
                }
            }
            await AppendAsync(batch).ConfigureAwait(false);
            Interlocked.Add(ref _pending, -lines);
        }
    }

    private async Task AppendAsync(StringBuilder batch)
    {
        if (batch.Length == 0)
        {
            return;
        }
        var text = batch.ToString();
        batch.Clear();
        try
        {
            await File.AppendAllTextAsync(CurrentPath, text).ConfigureAwait(false);
            _size += Encoding.UTF8.GetByteCount(text);
        }
        catch (IOException)
        {
            // Full disk or locked file: the lines are lost, the application carries on.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void Rotate()
    {
        try
        {
            for (var i = _maxFiles - 1; i >= 1; i--)
            {
                var from = i == 1 ? CurrentPath : Rotated(i - 1);
                var to = Rotated(i);
                if (File.Exists(from))
                {
                    File.Move(from, to, overwrite: true);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // File opened by another program: carries on with the current one.
        }
    }

    private string Rotated(int index) => Path.Combine(_directory, $"diagnostic.{index.ToString(CultureInfo.InvariantCulture)}.jsonl");

    /// <summary>Type and stack (with the inner causes), without the messages, which may carry SQL or values.</summary>
    public static string StackOf(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var sb = new StringBuilder();
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (sb.Length > 0)
            {
                sb.Append(" ---> ");
            }
            sb.Append(e.GetType().FullName).Append('\n').Append(e.StackTrace);
        }
        return sb.ToString();
    }

    /// <summary>Passwords in SQL and in connection strings become <c>***</c>.</summary>
    public static string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return ConnectionStringSecret().Replace(SecretMasking.MaskSql(text), m => m.Groups["key"].Value + "=***");
    }

    [GeneratedRegex(@"(?<key>\b(?:password|pwd|passwd)\s*)=\s*(?:""[^""]*""|'[^']*'|[^;\s]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConnectionStringSecret();
}
