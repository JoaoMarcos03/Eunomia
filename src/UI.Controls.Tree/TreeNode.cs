namespace Eunomia.UI.Controls.Tree;

/// <summary>Níveis da árvore de bases (secção 4.2).</summary>
public enum TreeNodeKind
{
    Session,
    Database,

    /// <summary>Grupo opcional (Tables, Views, ...) quando "Group objects by type" está ligado.</summary>
    Group,
    Table,
    View,
    Procedure,
    Function,
    Trigger,
    Event,
    Column,

    /// <summary>Nó "a carregar…" enquanto os filhos são pedidos.</summary>
    Placeholder,
}

/// <summary>Ícone de um nó de coluna: a primeira chave que a contém, ou campo simples (anexo D, A2).</summary>
public enum ColumnKeyMarker
{
    None,
    Primary,
    Unique,
    Key,
    FullText,
    Spatial,
}

/// <summary>
/// Nó da árvore. Os filhos são carregados quando o nó é expandido pela primeira vez (lazy);
/// <see cref="Children"/> é <c>null</c> até lá.
/// </summary>
public sealed class TreeNode
{
    internal int MatchVersion = -1;
    internal bool MatchResult;

    public TreeNode(TreeNodeKind kind, string name, TreeNode? parent = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        Kind = kind;
        Name = name;
        Parent = parent;
        Depth = parent is null ? 0 : parent.Depth + 1;
        Session = kind == TreeNodeKind.Session ? this : parent?.Session;
    }

    public TreeNodeKind Kind { get; }

    /// <summary>Texto da coluna 0: o nome do objeto, sem o nome da base. É o texto comparado pelos filtros.</summary>
    public string Name { get; }

    /// <summary>Texto desenhado: nos grupos, "Tables (12)" quando os filhos já existem (anexo D, A1).</summary>
    public string DisplayText => Kind == TreeNodeKind.Group && !IsLoading && Children is { } children
        ? $"{Name} ({children.Count})"
        : Name;

    public TreeNode? Parent { get; }

    public TreeNode? Session { get; }

    public int Depth { get; }

    /// <summary>Dados da aplicação associados ao nó (id da sessão, referência do objeto).</summary>
    public object? Tag { get; init; }

    /// <summary>
    /// Ícone esbatido (secção 5.7): sessão sem ligação, base ainda sem cache, objeto ainda não selecionado nesta sessão,
    /// grupo vazio.
    /// </summary>
    public bool IsGhosted { get; set; }

    /// <summary>Base em <c>USE</c> na ligação primária: marcador no ícone (anexo D, A2).</summary>
    public bool IsCurrent { get; set; }

    /// <summary>Engine da tabela quando tem overlay (MEMORY, CSV, FEDERATED, ARIA, ...); <c>null</c> nas restantes.</summary>
    public string? EngineOverlay { get; set; }

    /// <summary>Tipo dos objetos de um grupo (o ícone do grupo é o do tipo).</summary>
    public TreeNodeKind GroupOf { get; init; } = TreeNodeKind.Group;

    /// <summary>Colunas: chave que dá o ícone.</summary>
    public ColumnKeyMarker KeyMarker { get; init; }

    /// <summary>Colunas: categoria do tipo para a cor do texto (as cores do grid); -1 = cor normal.</summary>
    public int TextCategory { get; init; } = -1;

    /// <summary>Cor da sessão em ARGB (só nos nós de sessão). 0 = sem cor.</summary>
    public uint SessionColor { get; init; }

    /// <summary>Tamanho em bytes (tabelas: Data_length + Index_length). Bases e sessões: soma (ver <see cref="TreeModel.UpdateSizes"/>).</summary>
    public long? SizeBytes { get; set; }

    /// <summary>Base: os tamanhos dos objetos já chegaram (a soma aparece mesmo que dê 0).</summary>
    public bool ChildSizesKnown { get; set; }

    public bool IsFavorite { get; set; }

    public bool IsExpanded { get; internal set; }

    public bool IsLoading { get; internal set; }

    /// <summary>Tem filhos por carregar (tabelas com "Columns below tables in tree").</summary>
    public bool MayHaveChildren { get; init; }

    /// <summary>Pode ter filhos (mostra o botão de expandir mesmo antes de carregar).</summary>
    public bool CanExpand => Kind is TreeNodeKind.Session or TreeNodeKind.Database
        || (Kind == TreeNodeKind.Group && Children is not { Count: 0 })
        || (MayHaveChildren && Children is not { Count: 0 })
        || (Kind == TreeNodeKind.Table && Children is { Count: > 0 });

    public IReadOnlyList<TreeNode>? Children { get; internal set; }

    public bool IsObject => Kind is TreeNodeKind.Table or TreeNodeKind.View or TreeNodeKind.Procedure
        or TreeNodeKind.Function or TreeNodeKind.Trigger or TreeNodeKind.Event;

    /// <summary>Base a que o nó pertence (o próprio nó, se for base).</summary>
    public TreeNode? Database
    {
        get
        {
            for (var n = this; n is not null; n = n.Parent)
            {
                if (n.Kind == TreeNodeKind.Database)
                {
                    return n;
                }
            }
            return null;
        }
    }

    /// <summary>Define os filhos já conhecidos (grupos montados com os objetos da base).</summary>
    public void SetChildren(IReadOnlyList<TreeNode> children) => Children = children;

    public override string ToString() => $"{Kind} {Name}";
}

/// <summary>Fornece os filhos de um nó (no produto: <c>SHOW DATABASES</c>, <c>SHOW TABLE STATUS</c>, ...).</summary>
public interface ITreeDataProvider
{
    Task<IReadOnlyList<TreeNode>> LoadChildrenAsync(TreeNode parent, CancellationToken cancellationToken);
}
