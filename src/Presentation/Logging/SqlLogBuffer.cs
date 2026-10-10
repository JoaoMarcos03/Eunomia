using System.Globalization;
using Eunomia.App.Settings;

namespace Eunomia.Presentation.Logging;

/// <summary>Categorias do SQL log (preferências Errors / User SQL / Internal SQL / Script / Info / Debug).</summary>
public enum LogCategory
{
    Error,
    UserSql,
    InternalSql,
    Script,
    Info,
    Debug,
}

/// <summary>Tipo de linha, que dá a cor de fundo (anexo C §1.10).</summary>
public enum LogLineKind
{
    Sql,
    Error,
    Warning,
    Note,
    Info,
    Comment,
}

/// <summary>Linha do SQL log com o número contínuo que aparece no gutter.</summary>
public sealed record LogLine(long Number, string Text, LogLineKind Kind);

/// <summary>Alteração do log: linhas novas no fim e/ou linhas cortadas no início (ou tudo limpo).</summary>
public sealed record LogChange(IReadOnlyList<LogLine> Appended, int TrimmedFromStart, bool Cleared);

/// <summary>
/// Conteúdo do SQL log com as regras do <c>LogSQL</c> do HeidiSQL (anexo C §1.10, FAM-Q04): filtro por categoria,
/// prefixo <c>[hh:nn:ss.zzz]</c> opcional, linhas cortadas em <c>logsqlwidth</c> com <c>...</c>, delimitador
/// acrescentado ao SQL, mensagens não-SQL como <c>/* … */</c>, só as últimas <c>logsqlnum</c> linhas com a
/// numeração contínua. Thread-safe: o motor escreve de qualquer thread; a UI aplica <see cref="Changed"/> na UI thread.
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
    /// Acrescenta uma mensagem. Devolve a linha escrita, ou <c>null</c> se a categoria estiver desligada nas
    /// preferências.
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

    /// <summary>Clear (Ctrl+Q). A numeração continua.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _lines.Clear();
        }
        Changed?.Invoke(this, new LogChange([], 0, Cleared: true));
    }

    /// <summary>Texto do log inteiro (Save as textfile).</summary>
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
        // O memo do log é de uma linha por entrada: quebras de linha passam a espaço.
        var text = message.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ').Trim();
        LogLineKind kind;
        if (category is LogCategory.UserSql or LogCategory.InternalSql or LogCategory.Script)
        {
            kind = LogLineKind.Sql;
            // Passwords em CREATE USER / SET PASSWORD não ficam no log (secção 23.2).
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

/// <summary>Mensagens das sessões (motor) para o SQL log, com as categorias do HeidiSQL.</summary>
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
