using System.Text.RegularExpressions;
using Eunomia.App.Sessions;
using Eunomia.Drivers.Abstractions;

namespace Eunomia.App.Objects;

/// <summary>
/// Editor por código de views, procedures, functions, triggers e events (Fase 13, versão simplificada dos editores do
/// anexo C §5): o utilizador edita o <c>CREATE</c>; gravar substitui o objeto sem o deixar desaparecer (ver
/// <see cref="SaveAsync"/>). "Create new ▸" começa por um modelo.
/// </summary>
public static partial class ObjectCode
{
    /// <summary>Palavra-chave SQL do tipo (<c>VIEW</c>, <c>PROCEDURE</c>, ...).</summary>
    public static string Keyword(DbObjectKind kind) => kind switch
    {
        DbObjectKind.View => "VIEW",
        DbObjectKind.Procedure => "PROCEDURE",
        DbObjectKind.Function => "FUNCTION",
        DbObjectKind.Trigger => "TRIGGER",
        DbObjectKind.Event => "EVENT",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only views, routines, triggers and events are edited as code."),
    };

    /// <summary>Tipos que se editam como código.</summary>
    public static bool IsCodeObject(DbObjectKind kind) =>
        kind is DbObjectKind.View or DbObjectKind.Procedure or DbObjectKind.Function or DbObjectKind.Trigger or DbObjectKind.Event;

    /// <summary>Modelo do "Create new ▸" (um trigger usa a tabela focada, se houver).</summary>
    public static string Template(DbObjectKind kind, IDialect dialect, string? table = null)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        return kind switch
        {
            DbObjectKind.View => $"CREATE VIEW {dialect.QuoteIdentifier("view_name")} AS\nSELECT 1;",
            DbObjectKind.Procedure => $"CREATE PROCEDURE {dialect.QuoteIdentifier("procedure_name")}()\nBEGIN\n\t\nEND",
            DbObjectKind.Function => $"CREATE FUNCTION {dialect.QuoteIdentifier("function_name")}()\nRETURNS INT\nBEGIN\n\tRETURN 0;\nEND",
            DbObjectKind.Trigger => $"CREATE TRIGGER {dialect.QuoteIdentifier("trigger_name")} BEFORE INSERT ON {dialect.QuoteIdentifier(table ?? "table_name")}\nFOR EACH ROW\nBEGIN\n\t\nEND",
            DbObjectKind.Event => $"CREATE EVENT {dialect.QuoteIdentifier("event_name")}\nON SCHEDULE EVERY 1 DAY\nDO\nBEGIN\n\t\nEND",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only views, routines, triggers and events are edited as code."),
        };
    }

    /// <summary>
    /// Tipo e nome do objeto que o <c>CREATE</c> cria (sem a base, se vier qualificado); <c>null</c> se o texto não
    /// começar por um <c>CREATE</c> de view, rotina, trigger ou event.
    /// </summary>
    public static (DbObjectKind Kind, string Name)? Parse(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        var match = CreatePattern().Match(StripLeadingComments(code));
        if (!match.Success)
        {
            return null;
        }
        var kind = match.Groups["kind"].Value.ToUpperInvariant() switch
        {
            "VIEW" => DbObjectKind.View,
            "PROCEDURE" => DbObjectKind.Procedure,
            "FUNCTION" => DbObjectKind.Function,
            "TRIGGER" => DbObjectKind.Trigger,
            _ => DbObjectKind.Event,
        };
        var name = match.Groups["second"].Success ? match.Groups["second"].Value : match.Groups["first"].Value;
        return (kind, Unquote(name));
    }

    /// <summary>
    /// Grava sem nunca deixar a base sem o objeto (Fase 14): <c>USE</c> da base e, num objeto que já existe com o mesmo
    /// nome, <c>CREATE OR REPLACE</c> (views em MySQL e MariaDB; rotinas, triggers e events no MariaDB 10.1.4+), que é uma
    /// só instrução; no MySQL, rotinas validam-se primeiro com um nome temporário. Nos outros casos (triggers e events no
    /// MySQL, nome mudado): <c>DROP … IF EXISTS</c> e o <c>CREATE</c> novo; se este falhar, o original volta com
    /// <paramref name="originalCode"/>. Se nem o original voltar (por exemplo, um <c>DEFINER</c> que o utilizador não pode
    /// usar), o código original fica num ficheiro em <paramref name="recoveryDirectory"/> e o erro di-lo.
    /// </summary>
    public static async Task SaveAsync(OpenSession session, string database, DbObjectRef? original, string? originalCode, string code, CancellationToken cancellationToken, string? recoveryDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(code);
        var services = session.Host.ServerServices ?? throw new InvalidOperationException("Not connected.");
        var dialect = services.Dialect;
        await session.Host.UseDatabaseAsync(database, cancellationToken).ConfigureAwait(false);
        if (original is null)
        {
            await RunAsync(session, code, cancellationToken).ConfigureAwait(false);
            return;
        }
        var parsed = Parse(code);
        var sameObject = parsed is { } p && p.Kind == original.Kind && string.Equals(p.Name, original.Name, StringComparison.Ordinal);
        var orReplace = original.Kind == DbObjectKind.View || services.Capabilities.Supports(Capability.CreateOrReplaceObjects);
        if (sameObject && orReplace)
        {
            await RunAsync(session, WithOrReplace(code), cancellationToken).ConfigureAwait(false);
            return;
        }
        if (sameObject && original.Kind is DbObjectKind.Procedure or DbObjectKind.Function)
        {
            // Rotinas não correm sozinhas: criar uma cópia com outro nome valida o código (sintaxe, DEFINER, privilégios)
            // antes de largar a original.
            var temp = "eunomia_check_" + Guid.NewGuid().ToString("N")[..12];
            await RunAsync(session, WithName(code, dialect.QuoteIdentifier(temp)), cancellationToken).ConfigureAwait(false);
            await RunAsync(session, $"DROP {Keyword(original.Kind)} IF EXISTS {dialect.QualifiedName(original with { Name = temp })}", CancellationToken.None).ConfigureAwait(false);
        }
        await RunAsync(session, $"DROP {Keyword(original.Kind)} IF EXISTS {dialect.QualifiedName(original)}", cancellationToken).ConfigureAwait(false);
        try
        {
            await RunAsync(session, code, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (originalCode is { Length: > 0 })
        {
            // Qualquer falha (erro do servidor, ligação, cancel) deixa o objeto antigo de volta antes de seguir.
            try
            {
                await RunAsync(session, originalCode, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception restoreFailure) when (restoreFailure is not OutOfMemoryException)
            {
                var file = SaveRecovery(recoveryDirectory, database, original, originalCode);
                throw new InvalidOperationException(
                    $"{failure.Message}\n\nThe original {Keyword(original.Kind).ToLowerInvariant()} \"{original.Name}\" could not be created again ({restoreFailure.Message})." +
                    (file is null ? string.Empty : $" Its code was saved to {file}."), failure);
            }
            throw;
        }
    }

    /// <summary>Grava o código de um objeto que não voltou a ser criado; devolve o caminho, ou <c>null</c> sem pasta.</summary>
    private static string? SaveRecovery(string? directory, string database, DbObjectRef obj, string code)
    {
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }
        try
        {
            Directory.CreateDirectory(directory);
            var safe = string.Concat($"{database}.{obj.Name}".Select(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_'));
            var file = Path.Combine(directory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Keyword(obj.Kind).ToLowerInvariant()}-{safe}.sql");
            File.WriteAllText(file, $"-- {Keyword(obj.Kind)} {database}.{obj.Name}, as it was before the failed save\n{code}\n");
            return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>O <c>CREATE</c> com <c>OR REPLACE</c> (se ainda não tiver).</summary>
    public static string WithOrReplace(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        var at = CreateKeywordIndex(code);
        if (at < 0)
        {
            return code;
        }
        var after = code.AsSpan(at + "CREATE".Length);
        var rest = after.TrimStart();
        return rest.StartsWith("OR", StringComparison.OrdinalIgnoreCase) && OrReplacePattern().IsMatch(rest.ToString())
            ? code
            : string.Concat(code.AsSpan(0, at + "CREATE".Length), " OR REPLACE", after);
    }

    /// <summary>O <c>CREATE</c> com outro nome (o mesmo esquema, se vier qualificado).</summary>
    public static string WithName(string code, string quotedName)
    {
        ArgumentNullException.ThrowIfNull(code);
        var at = CreateKeywordIndex(code);
        if (at < 0)
        {
            return code;
        }
        // O padrão está ancorado no início: procura-se no texto a partir do CREATE.
        var match = CreatePattern().Match(code[at..]);
        if (!match.Success)
        {
            return code;
        }
        var name = match.Groups["second"].Success ? match.Groups["second"] : match.Groups["first"];
        return string.Concat(code.AsSpan(0, at + name.Index), quotedName, code.AsSpan(at + name.Index + name.Length));
    }

    /// <summary>Posição do <c>CREATE</c> depois dos comentários iniciais, ou -1.</summary>
    private static int CreateKeywordIndex(string code)
    {
        var stripped = StripLeadingComments(code);
        var at = code.Length - stripped.Length;
        return stripped.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase) ? at : -1;
    }

    [GeneratedRegex(@"^OR\s+REPLACE\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OrReplacePattern();

    private static Task RunAsync(OpenSession session, string sql, CancellationToken cancellationToken) =>
        SafeDdl.RunAsync(session.Host, strict: false, (h, ct) => h.ExecuteNonQueryAsync(sql, ct), cancellationToken);

    private static string StripLeadingComments(string code)
    {
        var text = code.TrimStart();
        while (true)
        {
            if (text.StartsWith("--", StringComparison.Ordinal) || text.StartsWith('#'))
            {
                var end = text.IndexOf('\n', StringComparison.Ordinal);
                text = end < 0 ? string.Empty : text[(end + 1)..].TrimStart();
            }
            else if (text.StartsWith("/*", StringComparison.Ordinal) && !text.StartsWith("/*!", StringComparison.Ordinal))
            {
                var end = text.IndexOf("*/", StringComparison.Ordinal);
                text = end < 0 ? string.Empty : text[(end + 2)..].TrimStart();
            }
            else
            {
                return text;
            }
        }
    }

    private static string Unquote(string name) =>
        name.Length >= 2 && name[0] == '`' && name[^1] == '`' ? name[1..^1].Replace("``", "`", StringComparison.Ordinal) : name;

    [GeneratedRegex(
        @"^CREATE\s+(?:OR\s+REPLACE\s+)?(?:(?:ALGORITHM\s*=\s*\w+|DEFINER\s*=\s*(?:`[^`]*`|'[^']*'|\w+)(?:\s*@\s*(?:`[^`]*`|'[^']*'|[\w.%-]+))?|SQL\s+SECURITY\s+\w+|AGGREGATE)\s+)*(?<kind>VIEW|PROCEDURE|FUNCTION|TRIGGER|EVENT)\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<first>`(?:[^`]|``)+`|[\w$]+)(?:\s*\.\s*(?<second>`(?:[^`]|``)+`|[\w$]+))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreatePattern();
}
