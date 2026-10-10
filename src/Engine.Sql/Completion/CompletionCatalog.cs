using Eunomia.Engine.Sql.Lexing;

namespace Eunomia.Engine.Sql.Completion;

public enum CompletionKind
{
    Keyword,
    DataType,
    Function,
    Table,
    Column,
    Database,
    View,
    Routine,
    Variable,
}

/// <summary>Uma sugestão: o nome mostrado, o tipo (ícone e cor) e o texto a inserir.</summary>
public sealed record CompletionItem(string Text, CompletionKind Kind)
{
    /// <summary>Segunda coluna: tipo da coluna em minúsculas, valor da variável, assinatura.</summary>
    public string? Detail { get; init; }

    /// <summary>Comentário da coluna ou descrição (status bar).</summary>
    public string? Description { get; init; }

    /// <summary>Texto inserido, quando difere do mostrado (identificadores entre crases).</summary>
    public string? InsertText { get; init; }

    /// <summary>Coluna da chave primária (ícone de chave).</summary>
    public bool IsKey { get; init; }
}

/// <summary>Resultado do autocomplete: onde começa o texto a substituir e as sugestões pela ordem.</summary>
public sealed record CompletionProposal(int ReplaceStart, IReadOnlyList<CompletionItem> Items);

/// <summary>
/// Lista estática de completion do spike (sem análise de contexto; o Autocomplete A da secção 16.1
/// fica para o produto). Procura com "Find matches in the middle" ligado, como o HeidiSQL:
/// primeiro os que começam pelo texto, depois os que o contêm; ordem alfabética dentro de cada grupo.
/// </summary>
public sealed class CompletionCatalog
{
    private readonly CompletionItem[] _items;

    public CompletionCatalog(IEnumerable<CompletionItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = [.. items.OrderBy(i => i.Text, StringComparer.OrdinalIgnoreCase)];
    }

    public IReadOnlyList<CompletionItem> Items => _items;

    /// <summary>Catálogo com keywords, tipos e funções MySQL mais os nomes dados.</summary>
    public static CompletionCatalog CreateMySql(IEnumerable<string> tables, IEnumerable<string> columns)
    {
        var items = MySqlKeywords.Keywords.Select(k => new CompletionItem(k, CompletionKind.Keyword))
            .Concat(MySqlKeywords.DataTypes.Select(t => new CompletionItem(t, CompletionKind.DataType)))
            .Concat(MySqlKeywords.Functions.Select(f => new CompletionItem(f, CompletionKind.Function)))
            .Concat(tables.Select(t => new CompletionItem(t, CompletionKind.Table)))
            .Concat(columns.Select(c => new CompletionItem(c, CompletionKind.Column)));
        return new CompletionCatalog(items);
    }

    public IReadOnlyList<CompletionItem> Find(ReadOnlySpan<char> typed, int max = 200)
    {
        var prefix = new List<CompletionItem>();
        var middle = new List<CompletionItem>();
        foreach (var item in _items)
        {
            if (prefix.Count >= max)
            {
                break;
            }
            var index = item.Text.AsSpan().IndexOf(typed, StringComparison.OrdinalIgnoreCase);
            if (index == 0)
            {
                prefix.Add(item);
            }
            else if (index > 0 && middle.Count < max)
            {
                middle.Add(item);
            }
        }
        prefix.AddRange(middle.Take(max - prefix.Count));
        return prefix;
    }
}
