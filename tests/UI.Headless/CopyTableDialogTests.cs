using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Eunomia.App.Objects;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Results;
using Eunomia.UI.Controls.Tree;
using Eunomia.UI.Features.Transfer;
using MySqlConnector;
using static Eunomia.UI.Headless.Tests.RealServer;
using ColumnDefinition = Eunomia.Drivers.Abstractions.ColumnDefinition;

namespace Eunomia.UI.Headless.Tests;

/// <summary>Copy table (Create new ▸ Table copy; anexo B §5.1): o diálogo e o fluxo pela árvore contra o servidor.</summary>
public sealed class CopyTableDialogTests
{
    private static ColumnDefinition Column(string name, string type, bool autoIncrement = false) =>
        new(name, type, new LogicalType(LogicalTypeKind.Text), !autoIncrement, null, autoIncrement, false, false, null);

    internal static TableDefinition People { get; } = new(
        new DbObjectRef("shop", "shop", "people", DbObjectKind.Table),
        [Column("id", "INT", autoIncrement: true), Column("first_name", "VARCHAR(20)"), Column("city_id", "INT")],
        [new IndexDefinition("PRIMARY", IndexKind.Primary, ["id"]), new IndexDefinition("ix_city", IndexKind.Key, ["city_id"])],
        [new ForeignKeyDefinition("fk_city", ["city_id"], new DbObjectRef("shop", "shop", "cities", DbObjectKind.Table), ["id"], ForeignKeyAction.Restrict, ForeignKeyAction.Restrict)],
        [],
        new Dictionary<string, string>());

    internal static CopyTableServices Fake(params string[] existing) =>
        new(People, 1234, ["shop", "archive"], (db, name, _) => Task.FromResult(existing.Contains(db + "." + name, StringComparer.OrdinalIgnoreCase)));

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task ValoresPorOmissaoEGruposComContagem()
    {
        using var culture = new CultureScope(CultureInfo.InvariantCulture);
        var dialog = new CopyTableDialog(Fake());
        dialog.Show();

        Assert.Equal("people_copy", dialog.NameBox.Text);
        Assert.Equal("shop", dialog.DatabaseBox.SelectedItem);
        var (columns, items) = dialog.GroupBoxes("Columns");
        Assert.Equal("Columns (3/3)", ((TextBlock)columns.Content!).Text);
        Assert.Equal("first_name", ((TextBlock)items[1].Content!).Text);
        Assert.Equal("Data (~1,234 rows)", ((TextBlock)dialog.DataBox.Content!).Text);

        // Um elemento desmarcado deixa o grupo indeterminado; um clique no grupo volta a marcar tudo e outro desmarca.
        items[2].IsChecked = false;
        Assert.Null(columns.IsChecked);
        Assert.Equal("Columns (2/3)", ((TextBlock)columns.Content!).Text);
        // O clique segue o ciclo do Avalonia (true → null → false → true): do indeterminado vai para false; de true, para null.
        columns.IsChecked = false;
        Assert.All(items, i => Assert.True(i.IsChecked));
        columns.IsChecked = null;
        Assert.All(items, i => Assert.False(i.IsChecked));
        Assert.Null(dialog.Request());
        Assert.Equal("Select at least one column.", dialog.StatusText);

        // Sem dados, o WHERE fica desligado e não vai no pedido.
        foreach (var item in items)
        {
            item.IsChecked = true;
        }
        dialog.WhereBox.Text = "id > 10";
        dialog.DataBox.IsChecked = false;
        Assert.False(dialog.WhereBox.IsEnabled);
        var request = Assert.IsType<TableCopyRequest>(dialog.Request());
        Assert.Equal(("shop", "people_copy", false, (string?)null), (request.TargetDatabase, request.TargetName, request.Data, request.Where));
        Assert.Equal(["id", "first_name", "city_id"], request.Columns);
        Assert.Equal(["PRIMARY", "ix_city"], request.Indexes);
        Assert.Equal(["fk_city"], request.ForeignKeys);
        dialog.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task DestinoQueJaExisteFicaRecusadoNoDialogo()
    {
        var dialog = new CopyTableDialog(Fake("shop.people_copy"));
        dialog.Show();

        await dialog.AcceptAsync();

        Assert.True(dialog.IsVisible);
        Assert.Equal("Table \"shop.people_copy\" already exists. Choose another name, or drop it first.", dialog.StatusText);
        dialog.Close();
    }

    [AvaloniaTheory(Timeout = UiTimeouts.Default)]
    [InlineData("light")]
    [InlineData("dark")]
    public async Task Snapshot(string theme)
    {
        using var culture = new CultureScope(CultureInfo.InvariantCulture);
        var dialog = new CopyTableDialog(Fake()) { RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light };
        dialog.Show();
        dialog.GroupBoxes("Indexes").Items[1].IsChecked = false;
        dialog.WhereBox.Text = "city_id IS NOT NULL";
        await Task.Yield();
        dialog.CaptureRenderedFrame();

        Tests.Snapshot.Match(dialog.CaptureRenderedFrame(), $"dialog-copy-table-{theme}");
        dialog.Close();
    }

    private static async Task<string?> ScalarAsync(string cs, string sql)
    {
        await using var connection = new MySqlConnection(cs);
        await connection.OpenAsync();
        await using var cmd = new MySqlCommand(sql, connection);
        return Convert.ToString(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task DaArvoreATabelaNova()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "Sem servidor de teste.");
        var db = NewDatabaseName("eunomia_ct_");
        await ExecAsync(cs, $"CREATE DATABASE `{db}`; CREATE TABLE `{db}`.people (id INT AUTO_INCREMENT PRIMARY KEY, name VARCHAR(20)); " +
            $"INSERT INTO `{db}`.people (name) VALUES ('Ana'), ('Rui'), ('Eva');");
        try
        {
            var (window, context, session, connector, _) = CreateShell(cs, db);
            Assert.True(await window.ConnectAsync(window, session, null));
            var model = window.Tree.Tree.Model!;
            var root = await WaitAsync(() => model.Roots.FirstOrDefault());
            var dbNode = await WaitAsync(() => root.Children?.FirstOrDefault(n => n.Name == db));
            await model.ExpandAsync(dbNode);
            window.Tree.Tree.FocusedNode = TreeModel.ObjectsOf(dbNode).Single(n => n.Name == "people");

            // O teste conduz o diálogo modal; o comando só acaba quando ele fecha.
            UnexpectedDialogGuardAttribute.Allow(typeof(CopyTableDialog));
            var command = context.Commands.ExecuteAsync("object.copyTable").AsTask();
            var dialog = await WaitAsync(() => window.CopyTableWindow);
            dialog.WhereBox.Text = "name <> 'Rui'";
            await dialog.AcceptAsync();
            Assert.True(await command);

            Assert.Equal("Ana,Eva", await ScalarAsync(cs, $"SELECT GROUP_CONCAT(name ORDER BY id) FROM `{db}`.people_copy"));
            Assert.Equal("3", await ScalarAsync(cs, $"SELECT COUNT(*) FROM `{db}`.people"));
            await WaitAsync(() => window.Tree.Tree.FocusedNode is { Name: "people_copy" } ? "ok" : null);

            await connector.DisconnectAllAsync();
            window.Close();
        }
        finally
        {
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`");
        }
    }
}
