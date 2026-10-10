using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Eunomia.App.Settings;
using Eunomia.Infra.Settings;
using Eunomia.UI.Controls.Tree;
using static Eunomia.UI.Headless.Tests.RealServer;

namespace Eunomia.UI.Headless.Tests;

/// <summary>
/// Database tree connected to a real server (phase 4): F5 that keeps the state (FAM-T08), groups and columns (FAM-T01),
/// per-session favourites (FAM-T03), statistics in the background and states (appendix D A2, A8).
/// </summary>
public sealed class TreeSessionTests
{
    private static TreeNode Child(TreeNode parent, string name) => parent.Children!.Single(n => n.Name == name);

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task F5KeepsDatabasesOpenAndTheFocusedObject()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "No test server.");
        var db1 = NewDatabaseName("eunomia_t1_");
        var db2 = NewDatabaseName("eunomia_t2_");
        await ExecAsync(cs, $"CREATE DATABASE `{db1}`; CREATE DATABASE `{db2}`; CREATE TABLE `{db1}`.a (id INT); CREATE TABLE `{db1}`.b (id INT); CREATE TABLE `{db2}`.c (id INT);");
        try
        {
            var (window, _, session, connector, _) = CreateShell(cs, $"{db1};{db2}");
            Assert.True(await window.ConnectAsync(window, session, null));
            var model = window.Tree.Tree.Model!;
            var root = await WaitAsync(() => model.Roots.FirstOrDefault());
            await WaitAsync(() => root.Children?.Count == 2 ? "ok" : null);
            await model.ExpandAsync(Child(root, db1));
            await model.ExpandAsync(Child(root, db2));
            var oldDb1 = Child(root, db1);
            window.Tree.Tree.FocusedNode = Child(oldDb1, "b");
            await ExecAsync(cs, $"CREATE TABLE `{db1}`.z (id INT); DROP TABLE `{db2}`.c; CREATE TABLE `{db2}`.d (id INT);");

            window.Tree.Tree.Focus();
            window.KeyPress(Key.F5, RawInputModifiers.None, PhysicalKey.F5, null);

            var focused = await WaitAsync(() => window.Tree.Tree.FocusedNode is { Name: "b" } f && !ReferenceEquals(f.Parent, oldDb1) ? f : null);
            var newDb1 = Child(root, db1);
            Assert.Same(newDb1, focused.Parent);
            Assert.True(newDb1.IsExpanded);
            Assert.Contains(newDb1.Children!, n => n.Name == "z");
            var newDb2 = Child(root, db2);
            Assert.True(newDb2.IsExpanded);
            Assert.Equal(["d"], newDb2.Children!.Select(n => n.Name));
            Assert.Contains(focused, model.Visible);

            await connector.DisconnectAllAsync();
            window.Close();
        }
        finally
        {
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db1}`; DROP DATABASE IF EXISTS `{db2}`");
        }
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task GroupsColumnsAndNaturalOrder()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "No test server.");
        var db = NewDatabaseName("eunomia_tg_");
        await ExecAsync(cs, $"CREATE DATABASE `{db}`; CREATE TABLE `{db}`.t10 (id INT PRIMARY KEY, name VARCHAR(20), born DATE, UNIQUE KEY u (name)); " +
            $"CREATE TABLE `{db}`.t2 (id INT) ENGINE=MEMORY; CREATE VIEW `{db}`.v AS SELECT 1 AS x; CREATE PROCEDURE `{db}`.p() SELECT 1;");
        try
        {
            var settings = new AppSettings();
            settings.Tree.GroupObjects = true;
            settings.Display.ColumnsInTree = true;
            var (window, context, session, connector, _) = CreateShell(cs, db, settings);
            Assert.True(await window.ConnectAsync(window, session, null));
            var model = window.Tree.Tree.Model!;
            var root = await WaitAsync(() => model.Roots.FirstOrDefault());
            var dbNode = await WaitAsync(() => root.Children?.FirstOrDefault());
            await model.ExpandAsync(dbNode);

            Assert.Equal(["Tables (2)", "Views (1)", "Procedures (1)", "Functions (0)", "Triggers (0)", "Events (0)"], dbNode.Children!.Select(g => g.DisplayText));
            Assert.True(Child(dbNode, "Functions").IsGhosted);
            var tables = Child(dbNode, "Tables");
            Assert.Equal(["t2", "t10"], tables.Children!.Select(t => t.Name));
            // The engine (and the overlay) arrives in the second phase, after the names.
            await WaitAsync(() => Child(tables, "t2").EngineOverlay == "MEMORY" ? "ok" : null);
            var t10 = Child(tables, "t10");
            Assert.True(t10.CanExpand);
            Assert.False(Child(Child(dbNode, "Views"), "v").CanExpand);
            await model.ExpandAsync(tables);
            await model.ExpandAsync(t10);
            Assert.Equal(["id", "name", "born"], t10.Children!.Select(c => c.Name));
            Assert.Equal([ColumnKeyMarker.Primary, ColumnKeyMarker.Unique, ColumnKeyMarker.None], t10.Children!.Select(c => c.KeyMarker));
            Assert.Equal(4, t10.Children![2].TextCategory); // Temporal
            window.Tree.Tree.FocusedNode = t10;

            // Turning the groups off rebuilds the tree from the cache, with the kinds mixed in natural order.
            Assert.True(await context.Commands.ExecuteAsync("tree.groupObjects"));
            var flat = await WaitAsync(() => root.Children?.FirstOrDefault() is { } d && !ReferenceEquals(d, dbNode) && d.Children?.Count == 4 ? d : null);
            Assert.Equal(["p", "t2", "t10", "v"], flat.Children!.Select(n => n.Name));
            // The focus goes back to the same object (groups are transparent in the path).
            await WaitAsync(() => ReferenceEquals(window.Tree.Tree.FocusedNode, Child(flat, "t10")) ? "ok" : null);
            Assert.False(settings.Tree.GroupObjects);

            await connector.DisconnectAllAsync();
            window.Close();
        }
        finally
        {
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`");
        }
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task FavouritesStatisticsAndStates()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "No test server.");
        var db = NewDatabaseName("eunomia_tf_");
        await ExecAsync(cs, $"CREATE DATABASE `{db}`; CREATE TABLE `{db}`.t (id INT, v TEXT); INSERT INTO `{db}`.t VALUES (1, REPEAT('x', 5000)); CREATE TABLE `{db}`.u (id INT);");
        var dir = Path.Combine(Path.GetTempPath(), "eunomia-ui-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            using var appDb = new SqliteAppDatabase(Path.Combine(dir, "app.db"));
            var (window, _, session, connector, _) = CreateShell(cs, db, appDatabase: appDb);
            var key = session.Id.ToString("D");
            // Favourite imported from HeidiSQL (db||name format).
            appDb.SetFavorite(key, $"{db}||t", true);
            Assert.True(await window.ConnectAsync(window, session, null));
            var model = window.Tree.Tree.Model!;
            var root = await WaitAsync(() => model.Roots.FirstOrDefault());
            var dbNode = await WaitAsync(() => root.Children?.FirstOrDefault());
            Assert.True(dbNode.IsGhosted);

            await model.ExpandAsync(dbNode);
            Assert.False(dbNode.IsGhosted);
            var t = Child(dbNode, "t");
            var u = Child(dbNode, "u");
            Assert.True(t.IsFavorite);
            Assert.False(u.IsFavorite);
            Assert.True(t.IsGhosted);

            model.ToggleFavorite(u);
            model.ToggleFavorite(t);
            Assert.Equal([$"{db}||u"], appDb.GetFavorites(key));

            window.Tree.Tree.FocusedNode = t;
            Assert.False(t.IsGhosted);
            Assert.True(u.IsGhosted);

            // "Get full table status" is on by default: the sizes arrive after the names.
            await WaitAsync(() => t.SizeBytes > 0 && dbNode.SizeBytes is not null ? "ok" : null);
            Assert.Equal(dbNode.SizeBytes, t.SizeBytes + u.SizeBytes);
            Assert.Equal(dbNode.SizeBytes, root.SizeBytes);

            await connector.DisconnectAllAsync();
            window.Close();
        }
        finally
        {
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`");
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }
}
