using System.Text.RegularExpressions;
using Eunomia.App.Sessions;
using Eunomia.Drivers.Abstractions;

namespace Eunomia.App.Objects;

/// <summary>
/// Code editor for views, procedures, functions, triggers and events (phase 13, a simplified version of the editors in
/// appendix C §5): the user edits the <c>CREATE</c>; saving replaces the object without letting it disappear (see
/// <see cref="SaveAsync"/>). "Create new ▸" starts from a template.
/// </summary>
public static partial class ObjectCode
{
    /// <summary>SQL keyword of the kind (<c>VIEW</c>, <c>PROCEDURE</c>, ...).</summary>
    public static string Keyword(DbObjectKind kind) => kind switch
    {
        DbObjectKind.View => "VIEW",
        DbObjectKind.Procedure => "PROCEDURE",
        DbObjectKind.Function => "FUNCTION",
        DbObjectKind.Trigger => "TRIGGER",
        DbObjectKind.Event => "EVENT",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only views, routines, triggers and events are edited as code."),
    };

    /// <summary>Kinds edited as code.</summary>
    public static bool IsCodeObject(DbObjectKind kind) =>
        kind is DbObjectKind.View or DbObjectKind.Procedure or DbObjectKind.Function or DbObjectKind.Trigger or DbObjectKind.Event;

    /// <summary>"Create new ▸" template (a trigger uses the focused table, if any).</summary>
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
    /// Kind and name of the object the <c>CREATE</c> creates (without the database, if it is qualified); <c>null</c> if the
    /// text does not start with a <c>CREATE</c> of a view, routine, trigger or event.
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
    /// Saves without ever leaving the database without the object (phase 14): <c>USE</c> of the database and, for an object
    /// that already exists with the same name, <c>CREATE OR REPLACE</c> (views on MySQL and MariaDB; routines, triggers and
    /// events on MariaDB 10.1.4+), which is a single statement; on MySQL, routines are first validated under a temporary
    /// name. In the other cases (triggers and events on MySQL, a changed name): <c>DROP … IF EXISTS</c> and the new
    /// <c>CREATE</c>; if that fails, the original comes back with <paramref name="originalCode"/>. If not even the original
    /// comes back (for example, a <c>DEFINER</c> the user cannot use), the original code is written to a file in
    /// <paramref name="recoveryDirectory"/> and the error says so.
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
            // Routines do not run on their own: creating a copy under another name validates the code (syntax, DEFINER,
            // privileges) before dropping the original.
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
            // Any failure (server error, connection, cancel) puts the old object back before moving on.
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

    /// <summary>Writes the code of an object that was not created again; returns the path, or <c>null</c> without a folder.</summary>
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

    /// <summary>The <c>CREATE</c> with <c>OR REPLACE</c> (if it does not have it yet).</summary>
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

    /// <summary>The <c>CREATE</c> with another name (the same schema, if it is qualified).</summary>
    public static string WithName(string code, string quotedName)
    {
        ArgumentNullException.ThrowIfNull(code);
        var at = CreateKeywordIndex(code);
        if (at < 0)
        {
            return code;
        }
        // The pattern is anchored at the start: the text is searched from the CREATE on.
        var match = CreatePattern().Match(code[at..]);
        if (!match.Success)
        {
            return code;
        }
        var name = match.Groups["second"].Success ? match.Groups["second"] : match.Groups["first"];
        return string.Concat(code.AsSpan(0, at + name.Index), quotedName, code.AsSpan(at + name.Index + name.Length));
    }

    /// <summary>Position of the <c>CREATE</c> after the leading comments, or -1.</summary>
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
