using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Eunomia.UI.Controls.Common.Accessibility;
using Eunomia.UI.Controls.Common.TreeList;

namespace Eunomia.UI.Features.QueryTab;

/// <summary>
/// Helpers panel to the right of the editor (appendix C §1.11): a "Filter ..." box above a two-column tree without a
/// header ("Main column" 64 and "Attributes" 100), with multi-selection. The filter shows the nodes whose text contains the
/// filter text (and their roots); open roots stay open when the content changes.
/// </summary>
public sealed class QueryHelpersView : DockPanel
{
    public QueryHelpersView()
    {
        Filter = new TextBox { PlaceholderText = "Filter ...", Margin = new Thickness(0, 0, 0, 2), VerticalContentAlignment = VerticalAlignment.Center }.Named("Filter helpers");
        Tree = new TreeListControl { ShowHeader = false, MultiSelect = true, GroupsFirst = false };
        Tree.Columns.Add(new TreeListColumn("Main column", 140));
        Tree.Columns.Add(new TreeListColumn("Attributes", 100));
        // The model's order (fixed sections, history from the most recent to the oldest).
        Tree.SortBy(-1, descending: false);
        SetDock(Filter, Dock.Top);
        Children.Add(Filter);
        Children.Add(Tree);
        Filter.TextChanged += (_, _) => ApplyFilter();
    }

    public TextBox Filter { get; }

    public TreeListControl Tree { get; }

    /// <summary>Swaps the content, keeping open the roots that were open (by text).</summary>
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

    /// <summary>"Columns in x" and "Columns" count as the same root.</summary>
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
