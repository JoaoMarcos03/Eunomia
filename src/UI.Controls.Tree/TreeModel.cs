using Eunomia.Engine.Sql.Filters;

namespace Eunomia.UI.Controls.Tree;

/// <summary>
/// Modelo da árvore de bases: nós lazy, lista achatada de nós visíveis (secção 15.3) e a semântica
/// do Database filter e do Table filter (secção 4.3):
/// <list type="bullet">
/// <item>o Database filter só se aplica a nós de base; esconder uma base esconde os filhos;</item>
/// <item>o Table filter só se aplica a tabelas, views, procedures, functions, triggers e events;
/// sessões, grupos e colunas nunca são escondidos, e bases sem objetos que correspondam continuam visíveis;</item>
/// <item>com "Show only favorites", um objeto só aparece se corresponder ao Table filter e for favorito;</item>
/// <item>os filtros atuam em todas as sessões e em nós carregados depois (ao expandir).</item>
/// </list>
/// Não é thread-safe: usar na UI thread.
/// </summary>
public sealed class TreeModel
{
    public const string PlaceholderText = "Loading…";

    private readonly List<TreeNode> _roots;
    private readonly ITreeDataProvider _provider;
    private readonly List<TreeNode> _visible = [];
    private readonly Dictionary<TreeNode, Task> _loading = [];
    private int _filterVersion;

    public TreeModel(IEnumerable<TreeNode> roots, ITreeDataProvider provider)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(provider);
        _roots = [.. roots];
        _provider = provider;
        Rebuild();
    }

    public IReadOnlyList<TreeNode> Roots => _roots;

    /// <summary>Acrescenta uma raiz (sessão aberta, FAM-S07).</summary>
    public void AddRoot(TreeNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        _roots.Add(root);
        Rebuild();
    }

    /// <summary>Retira uma raiz (Disconnect).</summary>
    public void RemoveRoot(TreeNode root)
    {
        if (_roots.Remove(root))
        {
            Rebuild();
        }
    }

    /// <summary>
    /// Refresh (F5, anexo D A9): esquece os filhos carregados do nó e volta a pedi-los se estava expandido, voltando a
    /// expandir os descendentes que estavam expandidos (bases, grupos, tabelas com colunas) e que ainda existem. Um
    /// descendente que falhe a carregar fica fechado e vai para <see cref="LoadFailed"/>. Devolve quando acabar.
    /// </summary>
    public async Task ReloadAsync(TreeNode node, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        CollectExpanded(node, string.Empty, expanded);
        var wasExpanded = node.IsExpanded;
        node.Children = null;
        node.IsExpanded = false;
        Rebuild();
        if (wasExpanded)
        {
            await ExpandAsync(node, cancellationToken).ConfigureAwait(true);
            await RestoreExpandedAsync(node, string.Empty, expanded, cancellationToken).ConfigureAwait(true);
        }
    }

    private static string Segment(TreeNode node) => ((int)node.Kind).ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + node.Name;

    private static void CollectExpanded(TreeNode node, string key, HashSet<string> expanded)
    {
        if (!node.IsExpanded || node.Children is null)
        {
            return;
        }
        foreach (var child in node.Children)
        {
            if (child.IsExpanded)
            {
                var childKey = key + "/" + Segment(child);
                expanded.Add(childKey);
                CollectExpanded(child, childKey, expanded);
            }
        }
    }

    private async Task RestoreExpandedAsync(TreeNode node, string key, HashSet<string> expanded, CancellationToken cancellationToken)
    {
        if (node.Children is null)
        {
            return;
        }
        foreach (var child in node.Children.ToArray())
        {
            var childKey = key + "/" + Segment(child);
            if (!expanded.Contains(childKey))
            {
                continue;
            }
            try
            {
                await ExpandAsync(child, cancellationToken).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                LoadFailed?.Invoke(this, ex);
                continue;
            }
            await RestoreExpandedAsync(child, childKey, expanded, cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>Caminho de um nó desde a raiz (tipo e nome de cada nível), para o encontrar depois de um refresh.</summary>
    public static IReadOnlyList<(TreeNodeKind Kind, string Name)> PathOf(TreeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var path = new List<(TreeNodeKind, string)>();
        for (var n = node; n is not null; n = n.Parent)
        {
            path.Insert(0, (n.Kind, n.Name));
        }
        return path;
    }

    /// <summary>
    /// O nó carregado mais fundo ao longo de <paramref name="path"/>. Os grupos são transparentes (o objeto é
    /// encontrado com e sem "Group objects by type"). <c>null</c> se nem a raiz existir.
    /// </summary>
    public TreeNode? FindDeepest(IReadOnlyList<(TreeNodeKind Kind, string Name)> path)
    {
        ArgumentNullException.ThrowIfNull(path);
        IReadOnlyList<TreeNode>? level = _roots;
        TreeNode? found = null;
        foreach (var (kind, name) in path)
        {
            if (kind == TreeNodeKind.Group)
            {
                continue;
            }
            var next = FindChild(level, kind, name);
            if (next is null)
            {
                break;
            }
            found = next;
            level = next.Children;
            if (level is null)
            {
                break;
            }
        }
        return found;
    }

    private static TreeNode? FindChild(IReadOnlyList<TreeNode>? nodes, TreeNodeKind kind, string name)
    {
        if (nodes is null)
        {
            return null;
        }
        foreach (var node in nodes)
        {
            if (node.Kind == kind && string.Equals(node.Name, name, StringComparison.Ordinal))
            {
                return node;
            }
            if (node.Kind == TreeNodeKind.Group && FindChild(node.Children, kind, name) is { } inside)
            {
                return inside;
            }
        }
        return null;
    }

    /// <summary>Expande os antepassados já carregados de um nó, para ele ficar visível.</summary>
    public void Reveal(TreeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var changed = false;
        for (var n = node.Parent; n is not null; n = n.Parent)
        {
            if (!n.IsExpanded && n.Children is not null)
            {
                n.IsExpanded = true;
                changed = true;
            }
        }
        if (changed)
        {
            Rebuild();
        }
    }

    /// <summary>Collapse all (anexo D A10): fecha todos os descendentes do nó; o próprio nó fica aberto.</summary>
    public void CollapseDescendants(TreeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        static void Close(TreeNode n)
        {
            if (n.Children is null)
            {
                return;
            }
            foreach (var child in n.Children)
            {
                child.IsExpanded = false;
                Close(child);
            }
        }
        Close(node);
        Rebuild();
    }

    /// <summary>Volta a calcular a lista de visíveis e avisa a vista (nós mudados por fora: tamanhos, estados).</summary>
    public void Refresh()
    {
        _filterVersion++;
        Rebuild();
    }

    /// <summary>Um descendente falhou a carregar durante um refresh (vai para o SQL log).</summary>
    public event EventHandler<Exception>? LoadFailed;

    /// <summary>A estrela de um objeto mudou (para gravar os favoritos da sessão).</summary>
    public event EventHandler<TreeNode>? FavoriteChanged;

    /// <summary>Nós visíveis por ordem de apresentação (só dentro de nós expandidos).</summary>
    public IReadOnlyList<TreeNode> Visible => _visible;

    public TreeFilterExpression DatabaseFilter { get; private set; } = TreeFilterExpression.Empty;

    public TreeFilterExpression TableFilter { get; private set; } = TreeFilterExpression.Empty;

    public bool OnlyFavorites { get; private set; }

    /// <summary>A lista de visíveis mudou.</summary>
    public event EventHandler? Changed;

    /// <summary>Uma regex nova é inválida. Disparado uma vez por texto (vai para o SQL log).</summary>
    public event EventHandler<string>? FilterError;

    public void SetDatabaseFilter(string text)
    {
        if (text == DatabaseFilter.Text)
        {
            return;
        }
        DatabaseFilter = new TreeFilterExpression(text);
        ReportError(DatabaseFilter);
        _filterVersion++;
        Rebuild();
    }

    public void SetTableFilter(string text)
    {
        if (text == TableFilter.Text)
        {
            return;
        }
        TableFilter = new TreeFilterExpression(text);
        ReportError(TableFilter);
        _filterVersion++;
        Rebuild();
    }

    public void SetOnlyFavorites(bool value)
    {
        if (value == OnlyFavorites)
        {
            return;
        }
        OnlyFavorites = value;
        _filterVersion++;
        Rebuild();
    }

    /// <summary>Liga/desliga a estrela de um objeto.</summary>
    public void ToggleFavorite(TreeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!node.IsObject)
        {
            return;
        }
        node.IsFavorite = !node.IsFavorite;
        FavoriteChanged?.Invoke(this, node);
        if (OnlyFavorites)
        {
            _filterVersion++;
            Rebuild();
        }
        else
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Indica se o nó passa nos filtros, incluindo os antepassados.</summary>
    public bool IsVisible(TreeNode node)
    {
        for (var n = node; n is not null; n = n.Parent)
        {
            if (!PassesOwnFilter(n))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Expande o nó, carregando os filhos na primeira vez (com placeholder enquanto carrega).</summary>
    public async Task ExpandAsync(TreeNode node, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!node.CanExpand)
        {
            return;
        }
        node.IsExpanded = true;
        if (_loading.TryGetValue(node, out var pending))
        {
            // Já está a carregar (outro expand ou o ExpandAll): espera pelo mesmo pedido.
            await pending.ConfigureAwait(true);
        }
        else if (node.Children is null)
        {
            var load = LoadAsync(node, cancellationToken);
            _loading[node] = load;
            try
            {
                await load.ConfigureAwait(true);
            }
            finally
            {
                _loading.Remove(node);
            }
        }
        Rebuild();
    }

    private async Task LoadAsync(TreeNode node, CancellationToken cancellationToken)
    {
        node.IsLoading = true;
        node.Children = [new TreeNode(TreeNodeKind.Placeholder, PlaceholderText, node)];
        Rebuild();
        try
        {
            node.Children = await _provider.LoadChildrenAsync(node, cancellationToken).ConfigureAwait(true);
            UpdateSizes(node);
        }
        catch
        {
            node.Children = null;
            node.IsExpanded = false;
            Rebuild();
            throw;
        }
        finally
        {
            node.IsLoading = false;
        }
    }

    public void Collapse(TreeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!node.IsExpanded)
        {
            return;
        }
        node.IsExpanded = false;
        Rebuild();
    }

    /// <summary>Expande o nó e todos os descendentes até aos objetos (carrega todas as bases: caro, como no HeidiSQL).</summary>
    public async Task ExpandAllAsync(TreeNode node, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        await ExpandAsync(node, cancellationToken).ConfigureAwait(true);
        if (node.Children is null)
        {
            return;
        }
        foreach (var child in node.Children.ToArray())
        {
            if (child.Kind is TreeNodeKind.Session or TreeNodeKind.Database or TreeNodeKind.Group)
            {
                await ExpandAllAsync(child, cancellationToken).ConfigureAwait(true);
            }
        }
    }

    /// <summary>
    /// Pesquisa incremental case-insensitive: primeiro nó visível a partir de <paramref name="start"/>
    /// (inclusive, com volta ao início) cujo nome começa por <paramref name="prefix"/>; -1 se não houver.
    /// </summary>
    public int FindByPrefix(string prefix, int start)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        var count = _visible.Count;
        for (var k = 0; k < count; k++)
        {
            var i = (Math.Max(0, start) + k) % count;
            if (_visible[i].Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    public int IndexOf(TreeNode node) => _visible.IndexOf(node);

    private bool PassesOwnFilter(TreeNode node)
    {
        if (node.MatchVersion == _filterVersion)
        {
            return node.MatchResult;
        }
        var result = node.Kind switch
        {
            TreeNodeKind.Database => DatabaseFilter.IsMatch(node.Name),
            _ when node.IsObject => TableFilter.IsMatch(node.Name) && (!OnlyFavorites || node.IsFavorite),
            _ => true,
        };
        node.MatchVersion = _filterVersion;
        node.MatchResult = result;
        return result;
    }

    private void Rebuild()
    {
        _visible.Clear();
        foreach (var root in _roots)
        {
            Visit(root);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Visit(TreeNode node)
    {
        if (!PassesOwnFilter(node))
        {
            return;
        }
        _visible.Add(node);
        if (node.IsExpanded && node.Children is not null)
        {
            foreach (var child in node.Children)
            {
                Visit(child);
            }
        }
    }

    private void ReportError(TreeFilterExpression expression)
    {
        if (expression.Error is not null)
        {
            FilterError?.Invoke(this, expression.Error);
        }
    }

    /// <summary>
    /// Somas da coluna Size (anexo D A8) do nó para cima. Base: soma dos objetos (também dentro de grupos), em branco
    /// enquanto nenhum tiver tamanho e os tamanhos não forem conhecidos. Sessão: soma das bases, só se todas tiverem
    /// tamanho. Grupos não têm tamanho.
    /// </summary>
    public static void UpdateSizes(TreeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        for (var n = node; n is not null; n = n.Parent)
        {
            if (n.Children is null)
            {
                continue;
            }
            if (n.Kind == TreeNodeKind.Database)
            {
                long sum = 0;
                var any = n.ChildSizesKnown;
                foreach (var obj in ObjectsOf(n))
                {
                    if (obj.SizeBytes is { } size)
                    {
                        sum += size;
                        any = true;
                    }
                }
                n.SizeBytes = any ? sum : null;
            }
            else if (n.Kind == TreeNodeKind.Session)
            {
                long sum = 0;
                var complete = n.Children.Count > 0;
                foreach (var db in n.Children)
                {
                    if (db.SizeBytes is { } size)
                    {
                        sum += size;
                    }
                    else
                    {
                        complete = false;
                    }
                }
                n.SizeBytes = complete ? sum : null;
            }
        }
    }

    /// <summary>Objetos carregados de uma base, dentro ou fora de grupos.</summary>
    public static IEnumerable<TreeNode> ObjectsOf(TreeNode database)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (database.Children is null)
        {
            yield break;
        }
        foreach (var child in database.Children)
        {
            if (child.IsObject)
            {
                yield return child;
            }
            else if (child.Kind == TreeNodeKind.Group && child.Children is not null)
            {
                foreach (var obj in child.Children)
                {
                    yield return obj;
                }
            }
        }
    }
}
