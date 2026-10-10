using System.Text.RegularExpressions;

namespace Eunomia.Engine.Sql;

/// <summary>
/// Masks secrets in SQL that goes to logs (section 23.2): the literals after <c>IDENTIFIED BY</c> /
/// <c>IDENTIFIED WITH … BY|AS</c>, <c>SET PASSWORD … =</c>, <c>PASSWORD('…')</c> / <c>OLD_PASSWORD('…')</c>,
/// <c>MASTER_PASSWORD =</c> and <c>PASSWORD '…'</c> (<c>CREATE SERVER</c> options) become <c>'***'</c>. The rest of the
/// SQL stays as it is.
/// </summary>
public static partial class SecretMasking
{
    public const string Mask = "'***'";

    public static string MaskSql(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        if (sql.IndexOf("PASSWORD", StringComparison.OrdinalIgnoreCase) < 0 && sql.IndexOf("IDENTIFIED", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return sql;
        }
        return Secret().Replace(sql, m => m.Groups["prefix"].Value + Mask);
    }

    /// <summary>
    /// The SQL holds a secret that <see cref="MaskSql"/> would mask. Used to keep it off disk (query
    /// history): masked it would be useless to run again (the password would become <c>***</c>).
    /// </summary>
    public static bool ContainsSecret(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        return (sql.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase) || sql.Contains("IDENTIFIED", StringComparison.OrdinalIgnoreCase))
            && Secret().IsMatch(sql);
    }

    // A text literal with backslash escapes or doubled quotes.
    private const string Literal = @"(?:'(?:[^'\\]|\\.|'')*'|""(?:[^""\\]|\\.|"""")*"")";

    [GeneratedRegex(
        @"(?<prefix>\bIDENTIFIED\s+(?:WITH\s+\S+\s+)?(?:BY|AS)\s+(?:PASSWORD\s+)?|\bSET\s+PASSWORD\b[^=;]*=\s*(?:(?:OLD_)?PASSWORD\s*\(\s*)?|\b(?:OLD_)?PASSWORD\s*\(\s*|\bMASTER_PASSWORD\s*=\s*|\bPASSWORD\s+)"
        + Literal,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex Secret();
}
