namespace Eunomia.UI.Controls.Tree.Tests;

/// <summary>Refresh with state, paths, groups and Size column sums (phase 4; FAM-T01, T02, T08).</summary>
public sealed class TreeRefreshTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Session → databases → (optional "Tables" group) → tables with a size. The data may change between refreshes.</summary>
    private sealed class FakeProvider : ITreeDataProvider
    {
        public Dictionary<string, List<(string Name, long? Size)>> Databases { get; } = new(StringComparer.Ordinal)
        {
            ["a"] = [("t1", 100), ("t2", 300)],
            ["b"] = [("x", null)],
            ["c"] = [],
        };

        public bool Group { get; set; }

        public string? Failing { get; set; }

        public Task<IReadOnlyList<TreeNode>> LoadChildrenAsync(TreeNode parent, CancellationToken cancellationToken)
        {
            if (parent.Name == Failing)
            {
                throw new InvalidOperationException("failed " + parent.Name);
            }
            IReadOnlyList<TreeNode> result = parent.Kind switch
            {
                TreeNodeKind.Session => Databases.Keys.Select(d => new TreeNode(TreeNodeKind.Database, d, parent)).ToList(),
                TreeNodeKind.Database when Group => [GroupOf(parent)],
                TreeNodeKind.Database => Tables(parent),
                _ => [],
            };
            return Task.FromResult(result);
        }

        private TreeNode GroupOf(TreeNode database)
        {
            var group = new TreeNode(TreeNodeKind.Group, "Tables", database) { GroupOf = TreeNodeKind.Table };
            group.SetChildren(Tables(group, database.Name));
            return group;
        }

        private List<TreeNode> Tables(TreeNode parent, string? database = null) =>
            Databases[database ?? parent.Name].Select(t => new TreeNode(TreeNodeKind.Table, t.Name, parent) { SizeBytes = t.Size }).ToList();
    }

    private static (TreeModel Model, FakeProvider Provider, TreeNode Session) Create(bool group = false)
    {
        var provider = new FakeProvider { Group = group };
        var model = new TreeModel([new TreeNode(TreeNodeKind.Session, "s")], provider);
        return (model, provider, model.Roots[0]);
    }

    private static TreeNode Child(TreeNode parent, string name) => parent.Children!.Single(n => n.Name == name);

    [Fact]
    public async Task RefreshKeepsDatabasesAndGroupsOpenAndFindsTheFocusedOneAgain()
    {
        var (model, provider, session) = Create(group: true);
        await model.ExpandAsync(session, Ct);
        var a = Child(session, "a");
        await model.ExpandAsync(a, Ct);
        await model.ExpandAsync(Child(a, "Tables"), Ct);
        var path = TreeModel.PathOf(Child(Child(a, "Tables"), "t2"));
        provider.Databases.Remove("b");
        provider.Databases["d"] = [("added", 1)];

        await model.ReloadAsync(session, Ct);

        var a2 = Child(session, "a");
        Assert.NotSame(a, a2);
        Assert.True(a2.IsExpanded);
        Assert.True(Child(a2, "Tables").IsExpanded);
        Assert.DoesNotContain(session.Children!, n => n.Name == "b");
        Assert.False(Child(session, "d").IsExpanded);
        var focused = model.FindDeepest(path);
        Assert.Equal("t2", focused!.Name);
        Assert.Contains(focused, model.Visible);
    }

    [Fact]
    public async Task VanishedObjectReturnsTheClosestAncestor()
    {
        var (model, provider, session) = Create();
        await model.ExpandAsync(session, Ct);
        await model.ExpandAsync(Child(session, "a"), Ct);
        var path = TreeModel.PathOf(Child(Child(session, "a"), "t1"));
        provider.Databases["a"].RemoveAt(0);

        await model.ReloadAsync(session, Ct);

        Assert.Same(Child(session, "a"), model.FindDeepest(path));
    }

    [Fact]
    public async Task GroupsAreTransparentInThePath()
    {
        // The focused object is found again after turning "Group objects by type" on or off.
        var (model, provider, session) = Create();
        await model.ExpandAsync(session, Ct);
        await model.ExpandAsync(Child(session, "a"), Ct);
        var path = TreeModel.PathOf(Child(Child(session, "a"), "t2"));
        provider.Group = true;

        await model.ReloadAsync(session, Ct);
        var inGroup = model.FindDeepest(path)!;
        Assert.Equal(TreeNodeKind.Group, inGroup.Parent!.Kind);
        Assert.Equal("t2", inGroup.Name);
        Assert.DoesNotContain(inGroup, model.Visible);
        model.Reveal(inGroup);
        Assert.Contains(inGroup, model.Visible);

        provider.Group = false;
        await model.ReloadAsync(session, Ct);
        Assert.Equal(TreeNodeKind.Database, model.FindDeepest(TreeModel.PathOf(inGroup))!.Parent!.Kind);
    }

    [Fact]
    public async Task DatabaseFailingOnRefreshStaysClosedAndTheOthersReopen()
    {
        var (model, provider, session) = Create();
        Exception? failure = null;
        model.LoadFailed += (_, ex) => failure = ex;
        await model.ExpandAsync(session, Ct);
        await model.ExpandAsync(Child(session, "a"), Ct);
        await model.ExpandAsync(Child(session, "b"), Ct);
        provider.Failing = "a";

        await model.ReloadAsync(session, Ct);

        Assert.False(Child(session, "a").IsExpanded);
        Assert.True(Child(session, "b").IsExpanded);
        Assert.Contains("failed a", failure!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GroupShowsTheCount()
    {
        var (model, _, session) = Create(group: true);
        await model.ExpandAsync(session, Ct);
        await model.ExpandAsync(Child(session, "a"), Ct);
        await model.ExpandAsync(Child(session, "c"), Ct);

        Assert.Equal("Tables (2)", Child(Child(session, "a"), "Tables").DisplayText);
        var empty = Child(Child(session, "c"), "Tables");
        Assert.Equal("Tables (0)", empty.DisplayText);
        Assert.False(empty.CanExpand);
    }

    [Fact]
    public async Task SizeColumnSums()
    {
        var (model, provider, session) = Create(group: true);
        provider.Databases["b"] = [("x", 50)];
        await model.ExpandAsync(session, Ct);
        await model.ExpandAsync(Child(session, "a"), Ct);
        await model.ExpandAsync(Child(session, "b"), Ct);

        // Database: the sum of the objects, inside groups too. Groups have no size.
        Assert.Equal(400, Child(session, "a").SizeBytes);
        Assert.Null(Child(Child(session, "a"), "Tables").SizeBytes);
        // Session: only when every database has a size ("c" has not been loaded yet).
        Assert.Null(session.SizeBytes);

        var c = Child(session, "c");
        await model.ExpandAsync(c, Ct);
        Assert.Null(c.SizeBytes);
        c.ChildSizesKnown = true;
        TreeModel.UpdateSizes(c);
        Assert.Equal(0, c.SizeBytes);
        Assert.Equal(450, session.SizeBytes);
    }

    [Fact]
    public async Task CollapseAllClosesTheDescendantsButNotTheSession()
    {
        var (model, _, session) = Create();
        await model.ExpandAsync(session, Ct);
        await model.ExpandAsync(Child(session, "a"), Ct);

        model.CollapseDescendants(session);

        Assert.True(session.IsExpanded);
        Assert.False(Child(session, "a").IsExpanded);
        Assert.Equal(4, model.Visible.Count);
    }

    [Fact]
    public async Task StarNotifiesWhoeverSaves()
    {
        var (model, _, session) = Create();
        await model.ExpandAsync(session, Ct);
        await model.ExpandAsync(Child(session, "a"), Ct);
        var changed = new List<TreeNode>();
        model.FavoriteChanged += (_, n) => changed.Add(n);
        var t1 = Child(Child(session, "a"), "t1");

        model.ToggleFavorite(t1);
        model.ToggleFavorite(Child(session, "a"));

        Assert.Equal([t1], changed);
        Assert.True(t1.IsFavorite);
    }
}
