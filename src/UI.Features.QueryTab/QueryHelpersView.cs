using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Eunomia.UI.Controls.Common.Accessibility;
using Eunomia.UI.Controls.Common.TreeList;

namespace Eunomia.UI.Features.QueryTab;

/// <summary>
/// Painel de helpers à direita do editor (anexo C §1.11): caixa "Filter ..." por cima de uma árvore de duas colunas sem
/// cabeçalho ("Main column" 64 e "Attributes" 100), com multi-seleção. O filtro mostra os nós cujo texto contém o
/// filtrado (e as raízes deles); as raízes abertas mantêm-se quando o conteúdo muda.
/// </summary>
public sealed class QueryHelpersView : DockPanel
{
    public QueryHelpersView()
    {
        Filter = new TextBox { PlaceholderText = "Filter ...", Margin = new Thickness(0, 0, 0, 2), VerticalContentAlignment = VerticalAlignment.Center }.Named("Filter helpers");
        Tree = new TreeListControl { ShowHeader = false, MultiSelect = true, GroupsFirst = false };
        Tree.Columns.Add(new TreeListColumn("Main column", 140));
        Tree.Columns.Add(new TreeListColumn("Attributes", 100));
        // A ordem é a do modelo (secções fixas, histórico do mais recente para o mais antigo).
        Tree.SortBy(-1, descending: false);
        SetDock(Filter, Dock.Top);
        Children.Add(Filter);
        Children.Add(Tree);
        Filter.TextChanged += (_, _) => ApplyFilter();
    }

    public TextBox Filter { get; }

    public TreeListControl Tree { get; }

    /// <summary>Troca o conteúdo, mantendo abertas as raízes que estavam abertas (pelo texto).</summary>
    public void SetRoots(IReadOnlyList<TreeListNode> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var expanded = Tree.Roots.Where(r => r.IsExpanded).Select(r => Key(r)).ToHashSet(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            root.IsExpanded = expanded.Contains(Key(root));
        }
        Tree.SetRoots(roots);
        ApplyFilter();
    }

    /// <summary>"Columns in x" e "Columns" contam como a mesma raiz.</summary>
    private static string Key(TreeListNode root) => root[0].StartsWith("Columns", StringComparison.Ordinal) ? "Columns" : root[0];

    private void ApplyFilter()
    {
        var text = Filter.Text?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            Tree.SetFilter(null);
            return;
        }
        Tree.SetFilter(node => node.Depth == 0 || Matches(node, text));
        foreach (var root in Tree.Roots.Where(r => r.Children.Any(c => Matches(c, text))))
        {
            Tree.Expand(root);
        }
    }

    private static bool Matches(TreeListNode node, string text) =>
        node[0].Contains(text, StringComparison.OrdinalIgnoreCase) || node.Children.Any(c => Matches(c, text));
}
