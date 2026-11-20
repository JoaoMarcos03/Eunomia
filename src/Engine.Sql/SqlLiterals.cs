using System.Text.RegularExpressions;

namespace Eunomia.Engine.Sql;

/// <summary>
/// Text that can go into SQL as is: a number (ASCII digits only; <c>\d</c> also accepted <c>٣</c> and <c>３</c>, which the
/// server rejects) or a <c>0x</c> hex literal.
/// </summary>
public static partial class SqlLiterals
{
    /// <summary>Decimal number with an optional sign, fraction and exponent (<c>-1.5e3</c>, <c>.5</c>, <c>2.</c>).</summary>
    [GeneratedRegex(@"^[+-]?([0-9]+(\.[0-9]*)?|\.[0-9]+)([eE][+-]?[0-9]+)?$", RegexOptions.CultureInvariant)]
    public static partial Regex Number();

    /// <summary>Hex literal <c>0x…</c> (the prefix in lower case, as MySQL requires).</summary>
    [GeneratedRegex(@"^0x[0-9a-fA-F]*$", RegexOptions.CultureInvariant)]
    public static partial Regex Hex();
}
