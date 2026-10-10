using System.Text.RegularExpressions;

namespace Eunomia.Engine.Sql;

/// <summary>
/// Máscara dos segredos em SQL que vai para logs (secção 23.2): os literais depois de <c>IDENTIFIED BY</c> /
/// <c>IDENTIFIED WITH … BY|AS</c>, <c>SET PASSWORD … =</c>, <c>PASSWORD('…')</c> / <c>OLD_PASSWORD('…')</c>,
/// <c>MASTER_PASSWORD =</c> e <c>PASSWORD '…'</c> (opções de <c>CREATE SERVER</c>) passam a <c>'***'</c>. O resto do
/// SQL fica como está.
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
    /// O SQL tem um segredo que <see cref="MaskSql"/> mascararia. Serve para não o guardar em disco (histórico de
    /// queries): mascarado não servia para correr de novo (a password passava a ser <c>***</c>).
    /// </summary>
    public static bool ContainsSecret(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        return (sql.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase) || sql.Contains("IDENTIFIED", StringComparison.OrdinalIgnoreCase))
            && Secret().IsMatch(sql);
    }

    // Um literal de texto com escapes por barra invertida ou aspas duplicadas.
    private const string Literal = @"(?:'(?:[^'\\]|\\.|'')*'|""(?:[^""\\]|\\.|"""")*"")";

    [GeneratedRegex(
        @"(?<prefix>\bIDENTIFIED\s+(?:WITH\s+\S+\s+)?(?:BY|AS)\s+(?:PASSWORD\s+)?|\bSET\s+PASSWORD\b[^=;]*=\s*(?:(?:OLD_)?PASSWORD\s*\(\s*)?|\b(?:OLD_)?PASSWORD\s*\(\s*|\bMASTER_PASSWORD\s*=\s*|\bPASSWORD\s+)"
        + Literal,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex Secret();
}
