using Eunomia.Engine.Sql.Lexing;

namespace Eunomia.Engine.Sql;

/// <summary>
/// Linguagem SQL de uma família de motores (secção 21.1, <c>ISqlLanguageService</c>): o que o editor, o realce e o
/// autocomplete sabem da sintaxe sem perguntar ao servidor. Fora do <c>Engine.Sql</c> e do driver, o código não conhece
/// a sintaxe de um motor: os nomes que entram no editor, as listas de palavras e o lexer do realce vêm daqui. Hoje só há
/// a do MySQL/MariaDB; o splitter e o contexto do autocomplete passam para trás desta interface com o item 3.5 do
/// roadmap.
/// </summary>
public interface ISqlLanguage
{
    /// <summary>Palavras-chave (realce, autocomplete, helpers e uppercase automático).</summary>
    IReadOnlySet<string> Keywords { get; }

    /// <summary>Tipos de dados.</summary>
    IReadOnlySet<string> DataTypes { get; }

    /// <summary>Funções (autocomplete, helpers e Insert value ▸ SQL function).</summary>
    IReadOnlySet<string> Functions { get; }

    /// <summary>Tokeniza uma linha a partir do estado deixado pela anterior e devolve o estado final (realce incremental, secção 16.1).</summary>
    LexerState TokenizeLine(ReadOnlySpan<char> line, LexerState state, List<SqlToken> tokens);

    /// <summary>Identificador sempre citado (quando o utilizador já abriu a citação no editor).</summary>
    string QuoteIdentifier(string name);

    /// <summary>
    /// Nome para inserir no editor (FAM-T07; anexo D A12): citado só quando é preciso (palavra da linguagem, caracteres
    /// fora dos identificadores simples, só dígitos), para o texto ficar como o utilizador o escreveria.
    /// </summary>
    string EditorIdentifier(string name);

    /// <summary>Nome qualificado <c>base.objeto</c> para o editor (Shift com duplo clique ou drag).</summary>
    string EditorQualifiedName(string database, string name) => EditorIdentifier(database) + "." + EditorIdentifier(name);
}
