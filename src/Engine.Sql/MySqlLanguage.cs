using Eunomia.Engine.Sql.Lexing;

namespace Eunomia.Engine.Sql;

/// <summary>
/// Linguagem do MySQL e do MariaDB: palavras de <see cref="MySqlKeywords"/>, lexer <see cref="MySqlLexer"/> e
/// identificadores entre crases (as crases de dentro duplicadas). No editor, um nome só leva crases quando é palavra
/// reservada ou tipo, tem caracteres fora de <c>[A-Za-z0-9_$]</c> ou é só dígitos.
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
