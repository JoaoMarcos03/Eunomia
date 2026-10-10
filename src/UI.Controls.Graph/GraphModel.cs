using Avalonia;

namespace Eunomia.UI.Controls.Graph;

[Flags]
public enum GraphRowFlags
{
    None = 0,
    PrimaryKey = 1,
    Unique = 2,
    ForeignKey = 4,
    Nullable = 8,
}

/// <summary>Uma coluna desenhada no nó (SV-01): nome, tipo, ícones PK/UNIQUE/FK e NULL; a cor vem da categoria do tipo.</summary>
/// <param name="Category">Categoria de tipo (a ordem do <c>TypeCategory</c>): o canvas pede a cor a <see cref="SchemaCanvas.TypeColor"/>.</param>
public sealed record GraphRow(string Name, string Type, GraphRowFlags Flags, int Category = 6);

/// <summary>Uma tabela do diagrama. A posição é do canto superior esquerdo, em coordenadas do diagrama.</summary>
public sealed class GraphNode(string id, string title, IReadOnlyList<GraphRow> rows)
{
    public string Id { get; } = id;

    public string Title { get; } = title;

    public IReadOnlyList<GraphRow> Rows { get; } = rows;

    public Point Position { get; set; }

    /// <summary>Tamanho calculado pelo canvas (cabeçalho e uma linha por coluna).</summary>
    public Size Size { get; internal set; }

    public Rect Bounds => new(Position, Size);

    /// <summary>Escondida pelo utilizador (SV-10).</summary>
    public bool IsHidden { get; set; }

    /// <summary>Nova desde o último layout guardado (SV-16: marcada até ser movida ou o diagrama relido).</summary>
    public bool IsNew { get; set; }

    /// <summary>Resultado da pesquisa (SV-06): <c>null</c> sem pesquisa; <c>false</c> fica esbatida.</summary>
    public bool? IsMatch { get; set; }

    public object? Tag { get; set; }
}

public enum GraphCardinality
{
    OneToMany,
    OneToOne,
}

/// <summary>
/// Relação por FK (SV-02, SV-03): de <see cref="From"/> (a tabela com a FK, lado "muitos") para <see cref="To"/> (a
/// referenciada), ligada às linhas das colunas.
/// </summary>
public sealed record GraphEdge(string Name, GraphNode From, IReadOnlyList<int> FromRows, GraphNode To, IReadOnlyList<int> ToRows, GraphCardinality Cardinality);

public sealed class GraphNodeEventArgs(GraphNode node, Point position) : EventArgs
{
    public GraphNode Node { get; } = node;

    /// <summary>Posição no controlo (para abrir menus).</summary>
    public Point Position { get; } = position;
}
