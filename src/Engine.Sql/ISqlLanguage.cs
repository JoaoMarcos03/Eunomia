using Eunomia.Engine.Sql.Lexing;

namespace Eunomia.Engine.Sql;

/// <summary>
/// SQL language of an engine family (section 21.1, <c>ISqlLanguageService</c>): what the editor, the highlighting and the
/// autocomplete know about the syntax without asking the server. Outside <c>Engine.Sql</c> and the driver, code does not
/// know an engine's syntax: the names inserted in the editor, the word lists and the highlighting lexer come from here.
/// Today there is only MySQL/MariaDB's; the splitter and the autocomplete context move behind this interface with
/// roadmap item 3.5.
/// </summary>
public interface ISqlLanguage
{
    /// <summary>Keywords (highlighting, autocomplete, helpers and automatic upper case).</summary>
    IReadOnlySet<string> Keywords { get; }

    /// <summary>Data types.</summary>
    IReadOnlySet<string> DataTypes { get; }

    /// <summary>Functions (autocomplete, helpers and Insert value ▸ SQL function).</summary>
    IReadOnlySet<string> Functions { get; }

    /// <summary>Tokenises a line from the state left by the previous one and returns the final state (incremental highlighting, section 16.1).</summary>
    LexerState TokenizeLine(ReadOnlySpan<char> line, LexerState state, List<SqlToken> tokens);

    /// <summary>Identifier always quoted (when the user has already opened the quote in the editor).</summary>
    string QuoteIdentifier(string name);

    /// <summary>
    /// Name to insert in the editor (FAM-T07; appendix D A12): quoted only when needed (a word of the language, characters
    /// outside plain identifiers, only digits), so the text looks the way the user would type it.
    /// </summary>
    string EditorIdentifier(string name);

    /// <summary>Qualified <c>database.object</c> name for the editor (Shift with double click or drag).</summary>
    string EditorQualifiedName(string database, string name) => EditorIdentifier(database) + "." + EditorIdentifier(name);
}
