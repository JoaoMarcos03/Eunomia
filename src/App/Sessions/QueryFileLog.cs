using System.Globalization;
using System.Text;
using Eunomia.Engine.Execution;
using Eunomia.Engine.Sql;

namespace Eunomia.App.Sessions;

/// <summary>
/// Session Manager "Log queries to file" (Advanced; phase 14): the session user's statements (query tabs, grid edits,
/// table editor, Drop/Empty) that change structure ("DDL queries") or data ("DML queries") go to a text file, one per
/// line with the time, as a record of what ran in production. Unlike HeidiSQL (section 23.1 S7), passwords are masked
/// (<see cref="SecretMasking"/>) and the file is only open during each write, in append mode. A write failure (full
/// disk, permissions) never stops the query: an error line goes to the SQL log.
/// </summary>
public sealed class QueryFileLog(ISessionLog inner, string path, bool ddl, bool dml) : ISessionLog
{
    private readonly Lock _gate = new();
    private bool _failed;

    public string Path { get; } = path;

    public void Write(string session, SessionLogCategory category, string message)
    {
        inner.Write(session, category, message);
        if (category != SessionLogCategory.UserSql || !Wanted(message))
        {
            return;
        }
        var line = string.Create(CultureInfo.InvariantCulture, $"/* {DateTime.Now:yyyy-MM-dd HH:mm:ss} [{session}] */ {SecretMasking.MaskSql(message.Trim())};{Environment.NewLine}");
        lock (_gate)
        {
            try
            {
                var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                File.AppendAllText(Path, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                _failed = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                if (!_failed)
                {
                    _failed = true;
                    inner.Write(session, SessionLogCategory.Error, $"Could not write the query log file {Path}: {ex.Message}");
                }
            }
        }
    }

    private bool Wanted(string sql) => TransactionTracker.Classify(sql) switch
    {
        TransactionEffect.ImplicitCommit => ddl,
        TransactionEffect.Change => dml,
        _ => false,
    };
}
