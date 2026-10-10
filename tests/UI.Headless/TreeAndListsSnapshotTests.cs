using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Eunomia.Drivers.Abstractions;
using Eunomia.Presentation.Objects;
using Eunomia.UI.Controls.Tree;
using Eunomia.UI.Features.DatabaseTab;
using Eunomia.UI.Features.HostTab;

namespace Eunomia.UI.Headless.Tests;

/// <summary>
/// Snapshots da Fase 4: árvore com grupos, colunas e estados (anexo D A1, A2) e as listas do Host e do Database tab
/// (secção 4.9), com dados fixos e cultura invariante.
/// </summary>
public sealed class TreeAndListsSnapshotTests
{
    /// <summary>Árvore montada à mão: uma sessão com grupos e colunas, outra sessão inativa.</summary>
    private sealed class FixedProvider : ITreeDataProvider
    {
        public Task<IReadOnlyList<TreeNode>> LoadChildrenAsync(TreeNode parent, CancellationToken cancellationToken)
        {
            IReadOnlyList<TreeNode> result = parent.Kind switch
            {
                TreeNodeKind.Session when parent.Name == "localhost" =>
                [
                    new TreeNode(TreeNodeKind.Database, "information_schema", parent) { IsGhosted = true },
                    new TreeNode(TreeNodeKind.Database, "shop", parent) { IsCurrent = true },
                    new TreeNode(TreeNodeKind.Database, "stats", parent) { IsGhosted = true },
                ],
                TreeNodeKind.Session => [new TreeNode(TreeNodeKind.Database, "archive", parent) { IsGhosted = true }],
                TreeNodeKind.Database => Groups(parent),
                TreeNodeKind.Table =>
                [
                    new TreeNode(TreeNodeKind.Column, "id", parent) { KeyMarker = ColumnKeyMarker.Primary, TextCategory = 0 },
                    new TreeNode(TreeNodeKind.Column, "email", parent) { KeyMarker = ColumnKeyMarker.Unique, TextCategory = 2 },
                    new TreeNode(TreeNodeKind.Column, "created", parent) { TextCategory = 4 },
                ],
                _ => [],
            };
            return Task.FromResult(result);
        }

        private static List<TreeNode> Groups(TreeNode db)
        {
            var groups = new List<TreeNode>();
            foreach (var (kind, caption, names) in new (TreeNodeKind, string, string[])[]
                     {
                         (TreeNodeKind.Table, "Tables", ["customers", "orders", "sessions"]),
                         (TreeNodeKind.View, "Views", ["v_sales"]),
                         (TreeNodeKind.Procedure, "Procedures", ["p_close_day"]),
                         (TreeNodeKind.Function, "Functions", []),
                         (TreeNodeKind.Trigger, "Triggers", []),
                         (TreeNodeKind.Event, "Events", []),
                     })
            {
                var group = new TreeNode(TreeNodeKind.Group, caption, db) { GroupOf = kind };
                var children = names.Select((n, i) => new TreeNode(kind, n, group)
                {
                    SizeBytes = kind == TreeNodeKind.Table ? (i + 1) * 196_608L : null,
                    MayHaveChildren = kind == TreeNodeKind.Table,
                    IsGhosted = n != "customers",
                    IsFavorite = n == "orders",
                    EngineOverlay = n == "sessions" ? "MEMORY" : null,
                }).ToList();
                group.SetChildren(children);
                group.IsGhosted = children.Count == 0;
                groups.Add(group);
            }
            db.ChildSizesKnown = true;
            return groups;
        }
    }

    private static Window CreateWindow(Control content, double width, double height, string theme)
    {
        var window = new Window { Width = width, Height = height, Content = content, RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light };
        window.Show();
        window.Activate();
        return window;
    }

    [AvaloniaTheory(Timeout = UiTimeouts.Default)]
    [InlineData("tree-groups-light", "light")]
    [InlineData("tree-groups-dark", "dark")]
    public async Task ArvoreComGruposColunasEEstados(string name, string theme)
    {
        using var culture = new CultureScope(CultureInfo.InvariantCulture);
        var view = new DatabaseTreeView();
        view.Tree.Culture = CultureInfo.InvariantCulture;
        var model = new TreeModel(
        [
            new TreeNode(TreeNodeKind.Session, "localhost"),
            new TreeNode(TreeNodeKind.Session, "backup-server"),
        ], new FixedProvider());
        view.Tree.Model = model;
        var session = model.Roots[0];
        await model.ExpandAsync(session);
        await model.ExpandAsync(model.Roots[1]);
        var shop = session.Children![1];
        await model.ExpandAsync(shop);
        var tables = shop.Children![0];
        await model.ExpandAsync(tables);
        var customers = tables.Children![0];
        await model.ExpandAsync(customers);
        await model.ExpandAsync(shop.Children![1]);
        TreeModel.UpdateSizes(shop);
        view.Tree.FocusedNode = customers;

        var window = CreateWindow(view, 320, 520, theme);
        view.Tree.Focus();
        window.CaptureRenderedFrame();
        Snapshot.Match(window.CaptureRenderedFrame(), name);
        window.Close();
    }

    private static DbObjectInfo Obj(string name, DbObjectKind kind, long? data, long? rows, string? engine) =>
        new(new DbObjectRef("shop", "shop", name, kind), DataBytes: data, IndexBytes: data is null ? null : 16_384, RowsEstimate: rows, Engine: engine,
            Comment: kind == DbObjectKind.Table ? "Main data" : null, Updated: new DateTime(2026, 9, 30, 18, 4, 5))
        {
            Created = new DateTime(2026, 1, 15, 9, 30, 0),
        };

    [AvaloniaTheory]
    [InlineData("database-tab-light", "light")]
    [InlineData("database-tab-dark", "dark")]
    public void DatabaseTab(string name, string theme)
    {
        using var culture = new CultureScope(CultureInfo.InvariantCulture);
        var view = new DatabaseTabView();
        view.Show("shop",
        [
            Obj("customers", DbObjectKind.Table, 1_048_576, 12_345, "InnoDB"),
            Obj("orders", DbObjectKind.Table, 7_340_032, 98_765, "InnoDB"),
            Obj("sessions", DbObjectKind.Table, 65_536, 42, "MEMORY"),
            Obj("v_sales", DbObjectKind.View, null, null, null),
            Obj("p_close_day", DbObjectKind.Procedure, null, null, null),
        ]);
        view.List.FocusedNode = view.List.VisibleNodes[1];

        var window = CreateWindow(view, 900, 220, theme);
        view.List.Focus();
        window.CaptureRenderedFrame();
        Snapshot.Match(window.CaptureRenderedFrame(), name);
        window.Close();
    }

    [AvaloniaTheory(Timeout = UiTimeouts.Default)]
    [InlineData("data-tab-light", "light")]
    [InlineData("data-tab-dark", "dark")]
    public async Task DataTab(string name, string theme)
    {
        using var culture = new CultureScope(CultureInfo.InvariantCulture);
        var view = new UI.Features.DataTab.DataTabView();
        view.Label.Text = "shop.products: 12,345 rows total (approximately), limited to 1,000";
        view.SetPaging(true);
        view.SetSortCount(2);
        view.SetColumns(5, 6);
        view.SetFilterActive(true);
        view.FilterPanel.IsVisible = true;
        view.SetRecentFilters(["`name` LIKE '%a%'", "id > 10"]);
        view.FilterEditor.Text = "`name` LIKE '%a%'";
        var grid = view.Grid.Control;
        grid.Culture = CultureInfo.InvariantCulture;
        var source = new Engine.Results.Synthetic.SyntheticRowSource(rows: 200, columns: 5, seed: 3);
        grid.SetSource(source);
        await source.RequestRangeAsync(0, 200);
        grid.SortIndicators = new Dictionary<int, UI.Controls.Grid.GridSortIndicator> { [1] = new(true, 1), [2] = new(false, 2) };
        await grid.AutoSizeColumnsAsync(0);
        grid.Selection.Focus(2, 1);

        var window = CreateWindow(view, 900, 360, theme);
        window.CaptureRenderedFrame();
        Snapshot.Match(window.CaptureRenderedFrame(), name);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("table-editor-light", "light")]
    [InlineData("table-editor-dark", "dark")]
    public void TableEditor(string name, string theme)
    {
        using var culture = new CultureScope(CultureInfo.InvariantCulture);
        var design = new App.Objects.TableDesign { Database = "shop", Name = "products", Comment = "Catalogue", Engine = "InnoDB", Collation = "utf8mb4_unicode_ci" };
        design.Columns.Add(new App.Objects.ColumnDesign { OriginalName = "id", Name = "id", DataType = "INT", LengthSet = "10", Unsigned = true, AllowNull = false, DefaultKind = App.Objects.DefaultKind.AutoIncrement });
        design.Columns.Add(new App.Objects.ColumnDesign { OriginalName = "name", Name = "name", DataType = "VARCHAR", LengthSet = "120", AllowNull = false, DefaultKind = App.Objects.DefaultKind.Text, DefaultText = "unnamed", Comment = "Display name", Collation = "utf8mb4_unicode_ci" });
        design.Columns.Add(new App.Objects.ColumnDesign { OriginalName = "price", Name = "price", DataType = "DECIMAL", LengthSet = "10,2", DefaultKind = App.Objects.DefaultKind.Null });
        design.Columns.Add(new App.Objects.ColumnDesign { OriginalName = "updated", Name = "updated", DataType = "TIMESTAMP", AllowNull = false, DefaultKind = App.Objects.DefaultKind.Expression, DefaultText = "CURRENT_TIMESTAMP", OnUpdate = "CURRENT_TIMESTAMP" });
        design.Indexes.Add(new App.Objects.IndexDesign { Name = "PRIMARY", Kind = Drivers.Abstractions.IndexKind.Primary, Columns = "id" });
        var lists = new UI.Features.TableEditor.TableEditorLists(["utf8mb4_general_ci", "utf8mb4_unicode_ci"], ["InnoDB", "MyISAM"], ["orders"]);
        var view = new UI.Features.TableEditor.TableEditorView(design, lists, _ => "CREATE TABLE `shop`.`products` (...)", _ => string.Empty) { SelectedColumn = 1 };

        var window = CreateWindow(view, 1100, 420, theme);
        window.CaptureRenderedFrame();
        Snapshot.Match(window.CaptureRenderedFrame(), name);
        window.Close();
    }

    [AvaloniaTheory(Timeout = UiTimeouts.Default)]
    [InlineData("sql-export-light", "light")]
    [InlineData("sql-export-dark", "dark")]
    public async Task SqlExport(string name, string theme)
    {
        using var culture = new CultureScope(CultureInfo.InvariantCulture);
        static DbObjectInfo O(string name, DbObjectKind kind) => new(new DbObjectRef("shop", "shop", name, kind), RowsEstimate: 1000);
        IReadOnlyList<DbObjectInfo> objects = [O("customers", DbObjectKind.Table), O("orders", DbObjectKind.Table), O("order_total", DbObjectKind.Function), O("v_sales", DbObjectKind.View)];
        var settings = new App.Settings.SqlExportSettings { CreateTables = true, Data = 2, Filenames = [@"C:\dumps\shop.sql"] };
        var dialog = new UI.Features.Transfer.SqlExportDialog(settings, ["information_schema", "shop", "stats"], (_, _) => Task.FromResult(objects),
            (_, _, _) => Task.FromResult(new App.Transfer.SqlExportResult(false, 0, [], null, TimeSpan.Zero)), "shop", null);
        await dialog.Loading;
        var content = (Control)dialog.Content!;
        dialog.Content = null;

        var window = CreateWindow(content, 900, 640, theme);
        window.CaptureRenderedFrame();
        Snapshot.Match(window.CaptureRenderedFrame(), name);
        window.Close();
    }

    [AvaloniaTheory(Timeout = UiTimeouts.Default)]
    [InlineData("csv-import-light", "light")]
    [InlineData("csv-import-dark", "dark")]
    public async Task CsvImport(string name, string theme)
    {
        using var culture = new CultureScope(CultureInfo.InvariantCulture);
        var settings = new App.Settings.CsvImportSettings { FileName = @"C:\data\customers.csv" };
        var dialog = new UI.Features.Transfer.CsvImportDialog(settings, ["shop", "stats"],
            (_, _) => Task.FromResult<IReadOnlyList<string>>(["customers", "orders"]),
            (_, _, _) => Task.FromResult<IReadOnlyList<Engine.Transfer.CsvTargetColumn>>([new("id", true), new("name", false), new("email", false), new("created_at", false)]),
            (_, _, _) => Task.FromResult(new App.Transfer.CsvImportResult(false, 0, 0, 0, 0, TimeSpan.Zero)), "shop", "customers");
        await dialog.Loading;
        var content = (Control)dialog.Content!;
        dialog.Content = null;

        var window = CreateWindow(content, 760, 700, theme);
        window.CaptureRenderedFrame();
        Snapshot.Match(window.CaptureRenderedFrame(), name);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("host-tab-light", "light")]
    [InlineData("host-tab-dark", "dark")]
    public void HostTab(string name, string theme)
    {
        using var culture = new CultureScope(CultureInfo.InvariantCulture);
        var view = new HostTabView();
        var counts = new Dictionary<DbObjectKind, int> { [DbObjectKind.Table] = 3, [DbObjectKind.View] = 1, [DbObjectKind.Procedure] = 1 };
        view.ShowDatabases(
        [
            new HostDatabaseRow("information_schema", null, null, null, null, "utf8_general_ci"),
            new HostDatabaseRow("shop", 8_552_448, 5, new DateTime(2026, 9, 30, 18, 4, 5), counts, "utf8mb4_unicode_ci"),
            new HostDatabaseRow("stats", 196_608, 1, new DateTime(2026, 8, 1, 7, 0, 0), new Dictionary<DbObjectKind, int> { [DbObjectKind.Table] = 1 }, "utf8mb4_general_ci"),
        ]);
        view.DatabaseList.FocusedNode = view.DatabaseList.VisibleNodes[1];

        var window = CreateWindow(view, 1000, 220, theme);
        view.DatabaseList.Focus();
        window.CaptureRenderedFrame();
        Snapshot.Match(window.CaptureRenderedFrame(), name);
        window.Close();
    }
}
