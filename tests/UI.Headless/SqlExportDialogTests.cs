using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Eunomia.App.Settings;
using Eunomia.App.Transfer;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Transfer;
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
        Assert.True(dialog.TargetDatabaseCombo.IsVisible);
        Assert.False(dialog.PathBox.IsEffectivelyVisible);
        Assert.Equal(["shop", "shop_copy"], dialog.TargetDatabaseCombo.Items.Cast<string>());

        // The exported database itself is refused before anything runs.
        dialog.TargetDatabaseCombo.SelectedItem = "shop";
        dialog.ExportButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await RealServer.WaitAsync(() => UnexpectedDialogGuardAttribute.TakeOpened() is [var refused] ? refused : null);
        Assert.Empty(questions);
        Assert.Null(requested);

        // Another one: the confirmation names the target and "Delete + insert"; No runs nothing.
        dialog.TargetDatabaseCombo.SelectedItem = "shop_copy";
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
        Assert.Equal([SqlExportDialog.SameAsSource, "archive"], dialog.TargetDatabaseCombo.Items.Cast<string>());
        Assert.Equal(SqlExportDialog.SameAsSource, dialog.TargetDatabaseCombo.SelectedItem);
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
        dialog.TargetDatabaseCombo.SelectedItem = "shop_copy";

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
            var (window, _, session, connector, _) = RealServer.CreateShell(cs, db);
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
            await connector.DisconnectAllAsync();
            window.Close();
        }
        finally
        {
            await RealServer.ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`");
        }
    }
}
