namespace Eunomia.UI.Controls.Graph.Layout;

/// <summary>Size of a node for the layout.</summary>
public readonly record struct LayoutSize(double Width, double Height);

/// <summary>Position (top-left corner) computed for a node.</summary>
public readonly record struct LayoutPoint(double X, double Y);

/// <summary>Directed edge for the layout: <see cref="From"/> stays to the left of <see cref="To"/> when possible.</summary>
public readonly record struct LayoutEdge(int From, int To);

/// <summary>
/// Graph layout (section 17.4): the implementation can be swapped (MSAGL, ELK) without touching the canvas. Runs off the UI
/// thread; cancellation is checked between phases.
/// </summary>
public interface IGraphLayout
{
    IReadOnlyList<LayoutPoint> Layout(IReadOnlyList<LayoutSize> nodes, IReadOnlyList<LayoutEdge> edges, CancellationToken cancellationToken);
}

/// <summary>
/// Left-to-right layered layout (Sugiyama; SV-09): per connected component, cycles broken by reversing the back edges of a
/// DFS, layers by longest path, dummy nodes on edges that skip layers, order within the layers by barycentre (sweeps in
/// both directions, keeping the order with the fewest crossings) and vertical positions by isotonic regression (each node
/// as close as possible to its neighbours, without overlaps and without changing the order). Components are packed on
/// shelves; nodes without relations go to a grid at the end. Deterministic: the same input gives the same positions.
/// </summary>
public sealed class LayeredLayout : IGraphLayout
{
    public double LayerGap { get; init; } = 90;

    public double NodeGap { get; init; } = 28;

    public double ComponentGap { get; init; } = 80;

    /// <summary>Crossing-reduction sweeps (each one goes down and up the layers).</summary>
    public int OrderingSweeps { get; init; } = 8;

    public IReadOnlyList<LayoutPoint> Layout(IReadOnlyList<LayoutSize> nodes, IReadOnlyList<LayoutEdge> edges, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);
        var result = new LayoutPoint[nodes.Count];
        if (nodes.Count == 0)
        {
            return result;
        }
        var adjacency = new List<int>[nodes.Count];
        for (var i = 0; i < nodes.Count; i++)
        {
            adjacency[i] = [];
        }
        var unique = new HashSet<(int, int)>();
        foreach (var e in edges)
        {
            if (e.From == e.To || e.From < 0 || e.To < 0 || e.From >= nodes.Count || e.To >= nodes.Count || !unique.Add((e.From, e.To)))
            {
                continue;
            }
            adjacency[e.From].Add(e.To);
            adjacency[e.To].Add(e.From);
        }
        var components = Components(adjacency);
        var placed = new List<(List<int> Nodes, double Width, double Height, Dictionary<int, LayoutPoint> Local)>();
        var singles = new List<int>();
        foreach (var component in components)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (component.Count == 1)
            {
                singles.Add(component[0]);
                continue;
            }
            var set = new HashSet<int>(component);
            var componentEdges = unique.Where(e => set.Contains(e.Item1)).Select(e => (e.Item1, e.Item2)).OrderBy(e => e.Item1).ThenBy(e => e.Item2).ToList();
            var local = LayoutComponent(component, componentEdges, nodes, cancellationToken);
            var width = component.Max(n => local[n].X + nodes[n].Width);
            var height = component.Max(n => local[n].Y + nodes[n].Height);
            placed.Add((component, width, height, local));
        }

        // Larger components first, on shelves as wide as the largest one or the square root of the total area.
        var totalArea = placed.Sum(p => p.Width * p.Height) + singles.Sum(n => (nodes[n].Width + NodeGap) * (nodes[n].Height + NodeGap));
        var shelfWidth = Math.Max(placed.Count > 0 ? placed.Max(p => p.Width) : 0, Math.Sqrt(totalArea) * 1.6);
        double x = 0, y = 0, shelfHeight = 0;
        foreach (var (component, width, height, local) in placed.OrderByDescending(p => p.Nodes.Count).ThenBy(p => p.Nodes.Min()))
        {
            if (x > 0 && x + width > shelfWidth)
            {
                x = 0;
                y += shelfHeight + ComponentGap;
                shelfHeight = 0;
            }
            foreach (var n in component)
            {
                result[n] = new LayoutPoint(x + local[n].X, y + local[n].Y);
            }
            x += width + ComponentGap;
            shelfHeight = Math.Max(shelfHeight, height);
        }
        if (singles.Count > 0)
        {
            x = 0;
            y += placed.Count > 0 ? shelfHeight + ComponentGap : 0;
            shelfHeight = 0;
            foreach (var n in singles.Order())
            {
                if (x > 0 && x + nodes[n].Width > shelfWidth)
                {
                    x = 0;
                    y += shelfHeight + NodeGap;
                    shelfHeight = 0;
                }
                result[n] = new LayoutPoint(x, y);
                x += nodes[n].Width + NodeGap;
                shelfHeight = Math.Max(shelfHeight, nodes[n].Height);
            }
        }
        return result;
    }

    private static List<List<int>> Components(List<int>[] adjacency)
    {
        var seen = new bool[adjacency.Length];
        var components = new List<List<int>>();
        for (var start = 0; start < adjacency.Length; start++)
        {
            if (seen[start])
            {
                continue;
            }
            var component = new List<int>();
            var stack = new Stack<int>();
            stack.Push(start);
            seen[start] = true;
            while (stack.Count > 0)
            {
                var v = stack.Pop();
                component.Add(v);
                foreach (var w in adjacency[v])
                {
                    if (!seen[w])
                    {
                        seen[w] = true;
                        stack.Push(w);
                    }
                }
            }
            component.Sort();
            components.Add(component);
        }
        return components;
    }

    /// <summary>One connected component; returns local positions (indexed by the original node).</summary>
    private Dictionary<int, LayoutPoint> LayoutComponent(List<int> component, List<(int From, int To)> edges, IReadOnlyList<LayoutSize> sizes, CancellationToken cancellationToken)
    {
        // Local indexes.
        var index = new Dictionary<int, int>();
        for (var i = 0; i < component.Count; i++)
        {
            index[component[i]] = i;
        }
        var n = component.Count;
        var outgoing = new List<int>[n];
        for (var i = 0; i < n; i++)
        {
            outgoing[i] = [];
        }
        foreach (var (from, to) in edges)
        {
            outgoing[index[from]].Add(index[to]);
        }

        // 1. Cycles: the back edges of a DFS (in index order) are reversed.
        var acyclic = RemoveCycles(outgoing);
        cancellationToken.ThrowIfCancellationRequested();

        // 2. Layers by longest path from the sources.
        var layer = new int[n];
        var indegree = new int[n];
        foreach (var list in acyclic)
        {
            foreach (var w in list)
            {
                indegree[w]++;
            }
        }
        var queue = new Queue<int>(Enumerable.Range(0, n).Where(v => indegree[v] == 0));
        while (queue.Count > 0)
        {
            var v = queue.Dequeue();
            foreach (var w in acyclic[v])
            {
                layer[w] = Math.Max(layer[w], layer[v] + 1);
                if (--indegree[w] == 0)
                {
                    queue.Enqueue(w);
                }
            }
        }
        // Sources pulled next to their children (a parent table of a single distant child does not stay in layer 0).
        var hasIncoming = new bool[n];
        foreach (var list in acyclic)
        {
            foreach (var w in list)
            {
                hasIncoming[w] = true;
            }
        }
        for (var v = 0; v < n; v++)
        {
            if (!hasIncoming[v] && acyclic[v].Count > 0)
            {
                layer[v] = acyclic[v].Min(w => layer[w]) - 1;
            }
        }

        // 3. Dummy nodes on edges that skip layers (they help ordering; they are not drawn).
        var nodeLayer = new List<int>(layer);
        var height = new List<double>(component.Select(c => sizes[c].Height));
        var width = new List<double>(component.Select(c => sizes[c].Width));
        var links = new List<(int From, int To)>();
        for (var v = 0; v < n; v++)
        {
            foreach (var w in acyclic[v])
            {
                var previous = v;
                for (var l = layer[v] + 1; l < layer[w]; l++)
                {
                    var dummy = nodeLayer.Count;
                    nodeLayer.Add(l);
                    height.Add(4);
                    width.Add(0);
                    links.Add((previous, dummy));
                    previous = dummy;
                }
                links.Add((previous, w));
            }
        }
        var total = nodeLayer.Count;
        var layerCount = nodeLayer.Max() + 1;
        var layers = new List<int>[layerCount];
        for (var l = 0; l < layerCount; l++)
        {
            layers[l] = [];
        }
        for (var v = 0; v < total; v++)
        {
            layers[nodeLayer[v]].Add(v);
        }
        var up = new List<int>[total];
        var down = new List<int>[total];
        for (var v = 0; v < total; v++)
        {
            up[v] = [];
            down[v] = [];
        }
        foreach (var (from, to) in links)
        {
            down[from].Add(to);
            up[to].Add(from);
        }

        // 4. Order within the layers: barycentres, going down and up; the best order seen is kept.
        var position = new double[total];
        void Renumber()
        {
            foreach (var list in layers)
            {
                for (var i = 0; i < list.Count; i++)
                {
                    position[list[i]] = i;
                }
            }
        }
        Renumber();
        var best = layers.Select(l => l.ToList()).ToArray();
        var bestCrossings = Crossings(layers, down, position);
        for (var sweep = 0; sweep < OrderingSweeps && bestCrossings > 0; sweep++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var l = 1; l < layerCount; l++)
            {
                Reorder(layers[l], up, position);
            }
            for (var l = layerCount - 2; l >= 0; l--)
            {
                Reorder(layers[l], down, position);
            }
            var crossings = Crossings(layers, down, position);
            if (crossings < bestCrossings)
            {
                bestCrossings = crossings;
                best = layers.Select(list => list.ToList()).ToArray();
            }
        }
        layers = best;
        Renumber();

        // 5. Coordinates: x by layer width; y aligned with the neighbours without overlaps.
        var layerX = new double[layerCount];
        for (var l = 1; l < layerCount; l++)
        {
            var previousWidth = layers[l - 1].Count == 0 ? 0 : layers[l - 1].Max(v => width[v]);
            layerX[l] = layerX[l - 1] + previousWidth + LayerGap;
        }
        var center = new double[total];
        foreach (var list in layers)
        {
            var y = 0.0;
            foreach (var v in list)
            {
                center[v] = y + (height[v] / 2);
                y += height[v] + NodeGap;
            }
        }
        for (var iteration = 0; iteration < 6; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var l = 1; l < layerCount; l++)
            {
                Place(layers[l], up, center, height);
            }
            for (var l = layerCount - 2; l >= 0; l--)
            {
                Place(layers[l], down, center, height);
            }
        }
        var minY = Enumerable.Range(0, total).Min(v => center[v] - (height[v] / 2));
        var result = new Dictionary<int, LayoutPoint>();
        for (var v = 0; v < n; v++)
        {
            result[component[v]] = new LayoutPoint(layerX[nodeLayer[v]], center[v] - (height[v] / 2) - minY);
        }
        return result;
    }

    private static List<int>[] RemoveCycles(List<int>[] outgoing)
    {
        var n = outgoing.Length;
        var state = new byte[n];
        var result = new List<int>[n];
        for (var i = 0; i < n; i++)
        {
            result[i] = [];
        }
        for (var start = 0; start < n; start++)
        {
            if (state[start] != 0)
            {
                continue;
            }
            // Iterative DFS: 1 = on the stack, 2 = finished.
            var stack = new Stack<(int Node, int Next)>();
            stack.Push((start, 0));
            state[start] = 1;
            while (stack.Count > 0)
            {
                var (v, next) = stack.Pop();
                if (next < outgoing[v].Count)
                {
                    stack.Push((v, next + 1));
                    var w = outgoing[v][next];
                    if (state[w] == 1)
                    {
                        // Back edge: reverse it.
                        if (!result[w].Contains(v))
                        {
                            result[w].Add(v);
                        }
                    }
                    else
                    {
                        if (!result[v].Contains(w))
                        {
                            result[v].Add(w);
                        }
                        if (state[w] == 0)
                        {
                            state[w] = 1;
                            stack.Push((w, 0));
                        }
                    }
                }
                else
                {
                    state[v] = 2;
                }
            }
        }
        return result;
    }

    /// <summary>Sorts a layer by the barycentre of its neighbours in the fixed layer (without neighbours, keeps its place).</summary>
    private static void Reorder(List<int> layer, List<int>[] neighbours, double[] position)
    {
        var keys = new Dictionary<int, double>();
        foreach (var v in layer)
        {
            keys[v] = neighbours[v].Count > 0 ? neighbours[v].Average(w => position[w]) : position[v];
        }
        var order = layer.Select((v, i) => (v, i)).OrderBy(t => keys[t.v]).ThenBy(t => t.i).Select(t => t.v).ToList();
        layer.Clear();
        layer.AddRange(order);
        for (var i = 0; i < layer.Count; i++)
        {
            position[layer[i]] = i;
        }
    }

    /// <summary>Crossings between neighbouring layers (inversions, counted by merge sort).</summary>
    private static long Crossings(List<int>[] layers, List<int>[] down, double[] position)
    {
        long total = 0;
        for (var l = 0; l + 1 < layers.Length; l++)
        {
            var targets = new List<int>();
            foreach (var v in layers[l])
            {
                targets.AddRange(down[v].Select(w => (int)position[w]).Order());
            }
            total += Inversions(targets.ToArray());
        }
        return total;
    }

    private static long Inversions(int[] values)
    {
        if (values.Length < 2)
        {
            return 0;
        }
        var buffer = new int[values.Length];
        return Sort(values, buffer, 0, values.Length);

        static long Sort(int[] a, int[] tmp, int lo, int hi)
        {
            if (hi - lo < 2)
            {
                return 0;
            }
            var mid = (lo + hi) / 2;
            var count = Sort(a, tmp, lo, mid) + Sort(a, tmp, mid, hi);
            int i = lo, j = mid, k = lo;
            while (i < mid && j < hi)
            {
                if (a[i] <= a[j])
                {
                    tmp[k++] = a[i++];
                }
                else
                {
                    count += mid - i;
                    tmp[k++] = a[j++];
                }
            }
            while (i < mid)
            {
                tmp[k++] = a[i++];
            }
            while (j < hi)
            {
                tmp[k++] = a[j++];
            }
            Array.Copy(tmp, lo, a, lo, hi - lo);
            return count;
        }
    }

    /// <summary>
    /// Centres of a layer: each node wants the mean centre of its neighbours (otherwise it stays where it is); the order and the
    /// minimum distances are kept, with the minimum sum of squared deviations (isotonic regression, pool adjacent violators).
    /// </summary>
    private void Place(List<int> layer, List<int>[] neighbours, double[] center, List<double> height)
    {
        var count = layer.Count;
        if (count == 0)
        {
            return;
        }
        // With each node's minimum offset s_i from the first, z_i = y_i - s_i must be non-decreasing.
        var offset = new double[count];
        for (var i = 1; i < count; i++)
        {
            offset[i] = offset[i - 1] + ((height[layer[i - 1]] + height[layer[i]]) / 2) + NodeGap;
        }
        var target = new double[count];
        for (var i = 0; i < count; i++)
        {
            var v = layer[i];
            var desired = neighbours[v].Count > 0 ? neighbours[v].Average(w => center[w]) : center[v];
            target[i] = desired - offset[i];
        }
        // Blocks (mean, weight, end).
        var means = new List<double>();
        var weights = new List<int>();
        var ends = new List<int>();
        for (var i = 0; i < count; i++)
        {
            means.Add(target[i]);
            weights.Add(1);
            ends.Add(i);
            while (means.Count > 1 && means[^2] > means[^1])
            {
                var w = weights[^2] + weights[^1];
                var m = ((means[^2] * weights[^2]) + (means[^1] * weights[^1])) / w;
                means.RemoveAt(means.Count - 1);
                weights.RemoveAt(weights.Count - 1);
                ends.RemoveAt(ends.Count - 2);
                means[^1] = m;
                weights[^1] = w;
            }
        }
        var start = 0;
        for (var b = 0; b < means.Count; b++)
        {
            for (var i = start; i <= ends[b]; i++)
            {
                center[layer[i]] = means[b] + offset[i];
            }
            start = ends[b] + 1;
        }
    }
}
