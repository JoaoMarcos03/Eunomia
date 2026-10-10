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

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task BaseFocadaMarcadaOpcoesEProgresso()
    {
        var settings = new SqlExportSettings { CreateTables = true, Data = 2, Filenames = [@"C:\dumps\loja.sql"], ZipFilenames = [@"C:\dumps\loja.zip"] };
        var objects = new Dictionary<string, IReadOnlyList<DbObjectInfo>>
        {
            ["loja"] = [O("loja", "clientes", DbObjectKind.Table), O("loja", "p1", DbObjectKind.Procedure), O("loja", "v", DbObjectKind.View)],
            ["outra"] = [O("outra", "t", DbObjectKind.Table)],
        };
        SqlExportRequest? requested = null;
        var dialog = new SqlExportDialog(
            settings,
            ["loja", "outra"],
            (db, _) => Task.FromResult(objects[db]),
            (request, progress, _) =>
            {
                requested = request;
                SqlExportItemProgress[] items =
                [
                    new("loja", "clientes", DbObjectKind.Table, 5, 10, TimeSpan.FromSeconds(1.5), SqlExportItemState.Done, null),
                    new("loja", "v", DbObjectKind.View, 0, 0, TimeSpan.FromSeconds(0.1), SqlExportItemState.Done, null),
                ];
                progress.Report(new SqlExportProgress(items, 1234, TimeSpan.FromSeconds(1)));
                return Task.FromResult(new SqlExportResult(false, 2048, items, "texto", TimeSpan.FromSeconds(2)));
            },
            initialDatabase: "loja",
            initialObjects: null);
        dialog.Show();
        await dialog.Loading;

        // The focused database comes fully checked; the recent entry of the chosen output fills in the name.
        var loja = (CheckBox)((TreeViewItem)dialog.Tree.Items[0]!).Header!;
        Assert.Equal("loja", ((TextBlock)loja.Content!).Text);
        Assert.True(loja.IsChecked);
        Assert.Equal(@"C:\dumps\loja.sql", dialog.PathBox.Text);
        dialog.OutputCombo.SelectedIndex = 1;
        Assert.Equal(@"C:\dumps\loja.zip", dialog.PathBox.Text);

        // Drop implies Create and shows in bold as "Drop!!"; turning Create off turns Drop off.
        dialog.DropTablesBox.IsChecked = true;
        Assert.Equal("Drop!!", dialog.DropTablesBox.Content);
        dialog.CreateTablesBox.IsChecked = false;
        Assert.False(dialog.DropTablesBox.IsChecked);
        Assert.Equal("Drop", dialog.DropTablesBox.Content);
        dialog.CreateTablesBox.IsChecked = true;

        // Unchecking an object leaves the database at "some".
        dialog.SetObjectChecked("loja", "p1", false);
        Assert.Null(loja.IsChecked);

        dialog.OutputCombo.SelectedIndex = 3;
        Assert.False(dialog.PathBox.IsEnabled);
        dialog.ExportButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await RealServer.WaitAsync(() => dialog.StatusText.Text?.StartsWith("Done", StringComparison.Ordinal) == true ? "ok" : null);

        Assert.NotNull(requested);
        Assert.Equal(SqlExportTarget.Clipboard, requested.Target);
        var database = Assert.Single(requested.Databases);
        Assert.Equal("loja", database.Name);
        Assert.Equal(["clientes", "v"], database.Objects.Select(o => o.Ref.Name));
        Assert.True(requested.Dump.CreateTables);
        Assert.False(requested.Dump.DropTables);
        Assert.Equal(DumpDataMode.Insert, requested.Dump.Data);
        Assert.Equal(1024, requested.Dump.MaxInsertSizeKb);
        Assert.True(requested.Dump.AddComments);
        Assert.True(requested.Dump.RemoveDefiner);

        var rows = dialog.ResultRows;
        Assert.Equal(2, rows.Count);
        Assert.Equal("clientes", rows[0][1].Text);
        Assert.StartsWith("5 / 50%", rows[0][2].Text, StringComparison.Ordinal);
        Assert.Equal("v (view)", rows[1][1].Text);
        Assert.Equal("done", rows[1][2].Text);
        Assert.Equal("Export", dialog.ExportButton.Content);
        Assert.Equal(3, settings.Output);
        dialog.Close();
    }
}
