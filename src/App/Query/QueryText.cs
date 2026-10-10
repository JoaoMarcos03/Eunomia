using System.Globalization;
using System.Text.RegularExpressions;
using Eunomia.Engine.Sql.Splitting;

namespace Eunomia.App.Query;

/// <summary>Text rules of the query tabs (section 4.7; appendix C §4.4).</summary>
public static partial class QueryText
{
    [GeneratedRegex(@"^\s*(UPDATE|DELETE)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UpdateOrDelete();

    [GeneratedRegex(@"\sWHERE\s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Where();

    [GeneratedRegex(@"--\s*names\s*:\s*(?<value>[^\r\n]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NamesComment();

    [GeneratedRegex(@"--\s*name\s*:\s*(?<value>[^\r\n]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SingleNameComment();

    [GeneratedRegex(@"\bat line (?<line>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ErrorLine();

    /// <summary>UPDATE or DELETE without <c>\sWHERE\s</c> ("Run unsafe queries without a WHERE clause?").</summary>
    public static bool IsUnsafe(string statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        var text = StripLeadingComments(statement) + " ";
        return UpdateOrDelete().IsMatch(text) && !Where().IsMatch(text);
    }

    private static string StripLeadingComments(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
            }
            else if (text[i] == '#' || (text[i] == '-' && i + 1 < text.Length && text[i + 1] == '-'))
            {
                var end = text.IndexOf('\n', i);
                i = end < 0 ? text.Length : end + 1;
            }
            else if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*' && !(i + 2 < text.Length && text[i + 2] == '!'))
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
            }
            else
            {
                break;
            }
        }
        return text[i..];
    }

    /// <summary>"-- names: a,b,c" in the first 1 KiB of the batch: result tab names, in order.</summary>
    public static IReadOnlyList<string> ResultNames(string batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var head = batch.Length > 1024 ? batch[..1024] : batch;
        return NamesComment().Match(head) is { Success: true } m
            ? m.Groups["value"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            : [];
    }

    /// <summary>"-- name: x" inside the statement: name of that statement's result tab.</summary>
    public static string? ResultName(string statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return SingleNameComment().Match(statement) is { Success: true } m && m.Groups["value"].Value.Trim() is { Length: > 0 } name ? name : null;
    }

    /// <summary>
    /// Result tab caption: the name (from <c>-- name:</c> / <c>-- names:</c>), otherwise the source table, otherwise "Result #n";
    /// followed by "(Nr × Mc)".
    /// </summary>
    public static string ResultCaption(string? name, string? sourceTable, int index, long rows, int columns) =>
        string.Create(CultureInfo.InvariantCulture, $"{name ?? sourceTable ?? $"Result #{index + 1}"} ({rows}r × {columns}c)");

    /// <summary>
    /// Editor line where the error happened: "near … at line N" is relative to the statement; without a number, the first
    /// line of the statement.
    /// </summary>
    public static int ErrorLineOf(SqlStatement statement, string message)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(message);
        return ErrorLine().Match(message) is { Success: true } m && int.TryParse(m.Groups["line"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var line)
            ? statement.Line + Math.Max(0, line - 1)
            : statement.Line;
    }

    /// <summary>
    /// Final message in the SQL log (appendix C §4.4): <c>Affected rows: n  Found rows: n  Warnings: n  Duration for k [of m]
    /// query|queries: x.xxx sec. (+ y sec. network)</c>.
    /// </summary>
    public static string Summary(long affected, long found, long warnings, int executed, int total, TimeSpan duration, TimeSpan network)
    {
        var c = CultureInfo.InvariantCulture;
        var count = executed == total
            ? executed == 1 ? "1 query" : $"{executed} queries"
            : $"{executed} of {total} {(total == 1 ? "query" : "queries")}";
        var networkPart = network > TimeSpan.Zero ? string.Create(c, $" (+ {network.TotalSeconds:0.000} sec. network)") : string.Empty;
        return string.Create(c, $"Affected rows: {affected:N0}  Found rows: {found:N0}  Warnings: {warnings:N0}  Duration for {count}: {duration.TotalSeconds:0.000} sec.{networkPart}");
    }
}
