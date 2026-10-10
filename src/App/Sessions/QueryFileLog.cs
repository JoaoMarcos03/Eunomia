using System.Globalization;
using System.Text;
using Eunomia.Engine.Execution;
using Eunomia.Engine.Sql;

namespace Eunomia.App.Sessions;

/// <summary>
/// "Log queries to file" do Session Manager (Advanced; Fase 14): as instruções do utilizador da sessão (query tabs, edições
/// do grid, table editor, Drop/Empty) que mudam estrutura ("DDL queries") ou dados ("DML queries") ficam num ficheiro de
/// texto, uma por linha com a hora, como registo do que se correu em produção. Ao contrário do HeidiSQL (secção 23.1 S7),
/// as passwords vão mascaradas (<see cref="SecretMasking"/>) e o ficheiro fica aberto só durante cada escrita, em modo
/// acrescentar. Uma falha de escrita (disco cheio, permissões) nunca pára a query: fica uma linha de erro no SQL log.
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
