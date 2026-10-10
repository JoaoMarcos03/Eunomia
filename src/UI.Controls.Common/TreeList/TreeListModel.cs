using Avalonia.Media;

namespace Eunomia.UI.Controls.Common.TreeList;

/// <summary>Coluna de uma <see cref="TreeListControl"/> (como as colunas do VirtualStringTree do HeidiSQL).</summary>
public sealed class TreeListColumn(string header, double width, TextAlignment alignment = TextAlignment.Left)
{
    public string Header { get; } = header;

    public double Width { get; set; } = width;

    public double MinWidth { get; set; } = 24;

    public bool Visible { get; set; } = true;

    public TextAlignment Alignment { get; } = alignment;

    /// <summary>Comparação para ordenar (por omissão: texto natural sem maiúsculas; números se ambos o forem).</summary>
    public Comparison<string>? Compare { get; init; }
}

/// <summary>Nó de uma <see cref="TreeListControl"/>: texto por coluna, ícone, cores e filhos.</summary>
public sealed class TreeListNode
{
    private readonly List<TreeListNode> _children = [];

    public TreeListNode(params string[] cells)
    {
        Cells = cells;
    }

    /// <summary>Texto por coluna (falta = vazio).</summary>
    public string[] Cells { get; set; }

    /// <summary>Chave do <see cref="Icons.IconSet"/>.</summary>
    public string? Icon { get; set; }

    /// <summary>Cor de fundo da linha inteira 0xRRGGBB (cor de sessão); <c>null</c> = sem cor.</summary>
    public uint? Background { get; set; }

    public bool Bold { get; set; }

    /// <summary>Texto esbatido (sessões inativas, objetos sem cache).</summary>
    public bool Muted { get; set; }

    /// <summary>Folder/grupo: fica no topo quando "folders at top" está ligado.</summary>
    public bool IsGroup { get; set; }

    public bool IsExpanded { get; set; }

    /// <summary>Barra proporcional (0–1) desenhada atrás do texto, por coluna (colunas Size e Time das listas do HeidiSQL).</summary>
    public Dictionary<int, double>? Bars { get; set; }

    /// <summary>Células destacadas (Host tab › Variables: valor da sessão diferente do global).</summary>
    public HashSet<int>? Highlighted { get; set; }

    /// <summary>Valor de ordenação por coluna (tamanhos, números, datas); sem valor, ordena pelo texto da célula.</summary>
    public Dictionary<int, IComparable>? SortKeys { get; set; }

    public object? Tag { get; set; }

    public TreeListNode? Parent { get; private set; }

    public IReadOnlyList<TreeListNode> Children => _children;

    public int Depth => Parent is null ? 0 : Parent.Depth + 1;

    public string this[int column] => column < Cells.Length ? Cells[column] : string.Empty;

    public TreeListNode Add(TreeListNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        child.Parent = this;
        _children.Add(child);
        return child;
    }

    public void Clear()
    {
        foreach (var c in _children)
        {
            c.Parent = null;
        }
        _children.Clear();
    }

    internal void Sort(Comparison<TreeListNode> comparison)
    {
        _children.Sort(comparison);
        foreach (var c in _children)
        {
            c.Sort(comparison);
        }
    }

    public bool IsAncestorOf(TreeListNode node)
    {
        for (var p = node.Parent; p is not null; p = p.Parent)
        {
            if (ReferenceEquals(p, this))
            {
                return true;
            }
        }
        return false;
    }

    public IEnumerable<TreeListNode> Descendants()
    {
        foreach (var c in _children)
        {
            yield return c;
            foreach (var d in c.Descendants())
            {
                yield return d;
            }
        }
    }
}

/// <summary>Pedido de drag &amp; drop: <see cref="Target"/> é o nó onde se largou (<c>null</c> = raiz).</summary>
public sealed class TreeListDropEventArgs(TreeListNode source, TreeListNode? target) : EventArgs
{
    public TreeListNode Source { get; } = source;

    public TreeListNode? Target { get; } = target;
}

/// <summary>Rename in-place: o handler valida e devolve a mensagem de erro em <see cref="Error"/> (ou deixa <c>null</c>).</summary>
public sealed class TreeListRenameEventArgs(TreeListNode node, string text) : EventArgs
{
    public TreeListNode Node { get; } = node;

    public string Text { get; } = text;

    public string? Error { get; set; }
}
