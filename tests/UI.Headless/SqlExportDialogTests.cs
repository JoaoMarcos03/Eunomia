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
}
