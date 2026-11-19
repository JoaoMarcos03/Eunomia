using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Eunomia.App.Settings;
using Eunomia.App.Transfer;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Transfer;
using Eunomia.UI.Controls.Common.Input;
using Eunomia.UI.Controls.Common.Theming;
using Eunomia.UI.Features.Transfer;

namespace Eunomia.UI.Headless.Tests;

/// <summary>Export database as SQL dialog (phase 8; FAM-E06; appendix B §2.3.1).</summary>
public sealed class SqlExportDialogTests
{
    private static DbObjectInfo O(string database, string name, DbObjectKind kind) => new(new DbObjectRef(database, database, name, kind), RowsEstimate: 10);

    /// <summary>
    /// Outputs "Database" and "Server: &lt;session&gt;" (user feedback, 2 Nov 2026): the databases of the target, the
    /// shell's checks, a confirmation that names the destructive options, then the run with its progress.
    /// </summary>
    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task ExportToDatabaseAsksBeforeRunningOnTheTarget()
    {
        var settings = new SqlExportSettings { CreateTables = true, Data = 1 };
        var staging = new SqlExportServer(Guid.NewGuid(), "Customers\\staging");
        SqlExportRequest? requested = null;
        var questions = new List<string>();
        var answer = false;
        var targets = new SqlExportTargets(
            [staging],
            (id, _) => Task.FromResult<IReadOnlyList<string>>(id is null ? ["shop", "shop_copy"] : ["archive"]),
            request => request.TargetDatabase == "shop" ? "The target database \"shop\" is one of the exported databases. Please select another one." : null,
            (request, export, import, _) =>
            {
                requested = request;
                import.Report(new SqlFileProgress(50, 100, 7, 0, 3, TimeSpan.FromSeconds(1)));
                var exported = new SqlExportResult(false, 4096, [], null, TimeSpan.FromSeconds(1));
                return Task.FromResult(new SqlExportToServerResult(exported, new SqlFileResult(Guid.NewGuid(), false, false, 12, 0, 3, 100, 100, TimeSpan.FromSeconds(1), []), null));
            });
        var dialog = new SqlExportDialog(settings, ["shop", "shop_copy"], (db, _) => Task.FromResult<IReadOnlyList<DbObjectInfo>>([O(db, "orders", DbObjectKind.Table)]),
            (_, _, _) => throw new InvalidOperationException("Not a file export."), "shop", null, targets)
        {
            ConfirmOverride = q =>
            {
                questions.Add(q);
                return Task.FromResult(answer);
            },
        };
        dialog.Show();
        await dialog.Loading;

        Assert.Equal(["Single .sql file", "ZIP compressed .sql file", "Directory - one file per object in database subdirectories", "Clipboard", "Database", "Server: Customers\\staging"],
            dialog.OutputCombo.Items.Cast<string>());
        dialog.OutputCombo.SelectedIndex = 4;
        await dialog.TargetLoading;
        Assert.True(dialog.TargetDatabaseSelect.IsVisible);
        Assert.False(dialog.PathBox.IsEffectivelyVisible);
        Assert.Equal(["shop", "shop_copy"], dialog.TargetDatabaseSelect.Items);

        // The exported database itself is refused before anything runs.
        dialog.TargetDatabaseSelect.SelectedItem = "shop";
        dialog.ExportButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await RealServer.WaitAsync(() => UnexpectedDialogGuardAttribute.TakeOpened() is [var refused] ? refused : null);
        Assert.Empty(questions);
        Assert.Null(requested);

        // Another one: the confirmation names the target and "Delete + insert"; No runs nothing.
        dialog.TargetDatabaseSelect.SelectedItem = "shop_copy";
        dialog.ExportButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await RealServer.WaitAsync(() => questions.Count == 1 ? "ok" : null);
        Assert.Contains("database \"shop_copy\" of this session", questions[0], StringComparison.Ordinal);
        Assert.Contains("DELETED", questions[0], StringComparison.Ordinal);
        Assert.Null(requested);

        answer = true;
        dialog.ExportButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await RealServer.WaitAsync(() => dialog.StatusText.Text?.StartsWith("Done", StringComparison.Ordinal) == true ? "ok" : null);
        Assert.NotNull(requested);
        Assert.Equal((SqlExportTarget.Database, "shop_copy", (Guid?)null), (requested.Target, requested.TargetDatabase, requested.TargetSession));
        Assert.Contains("12 queries run on database \"shop_copy\"", dialog.StatusText.Text, StringComparison.Ordinal);
        Assert.Equal(4, settings.Output);
        Assert.Equal("shop_copy", settings.TargetDatabase);

        // A server lists "[Same as on source server]" first and is not remembered as the output.
        dialog.OutputCombo.SelectedIndex = 5;
        await dialog.TargetLoading;
        Assert.Equal([SqlExportDialog.SameAsSource, "archive"], dialog.TargetDatabaseSelect.Items);
        Assert.Equal(SqlExportDialog.SameAsSource, dialog.TargetDatabaseSelect.SelectedItem);
        dialog.ExportButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await RealServer.WaitAsync(() => requested.Target == SqlExportTarget.Server ? "ok" : null);
        Assert.Equal((staging.Id, (string?)null), (requested.TargetSession, requested.TargetDatabase));
        Assert.Contains("in databases with the same names", questions[^1], StringComparison.Ordinal);
        Assert.Equal(4, settings.Output);
        dialog.Close();
    }

    /// <summary>
    /// Request of 12 Nov 2026: an export to another database takes only the checked objects that the Table filter shows,
    /// never the whole database.
    /// </summary>
    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task ExportToAnotherDatabaseTakesOnlyCheckedVisibleObjects()
    {
        SqlExportRequest? requested = null;
        var targets = new SqlExportTargets(
            [],
            (_, _) => Task.FromResult<IReadOnlyList<string>>(["shop", "shop_copy"]),
            _ => null,
            (request, _, _, _) =>
            {
                requested = request;
                var exported = new SqlExportResult(false, 1, [], null, TimeSpan.Zero);
                return Task.FromResult(new SqlExportToServerResult(exported, new SqlFileResult(Guid.NewGuid(), false, false, 1, 0, 1, 1, 1, TimeSpan.Zero, []), null));
            });
        var dialog = new SqlExportDialog(
            new SqlExportSettings { CreateTables = true },
            ["shop", "shop_copy"],
            (db, _) => Task.FromResult<IReadOnlyList<DbObjectInfo>>([O(db, "orders", DbObjectKind.Table), O(db, "customers", DbObjectKind.Table), O(db, "logs", DbObjectKind.Table), O(db, "log_view", DbObjectKind.View)]),
            (_, _, _) => throw new InvalidOperationException("Not a file export."),
            "shop",
            null,
            targets,
            new SqlExportTreeFilters(string.Empty, "^(orders|customers|logs)$"))
        {
            ConfirmOverride = _ => Task.FromResult(true),
        };
        dialog.Show();
        await dialog.Loading;
        dialog.SetObjectChecked("shop", "logs", false);
        dialog.OutputCombo.SelectedIndex = 4;
        await dialog.TargetLoading;
        dialog.TargetDatabaseSelect.SelectedItem = "shop_copy";

        dialog.ExportButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await RealServer.WaitAsync(() => requested);

        var database = Assert.Single(requested!.Databases);
        Assert.Equal(["orders", "customers"], database.Objects.Select(o => o.Ref.Name));
        dialog.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task FocusedDatabaseCheckedOptionsAndProgress()
    {
        var settings = new SqlExportSettings { CreateTables = true, Data = 2, Filenames = [@"C:\dumps\shop.sql"], ZipFilenames = [@"C:\dumps\shop.zip"] };
        var objects = new Dictionary<string, IReadOnlyList<DbObjectInfo>>
        {
            ["shop"] = [O("shop", "customers", DbObjectKind.Table), O("shop", "p1", DbObjectKind.Procedure), O("shop", "v", DbObjectKind.View)],
            ["other"] = [O("other", "t", DbObjectKind.Table)],
        };
        SqlExportRequest? requested = null;
        var dialog = new SqlExportDialog(
            settings,
            ["shop", "other"],
            (db, _) => Task.FromResult(objects[db]),
            (request, progress, _) =>
            {
                requested = request;
                SqlExportItemProgress[] items =
                [
                    new("shop", "customers", DbObjectKind.Table, 5, 10, TimeSpan.FromSeconds(1.5), SqlExportItemState.Done, null),
                    new("shop", "v", DbObjectKind.View, 0, 0, TimeSpan.FromSeconds(0.1), SqlExportItemState.Done, null),
                ];
                progress.Report(new SqlExportProgress(items, 1234, TimeSpan.FromSeconds(1)));
                return Task.FromResult(new SqlExportResult(false, 2048, items, "text", TimeSpan.FromSeconds(2)));
            },
            initialDatabase: "shop",
            initialObjects: null);
        dialog.Show();
        await dialog.Loading;

        // The focused database comes fully checked; the recent entry of the chosen output fills in the name.
        var shop = (CheckBox)((TreeViewItem)dialog.Tree.Items[0]!).Header!;
        Assert.Equal("shop", ((TextBlock)shop.Content!).Text);
        Assert.True(shop.IsChecked);
        Assert.Equal(@"C:\dumps\shop.sql", dialog.PathBox.Text);
        dialog.OutputCombo.SelectedIndex = 1;
        Assert.Equal(@"C:\dumps\shop.zip", dialog.PathBox.Text);

        // Drop implies Create and shows in bold as "Drop!!"; turning Create off turns Drop off.
        dialog.DropTablesBox.IsChecked = true;
        Assert.Equal("Drop!!", dialog.DropTablesBox.Content);
        dialog.CreateTablesBox.IsChecked = false;
        Assert.False(dialog.DropTablesBox.IsChecked);
        Assert.Equal("Drop", dialog.DropTablesBox.Content);
        dialog.CreateTablesBox.IsChecked = true;

        // Unchecking an object leaves the database at "some".
        dialog.SetObjectChecked("shop", "p1", false);
        Assert.Null(shop.IsChecked);

        dialog.OutputCombo.SelectedIndex = 3;
        Assert.False(dialog.PathBox.IsEnabled);
        dialog.ExportButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await RealServer.WaitAsync(() => dialog.StatusText.Text?.StartsWith("Done", StringComparison.Ordinal) == true ? "ok" : null);

        Assert.NotNull(requested);
        Assert.Equal(SqlExportTarget.Clipboard, requested.Target);
        var database = Assert.Single(requested.Databases);
        Assert.Equal("shop", database.Name);
        Assert.Equal(["customers", "v"], database.Objects.Select(o => o.Ref.Name));
        Assert.True(requested.Dump.CreateTables);
        Assert.False(requested.Dump.DropTables);
        Assert.Equal(DumpDataMode.Insert, requested.Dump.Data);
        Assert.Equal(1024, requested.Dump.MaxInsertSizeKb);
        Assert.True(requested.Dump.AddComments);
        Assert.True(requested.Dump.RemoveDefiner);

        var rows = dialog.ResultRows;
        Assert.Equal(2, rows.Count);
        Assert.Equal("customers", rows[0][1].Text);
        Assert.StartsWith("5 / 50%", rows[0][2].Text, StringComparison.Ordinal);
        Assert.Equal("v (view)", rows[1][1].Text);
        Assert.Equal("done", rows[1][2].Text);
        Assert.Equal("Export", dialog.ExportButton.Content);
        Assert.Equal(3, settings.Output);
        dialog.Close();
    }

    /// <summary>
    /// Request of 16 Nov 2026 ("the SQL file did not have the 15 GB, it had 8.7"): a ZIP export says how much SQL went
    /// into how big a file, and objects with errors open a message that names them, not only a line in the status.
    /// </summary>
    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task ZipSizeAndObjectsWithErrorsAreShown()
    {
        using var culture = new CultureScope(System.Globalization.CultureInfo.InvariantCulture);
        var path = Path.Combine(Path.GetTempPath(), "eunomia-" + Guid.NewGuid().ToString("N") + ".zip");
        var settings = new SqlExportSettings { CreateTables = true, Data = 1, Output = 1, ZipFilenames = [path] };
        SqlExportItemProgress[] items =
        [
            new("shop", "customers", DbObjectKind.Table, 5, 10, TimeSpan.FromSeconds(1), SqlExportItemState.Done, null),
            .. Enumerable.Range(0, 12).Select(n => new SqlExportItemProgress("shop", "t" + n, DbObjectKind.Table, 0, 10, TimeSpan.Zero, SqlExportItemState.Failed,
                "Query execution was interrupted")),
            new("shop", "v", DbObjectKind.View, 0, 0, TimeSpan.Zero, SqlExportItemState.Failed, "View 'shop.v' references invalid table(s)"),
        ];
        var dialog = new SqlExportDialog(
            settings,
            ["shop"],
            (db, _) => Task.FromResult<IReadOnlyList<DbObjectInfo>>([O(db, "customers", DbObjectKind.Table)]),
            (_, _, _) => Task.FromResult(new SqlExportResult(false, 9L * 1024 * 1024 * 1024, items, null, TimeSpan.FromSeconds(312.4)) { FileBytes = 860L * 1024 * 1024 }),
            initialDatabase: "shop",
            initialObjects: null);
        dialog.Show();
        await dialog.Loading;
        Assert.Equal(path, dialog.PathBox.Text);

        dialog.ExportButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var message = await RealServer.WaitAsync(() => UnexpectedDialogGuardAttribute.TakeOpened() is [var opened] ? opened : null);

        Assert.Equal("Done: 9.00 GiB of SQL in a 860.0 MiB ZIP file, in 312.4 s, 13 object(s) with errors.", dialog.StatusText.Text);
        Assert.StartsWith("MessageDialog \"Export finished with errors\": 13 object(s) could not be exported.", message, StringComparison.Ordinal);
        Assert.Contains("\nshop.t0: Query execution was interrupted", message, StringComparison.Ordinal);
        Assert.Contains("\nshop.t9: Query execution was interrupted", message, StringComparison.Ordinal);
        Assert.DoesNotContain("shop.t10", message, StringComparison.Ordinal);
        Assert.EndsWith("... and 3 more (marked in the list).", message, StringComparison.Ordinal);
        Assert.False(File.Exists(path));
        dialog.Close();
    }

    [Fact]
    public void DoneTextOfAFileHasNoZipSize()
    {
        using var culture = new CultureScope(System.Globalization.CultureInfo.InvariantCulture);
        var result = new SqlExportResult(false, 2048, [], null, TimeSpan.FromSeconds(2)) { FileBytes = 2048 };

        Assert.Equal("Done: 2.0 KiB in 2.0 s.", SqlExportDialog.DoneText(result, SqlExportTarget.File, failed: 0));
        Assert.Equal("Done: 2.0 KiB in 2.0 s, 1 object(s) with errors.", SqlExportDialog.DoneText(result with { FileBytes = null }, SqlExportTarget.Clipboard, failed: 1));
        var failures = new[] { new SqlExportItemProgress("db", "p", DbObjectKind.Procedure, 0, 0, TimeSpan.Zero, SqlExportItemState.Failed, "denied") };
        Assert.EndsWith("\n\ndb.p (procedure): denied", SqlExportDialog.FailuresText(failures), StringComparison.Ordinal);
    }

    /// <summary>
    /// The dialog opens with the main tree's filters (not the saved ones) and exports only what they show: a
    /// hidden database stays out even when checked, and checking a database checks only its visible objects.
    /// </summary>
    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task TreeFiltersOpenTheDialogAndOnlyWhatIsVisibleIsExported()
    {
        var settings = new SqlExportSettings { CreateTables = true, DatabaseFilter = "old", TableFilter = "old" };
        var objects = new Dictionary<string, IReadOnlyList<DbObjectInfo>>
        {
            ["shop"] = [O("shop", "customers", DbObjectKind.Table), O("shop", "orders", DbObjectKind.Table), O("shop", "logs", DbObjectKind.Table)],
            ["archive"] = [O("archive", "logs_2020", DbObjectKind.Table)],
            ["users"] = [O("users", "customers", DbObjectKind.Table)],
        };
        SqlExportRequest? requested = null;
        var dialog = new SqlExportDialog(
            settings,
            ["archive", "shop", "users"],
            (db, _) => Task.FromResult(objects[db]),
            (request, _, _) =>
            {
                requested = request;
                return Task.FromResult(new SqlExportResult(false, 0, [], "text", TimeSpan.Zero));
            },
            initialDatabase: "shop",
            initialObjects: null,
            treeFilters: new SqlExportTreeFilters("shop;archive", "^CUS|^ord"));
        dialog.Show();
        await dialog.Loading;

        Assert.Equal("shop;archive", dialog.DatabaseFilterBox.Text);
        Assert.Equal("^CUS|^ord", dialog.TableFilterBox.Text);
        var items = dialog.Tree.Items.Cast<TreeViewItem>().ToList();
        Assert.Equal([true, true, false], items.Select(i => i.IsVisible));

        // "users" is hidden by the filter: checked before, it stays out of the export.
        dialog.SetDatabaseChecked("users", true);
        await dialog.Loading;
        // "archive" is visible but none of its objects passes the Table filter: checking it exports nothing from it.
        dialog.SetDatabaseChecked("archive", true);
        await dialog.Loading;

        var shop = (CheckBox)items[1].Header!;
        Assert.True(shop.IsChecked);
        dialog.OutputCombo.SelectedIndex = 3;
        dialog.ExportButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await RealServer.WaitAsync(() => dialog.StatusText.Text?.StartsWith("Done", StringComparison.Ordinal) == true ? "ok" : null);

        Assert.NotNull(requested);
        var database = Assert.Single(requested.Databases);
        Assert.Equal("shop", database.Name);
        Assert.Equal(["customers", "orders"], database.Objects.Select(o => o.Ref.Name));

        // Without a Table filter, "logs" shows unchecked and the database becomes "some".
        dialog.TableFilterBox.Text = string.Empty;
        await RealServer.WaitAsync(() => shop.IsChecked is null ? "ok" : null);
        Assert.True(items[0].IsVisible);
        Assert.Equal("shop;archive", settings.DatabaseFilter);
        dialog.Close();
    }

    /// <summary>
    /// Issue #5: with the tree's "Group objects by type", the dialog shows the main tree's 6 groups ("Tables (2)", the ones
    /// without visible objects disabled); a group checks its visible objects and its state counts only them; the export
    /// keeps the order of the plain list.
    /// </summary>
    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task GroupObjectsShowsTheMainTreeGroups()
    {
        SqlExportRequest? requested = null;
        var dialog = new SqlExportDialog(
            new SqlExportSettings { CreateTables = true },
            ["shop"],
            (db, _) => Task.FromResult<IReadOnlyList<DbObjectInfo>>([O(db, "orders", DbObjectKind.Table), O(db, "v_orders", DbObjectKind.View),
                O(db, "customers", DbObjectKind.Table), O(db, "p", DbObjectKind.Procedure), O(db, "trg", DbObjectKind.Trigger)]),
            (request, _, _) =>
            {
                requested = request;
                return Task.FromResult(new SqlExportResult(false, 0, [], "text", TimeSpan.Zero));
            },
            "shop",
            ["p"],
            treeFilters: new SqlExportTreeFilters(string.Empty, string.Empty),
            groupObjects: true);
        dialog.Show();
        await dialog.Loading;
        var database = (CheckBox)dialog.Tree.Items.Cast<TreeViewItem>().Single().Header!;
        await RealServer.WaitAsync(() => database.IsChecked is null ? "ok" : null);

        var groups = dialog.GroupItems("shop");
        var checks = groups.Select(g => (CheckBox)g.Header!).ToList();
        Assert.Equal(["Tables (2)", "Views (1)", "Procedures (1)", "Functions (0)", "Triggers (1)", "Events (0)"], checks.Select(c => ((TextBlock)c.Content!).Text));
        Assert.Equal([true, true, true, false, true, false], checks.Select(c => c.IsEnabled));
        // The chosen object comes checked in its group, which opens; the others stay closed.
        Assert.Equal([false, false, true, false, false, false], checks.Select(c => c.IsChecked));
        Assert.Equal([false, false, true, false, false, false], groups.Select(g => g.IsExpanded));
        // The group says the kind: the objects show only their name.
        var tables = groups[0].Items.Cast<TreeViewItem>().ToList();
        Assert.Equal(["orders", "customers"], tables.Select(i => ((TextBlock)ObjectCheck(i).Content!).Text));
        Assert.Single(((StackPanel)tables[0].Header!).Children);

        // A group checks its objects; one unchecked object leaves it partial.
        checks[0].IsChecked = true;
        Assert.True(ObjectCheck(tables[1]).IsChecked);
        dialog.SetObjectChecked("shop", "orders", false);
        Assert.Null(checks[0].IsChecked);
        Assert.Null(database.IsChecked);

        // With the Table filter, groups count only their visible objects: Procedures (its "p" checked but hidden) and
        // Triggers have none and are disabled.
        dialog.TableFilterBox.Text = "^(o|v|c)";
        await RealServer.WaitAsync(() => !checks[2].IsEnabled ? "ok" : null);
        Assert.Equal([true, true, false, false, false, false], checks.Select(c => c.IsEnabled));
        Assert.Equal([null, false, false, false, false, false], checks.Select(c => c.IsChecked));
        checks[1].IsChecked = true;
        checks[0].IsChecked = true;
        Assert.True(database.IsChecked);

        // The export takes the checked visible objects in the plain list's order, not the groups'.
        dialog.OutputCombo.SelectedIndex = 3;
        dialog.ExportButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await RealServer.WaitAsync(() => dialog.StatusText.Text?.StartsWith("Done", StringComparison.Ordinal) == true ? "ok" : null);
        Assert.Equal(["orders", "v_orders", "customers"], Assert.Single(requested!.Databases).Objects.Select(o => o.Ref.Name));
        dialog.Close();
    }

    private static CheckBox ObjectCheck(TreeViewItem item) => (CheckBox)((StackPanel)item.Header!).Children[0];

    /// <summary>
    /// Request of 16 Nov 2026: each "Server:" row of the output list has its session's colour from the settings, as in the
    /// tree (a soft tint and a bar with the full colour), in light and dark; the other rows keep the theme's.
    /// </summary>
    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task ServerRowsHaveTheSessionColour()
    {
        const uint Crimson = 0xDC143C;
        var targets = new SqlExportTargets(
            [new SqlExportServer(Guid.NewGuid(), "live", Crimson), new SqlExportServer(Guid.NewGuid(), "plain")],
            (_, _) => Task.FromResult<IReadOnlyList<string>>(["shop"]),
            _ => null,
            (_, _, _, _) => throw new InvalidOperationException("Not run."));
        var dialog = new SqlExportDialog(new SqlExportSettings(), ["shop"], (_, _) => Task.FromResult<IReadOnlyList<DbObjectInfo>>([]),
            (_, _, _) => throw new InvalidOperationException("Not run."), null, null, targets)
        { RequestedThemeVariant = ThemeVariant.Light };
        dialog.Show();
        await dialog.Loading;

        var combo = dialog.OutputCombo;
        combo.IsDropDownOpen = true;
        var live = await RealServer.WaitAsync(() => combo.ContainerFromIndex(5) as ComboBoxItem);
        var plain = (ComboBoxItem)combo.ContainerFromIndex(6)!;
        var database = (ComboBoxItem)combo.ContainerFromIndex(4)!;
        var bar = new Thickness(3, 0, 0, 0);
        Assert.Equal(ColorMath.SessionTint(Crimson, ThemeTokens.Light.Argb("surface.content")), BrushArgb(live.Background));
        Assert.Equal(0xFFDC143C, BrushArgb(live.BorderBrush));
        Assert.Equal(bar, live.BorderThickness);
        foreach (var other in new[] { plain, database })
        {
            Assert.NotEqual(bar, other.BorderThickness);
            Assert.NotEqual(BrushArgb(live.Background), BrushArgb(other.Background));
        }

        // The theme changes with the list open: the rows are painted again.
        dialog.RequestedThemeVariant = ThemeVariant.Dark;
        Assert.Equal(ColorMath.SessionTint(Crimson, ThemeTokens.Dark.Argb("surface.panel")), BrushArgb(live.Background));
        Assert.NotEqual(bar, plain.BorderThickness);
        combo.IsDropDownOpen = false;
        dialog.Close();

        static uint? BrushArgb(IBrush? brush) => (brush as ISolidColorBrush)?.Color.ToUInt32();
    }

    /// <summary>
    /// Request of 16 Nov 2026: the target database of a server is searched by name (contains, ignoring case) and, with
    /// nothing typed, the list shows every database; the only match is chosen when the box loses the focus.
    /// </summary>
    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task TheTargetDatabaseIsSearchedByName()
    {
        SqlExportRequest? requested = null;
        var targets = new SqlExportTargets(
            [new SqlExportServer(Guid.NewGuid(), "staging")],
            (id, _) => Task.FromResult<IReadOnlyList<string>>(id is null ? ["shop"] : ["archive_2024", "crm", "Shop_Copy", "shop_old", "stats"]),
            _ => null,
            (request, _, _, _) =>
            {
                requested = request;
                var exported = new SqlExportResult(false, 1, [], null, TimeSpan.Zero);
                return Task.FromResult(new SqlExportToServerResult(exported, new SqlFileResult(Guid.NewGuid(), false, false, 1, 0, 1, 1, 1, TimeSpan.Zero, []), null));
            });
        var dialog = new SqlExportDialog(new SqlExportSettings { CreateTables = true }, ["shop"],
            (db, _) => Task.FromResult<IReadOnlyList<DbObjectInfo>>([O(db, "orders", DbObjectKind.Table)]),
            (_, _, _) => throw new InvalidOperationException("Not a file export."), "shop", null, targets)
        {
            ConfirmOverride = _ => Task.FromResult(true),
        };
        dialog.Show();
        await dialog.Loading;
        dialog.OutputCombo.SelectedIndex = 5;
        await dialog.TargetLoading;
        var select = dialog.TargetDatabaseSelect;
        string[] all = [SqlExportDialog.SameAsSource, "archive_2024", "crm", "Shop_Copy", "shop_old", "stats"];
        Assert.Equal(all, select.Items);
        Assert.Equal(SqlExportDialog.SameAsSource, select.SelectedItem);

        // The arrow, with the chosen item in the box: every database.
        await PopupAsync(select);
        select.Open();
        Assert.Equal(all, await ListedAsync(select, all.Length));

        // Typing replaces the chosen item and keeps the names that contain the text, ignoring case.
        Dispatcher.UIThread.RunJobs();
        dialog.KeyTextInput("SHOP");
        Assert.Equal("SHOP", select.Box.Text);
        Assert.Equal(["Shop_Copy", "shop_old"], await ListedAsync(select, 2));
        Assert.True(select.Box.IsDropDownOpen);

        // Text cleared: the whole list again.
        select.Box.Text = string.Empty;
        Assert.Equal(all, await ListedAsync(select, all.Length));

        // The only database that contains "old" is the one exported to.
        dialog.KeyTextInput("old");
        dialog.ExportButton.Focus();
        Assert.Equal("shop_old", select.SelectedItem);
        dialog.ExportButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await RealServer.WaitAsync(() => requested);
        Assert.Equal("shop_old", requested!.TargetDatabase);
        dialog.Close();

        // The names in the select's open list (the list box of its popup).
        static async Task<string[]> ListedAsync(SearchableSelect select, int count)
        {
            var popup = await PopupAsync(select);
            var list = Assert.Single(popup.Child!.GetSelfAndVisualDescendants().OfType<ListBox>());
            await RealServer.WaitAsync(() => list.Items.Count == count ? "ok" : null);
            return [.. list.Items.Cast<string>()];
        }

        // The select was hidden until a database output was chosen: its template comes with the next layout.
        static Task<Popup> PopupAsync(SearchableSelect select) => RealServer.WaitAsync(() => select.Box.GetVisualDescendants().OfType<Popup>().FirstOrDefault());
    }

    /// <summary>Without tree filters (tests, other callers) the dialog uses the saved filters, now with ';'.</summary>
    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task WithoutTreeFiltersTheSavedOnesAreUsedWithTheTreeSyntax()
    {
        var settings = new SqlExportSettings { DatabaseFilter = "shop;archive" };
        var dialog = new SqlExportDialog(
            settings,
            ["archive", "shop", "users"],
            (_, _) => Task.FromResult<IReadOnlyList<DbObjectInfo>>([]),
            (_, _, _) => throw new InvalidOperationException("Not run."));
        dialog.Show();
        await dialog.Loading;

        Assert.Equal([true, true, false], dialog.Tree.Items.Cast<TreeViewItem>().Select(i => i.IsVisible));
        dialog.Close();
    }

    /// <summary>
    /// Request of 12 Nov 2026: with the Database tab list focused, the export opens with only the objects selected in it
    /// checked (as in HeidiSQL), and that is what goes in the request.
    /// </summary>
    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task DatabaseTabListSelectionComesCheckedInTheExport()
    {
        var cs = RealServer.ConnectionString();
        Assert.SkipWhen(cs is null, "No test server.");
        var db = RealServer.NewDatabaseName("eunomia_exp_");
        await RealServer.ExecAsync(cs, $"CREATE DATABASE `{db}`; CREATE TABLE `{db}`.a (id INT); CREATE TABLE `{db}`.b (id INT); CREATE TABLE `{db}`.c (id INT);");
        try
        {
            var (window, context, session, connector, _) = RealServer.CreateShell(cs, db);
            Assert.True(await window.ConnectAsync(window, session, null));
            var root = await RealServer.WaitAsync(() => window.Tree.Tree.Model!.Roots.FirstOrDefault());
            window.Tree.Tree.FocusedNode = await RealServer.WaitAsync(() => root.Children?.FirstOrDefault());
            var list = window.DatabaseView.List;
            await RealServer.WaitAsync(() => list.Roots.Count == 3 ? "ok" : null);
            list.Focus();
            list.FocusedNode = list.Roots.First(n => n[0] == "b");
            Assert.True(list.IsKeyboardFocusWithin);

            UnexpectedDialogGuardAttribute.Allow(typeof(SqlExportDialog));
            var command = window.CommandUi.ExecuteAsync("export.databaseAsSql");
            var dialog = await RealServer.WaitAsync(() => window.OwnedWindows.OfType<SqlExportDialog>().FirstOrDefault());
            await RealServer.WaitAsync(() => dialog.Tree.Items.Count > 0 ? "ok" : null);
            await dialog.Loading;
            var item = dialog.Tree.Items.Cast<TreeViewItem>().First(i => ((TextBlock)((CheckBox)i.Header!).Content!).Text == db);
            await RealServer.WaitAsync(() => ((CheckBox)item.Header!).IsChecked is null ? "ok" : null);
            var checks = item.Items.Cast<TreeViewItem>().Select(i => (CheckBox)((StackPanel)i.Header!).Children[0]).ToList();
            Assert.Equal(["b"], checks.Where(c => c.IsChecked == true).Select(c => ((TextBlock)c.Content!).Text));
            dialog.Close();
            await command;

            // With the tree's "Group objects by type" (issue #5), the same selection shows in the open Tables group.
            context.Settings.Tree.GroupObjects = true;
            list.Focus();
            list.FocusedNode = list.Roots.First(n => n[0] == "b");
            var flat = dialog;
            command = window.CommandUi.ExecuteAsync("export.databaseAsSql");
            dialog = await RealServer.WaitAsync(() => window.OwnedWindows.OfType<SqlExportDialog>().FirstOrDefault(d => !ReferenceEquals(d, flat)));
            await RealServer.WaitAsync(() => dialog.GroupItems(db) is { Count: 6 } g && ((CheckBox)g[0].Header!).IsChecked is null ? "ok" : null);
            var tables = dialog.GroupItems(db)[0];
            Assert.Equal("Tables (3)", ((TextBlock)((CheckBox)tables.Header!).Content!).Text);
            Assert.True(tables.IsExpanded);
            checks = [.. tables.Items.Cast<TreeViewItem>().Select(i => (CheckBox)((StackPanel)i.Header!).Children[0])];
            Assert.Equal(["b"], checks.Where(c => c.IsChecked == true).Select(c => ((TextBlock)c.Content!).Text));
            dialog.Close();
            await command;
            await connector.DisconnectAllAsync();
            window.Close();
        }
        finally
        {
            await RealServer.ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`");
        }
    }
}
