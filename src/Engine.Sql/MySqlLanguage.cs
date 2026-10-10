using Eunomia.Engine.Sql.Lexing;

namespace Eunomia.Engine.Sql;

/// <summary>
/// The MySQL and MariaDB language: words from <see cref="MySqlKeywords"/>, the <see cref="MySqlLexer"/> lexer and
/// backticked identifiers (inner backticks doubled). In the editor, a name only gets backticks when it is a reserved
/// word or a type, has characters outside <c>[A-Za-z0-9_$]</c> or is only digits.
/// </summary>
internal sealed class MySqlLanguage : ISqlLanguage
{
    public IReadOnlySet<string> Keywords => MySqlKeywords.Keywords;

    public IReadOnlySet<string> DataTypes => MySqlKeywords.DataTypes;

    public IReadOnlySet<string> Functions => MySqlKeywords.Functions;

    public LexerState TokenizeLine(ReadOnlySpan<char> line, LexerState state, List<SqlToken> tokens) => MySqlLexer.TokenizeLine(line, state, tokens);

    public string QuoteIdentifier(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return "`" + name.Replace("`", "``", StringComparison.Ordinal) + "`";
    }

    public string EditorIdentifier(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return NeedsQuotes(name) ? QuoteIdentifier(name) : name;
    }

    private static bool NeedsQuotes(string name)
    {
        if (name.Length == 0 || MySqlKeywords.Keywords.Contains(name) || MySqlKeywords.DataTypes.Contains(name))
        {
            return true;
        }
        var allDigits = true;
        foreach (var c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '$'))
            {
                return true;
            }
            allDigits &= char.IsAsciiDigit(c);
        }
        return allDigits;
    }
}
