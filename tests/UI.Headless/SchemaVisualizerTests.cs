using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Eunomia.App.Diagram;
using Eunomia.Drivers.Abstractions;
using Eunomia.Infra.Settings;
using Eunomia.UI.Controls.Tree;
using Eunomia.UI.Features.SchemaVisualizer;
using static Eunomia.UI.Headless.Tests.RealServer;
using ColumnDefinition = Eunomia.Drivers.Abstractions.ColumnDefinition;

namespace Eunomia.UI.Headless.Tests;

/// <summary>Schema Visualizer in the shell (phase 9; section 17; SV-06, SV-10, SV-12, SV-14–SV-16).</summary>
public sealed class SchemaVisualizerTests
{
    private static ColumnDefinition Column(string name, string type, LogicalTypeKind kind, bool nullable = false) =>
        new(name, type, new LogicalType(kind), nullable, null, false, false, false, null);

    private static DbObjectRef Ref(string name) => new("shop", "shop", name, DbObjectKind.Table);

    private static SchemaDiagram Shop() => SchemaDiagram.From("shop",
    [
        new TableDefinition(Ref("customers"), [Column("id", "int(10) unsigned", LogicalTypeKind.Integer), Column("name", "varchar(120)", LogicalTypeKind.Text), Column("email", "varchar(200)", LogicalTypeKind.Text, true)],
            [new IndexDefinition("PRIMARY", IndexKind.Primary, ["id"]), new IndexDefinition("uq_email", IndexKind.Unique, ["email"])], [], [], new Dictionary<string, string>()),
        new TableDefinition(Ref("orders"), [Column("id", "int(10) unsigned", LogicalTypeKind.Integer), Column("customer_id", "int(10) unsigned", LogicalTypeKind.Integer), Column("created", "datetime", LogicalTypeKind.DateTime), Column("total", "decimal(10,2)", LogicalTypeKind.Decimal)],
            [new IndexDefinition("PRIMARY", IndexKind.Primary, ["id"])],
            [new ForeignKeyDefinition("fk_customer", ["customer_id"], Ref("customers"), ["id"], ForeignKeyAction.Restrict, ForeignKeyAction.Restrict)], [], new Dictionary<string, string>()),
        new TableDefinition(Ref("profiles"), [Column("customer_id", "int(10) unsigned", LogicalTypeKind.Integer), Column("bio", "text", LogicalTypeKind.Text, true)],
            [new IndexDefinition("PRIMARY", IndexKind.Primary, ["customer_id"])],
            [new ForeignKeyDefinition("fk_profile", ["customer_id"], Ref("customers"), ["id"], ForeignKeyAction.Restrict, ForeignKeyAction.Restrict)], [], new Dictionary<string, string>()),
        new TableDefinition(Ref("audit"), [Column("id", "bigint", LogicalTypeKind.Integer), Column("text", "text", LogicalTypeKind.Text, true)],
            [new IndexDefinition("PRIMARY", IndexKind.Primary, ["id"])], [], [], new Dictionary<string, string>()),
    ]);

    [AvaloniaTheory(Timeout = UiTimeouts.Default)]
    [InlineData("schema-visualizer-light", "light")]
    [InlineData("schema-visualizer-dark", "dark")]
    public async Task ViewWithBarAndSearch(string name, string theme)
    {
        using var culture = new CultureScope(CultureInfo.InvariantCulture);
        var view = new SchemaVisualizerView("shop");
        var window = new Window { Width = 1000, Height = 520, Content = view, RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light };
        window.Show();
        await view.ShowAsync(Shop(), null);
        await view.Layouting;
        view.SearchBox.Text = "cust";
        window.CaptureRenderedFrame();
        view.Canvas.FitToView();
        Assert.Equal("4 tables, 2 relations — 3 matching", view.StatusText.Text);
        Snapshot.Match(window.CaptureRenderedFrame(), name);
        window.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task LargeSchemaOpensOnlyTheComponentAndNewTablesLeaveTheOthersAlone()
    {
        var tables = new List<TableDefinition>();
        for (var i = 0; i < 400; i++)
        {
            List<ForeignKeyDefinition> fks = i is > 0 and < 50 ? [new ForeignKeyDefinition("fk" + i, ["p"], Ref("t0"), ["id"], ForeignKeyAction.Restrict, ForeignKeyAction.Restrict)] : [];
            tables.Add(new TableDefinition(Ref("t" + i), [Column("id", "int", LogicalTypeKind.Integer), Column("p", "int", LogicalTypeKind.Integer)],
                [new IndexDefinition("PRIMARY", IndexKind.Primary, ["id"])], fks, [], new Dictionary<string, string>()));
        }
        var view = new SchemaVisualizerView("shop");
        var window = new Window { Width = 900, Height = 600, Content = view };
        window.Show();
        await view.ShowAsync(SchemaDiagram.From("shop", tables), null, focusTable: "t3");
        await view.Layouting;

        // More than 300 tables: only t3's component (t0 and its 49 children).
        Assert.Equal(50, view.Canvas.Nodes.Count(n => !n.IsHidden));
        Assert.Equal(["t3"], view.Canvas.SelectedNodes.Select(n => n.Id));
        var state = view.CurrentState();
        Assert.Equal(400, state.Nodes.Count);
        Assert.Equal(350, state.Nodes.Values.Count(n => !n.Placed));

        // Reopening with the saved state and a new table connected to t0: the positions stay, the new one is placed apart.
        tables.Add(new TableDefinition(Ref("added"), [Column("id", "int", LogicalTypeKind.Integer), Column("p", "int", LogicalTypeKind.Integer)], [],
            [new ForeignKeyDefinition("fk_added", ["p"], Ref("t0"), ["id"], ForeignKeyAction.Restrict, ForeignKeyAction.Restrict)], [], new Dictionary<string, string>()));
        var again = new SchemaVisualizerView("shop");
        window.Content = again;
        await again.ShowAsync(SchemaDiagram.From("shop", tables), state);
        await again.Layouting;
        var t0 = again.Canvas.Nodes.Single(n => n.Id == "t0");
        Assert.Equal(new Point(state.Nodes["t0"].X, state.Nodes["t0"].Y), t0.Position);
        var added = again.Canvas.Nodes.Single(n => n.Id == "added");
        Assert.True(added.IsNew);
        Assert.False(added.IsHidden);
        Assert.True(added.Position.X > again.Canvas.Nodes.Where(n => !n.IsHidden && n != added).Max(n => n.Bounds.Right));
        Assert.Contains("1 new", again.StatusText.Text, StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task OpenFromTheTreeSyncAndRefresh()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "No test server.");
        var db = NewDatabaseName("eunomia_sv_");
        await ExecAsync(cs, $"CREATE DATABASE `{db}`; " +
            $"CREATE TABLE `{db}`.customers (id INT PRIMARY KEY, name VARCHAR(50)) ENGINE=InnoDB; " +
            $"CREATE TABLE `{db}`.orders (id INT PRIMARY KEY, customer_id INT, CONSTRAINT fk_c FOREIGN KEY (customer_id) REFERENCES `{db}`.customers (id)) ENGINE=InnoDB; " +
            $"CREATE TABLE `{db}`.profiles (customer_id INT PRIMARY KEY, CONSTRAINT fk_p FOREIGN KEY (customer_id) REFERENCES `{db}`.customers (id)) ENGINE=InnoDB; " +
            $"INSERT INTO `{db}`.customers VALUES (1, 'Ana');");
        var dir = Path.Combine(Path.GetTempPath(), "eunomia-sv-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            using var appDb = new SqliteAppDatabase(Path.Combine(dir, "app.db"));
            var (window, context, session, _, _) = CreateShell(cs, db, appDatabase: appDb, color: 0x2060C0);
            Assert.True(await window.ConnectAsync(window, session, null));
            var model = window.Tree.Tree.Model!;
            var root = await WaitAsync(() => model.Roots.FirstOrDefault());
            var dbNode = await WaitAsync(() => root.Children?.FirstOrDefault(n => n.Name == db));
            await model.ExpandAsync(dbNode);
            window.Tree.Tree.FocusedNode = dbNode.Children!.Single(n => n.Name == "orders");

            Assert.True(await context.Commands.ExecuteAsync("diagram.show"));
            var view = Assert.Single(window.DiagramViews);
            await WaitAsync(() => view.Diagram);
            await view.Layouting;
            Assert.Equal(["customers", "orders", "profiles"], view.Canvas.Nodes.Select(n => n.Id).Order());
            Assert.Equal(["orders"], view.Canvas.SelectedNodes.Select(n => n.Id));
            Assert.Equal(Controls.Graph.GraphCardinality.OneToOne, GraphCardinalityOf(view, "fk_p"));
            Assert.Equal("Diagram: " + db, ((TabItem)window.Tabs.SelectedItem!).Header is StackPanel header ? header.Children.OfType<TextBlock>().First().Text : null);

            // Tree → diagram: the focused table is selected in the diagram.
            window.Tree.Tree.FocusedNode = TreeModel.ObjectsOf(dbNode).Single(n => n.Name == "customers");
            Assert.Equal(["customers"], view.Canvas.SelectedNodes.Select(n => n.Id));

            // Diagram → tree: selecting a node focuses the table in the tree; double click opens the Data tab.
            view.Canvas.Select([view.Canvas.Nodes.Single(n => n.Id == "profiles")]);
            await WaitAsync(() => window.Tree.Tree.FocusedNode?.Name == "profiles" ? "ok" : null);
            var profiles = view.Canvas.Nodes.Single(n => n.Id == "profiles");
            view.Canvas.CenterOn(profiles);
            window.CaptureRenderedFrame();
            var at = view.Canvas.TranslatePoint(view.Canvas.ToScreen(profiles.Position + new Point(20, 10)), window)!.Value;
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            await WaitAsync(() => ReferenceEquals(window.Tabs.SelectedItem, window.Tabs.DataTab) ? "ok" : null);

            // F5 with a new table: the positions stay and the new one is highlighted.
            var before = view.Canvas.Nodes.Single(n => n.Id == "customers").Position;
            await ExecAsync(cs, $"CREATE TABLE `{db}`.notes (id INT PRIMARY KEY, order_id INT, CONSTRAINT fk_n FOREIGN KEY (order_id) REFERENCES `{db}`.orders (id)) ENGINE=InnoDB");
            window.Tabs.SelectedItem = window.Tabs.DiagramTabs[0];
            view.Canvas.Focus();
            window.KeyPress(Key.F5, RawInputModifiers.None, PhysicalKey.F5, null);
            await WaitAsync(() => view.Canvas.Nodes.Any(n => n.Id == "notes") ? "ok" : null);
            await view.Layouting;
            Assert.True(view.Canvas.Nodes.Single(n => n.Id == "notes").IsNew);
            Assert.Equal(before, view.Canvas.Nodes.Single(n => n.Id == "customers").Position);

            // Closing saves the layout; reopening restores it.
            var moved = view.Canvas.Nodes.Single(n => n.Id == "customers");
            moved.Position += new Point(200, 40);
            window.Tabs.CloseDiagramTab(window.Tabs.DiagramTabs[0]);
            Assert.Empty(window.DiagramViews);
            window.Tree.Tree.FocusedNode = dbNode;
            Assert.True(await context.Commands.ExecuteAsync("diagram.show"));
            var reopened = Assert.Single(window.DiagramViews);
            await WaitAsync(() => reopened.Diagram);
            await reopened.Layouting;
            Assert.Equal(moved.Position, reopened.Canvas.Nodes.Single(n => n.Id == "customers").Position);
            window.Close();
        }
        finally
        {
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`");
            Directory.Delete(dir, recursive: true);
        }
    }

    private static Controls.Graph.GraphCardinality GraphCardinalityOf(SchemaVisualizerView view, string name) =>
        view.Canvas.Edges.Single(e => e.Name == name).Cardinality;
}
