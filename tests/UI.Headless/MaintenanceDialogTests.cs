using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Eunomia.App.Objects;
using Eunomia.Drivers.Abstractions;
using Eunomia.Drivers.MySql;
using Eunomia.UI.Features.Transfer;
using MySqlConnector;
using static Eunomia.UI.Headless.Tests.RealServer;

namespace Eunomia.UI.Headless.Tests;

/// <summary>Maintenance (Tools ▸ Maintenance; anexo B §2.2): o diálogo com serviços falsos e o fluxo contra o servidor.</summary>
public sealed class MaintenanceDialogTests
{
    private static readonly MySqlDialect Dialect = new(MySqlConnectionFactory.ParseIdentity("10.4.32-MariaDB", null));

    /// <summary>Base "shop" com duas tabelas, uma view e uma rotina; "people" escolhida na árvore.</summary>
    internal static MaintenanceServices Fake(Func<MaintenanceOperation, DbObjectRef, MaintenanceOptions, CancellationToken, Task<IReadOnlyList<MaintenanceMessage>>>? run = null,
        bool confirm = true, List<string>? questions = null) => new(
        ["shop", "test"],
        "shop",
        ["people"],
        (db, _) => Task.FromResult<IReadOnlyList<DbObjectInfo>>(db == "shop"
            ?
            [
                new(new DbObjectRef("shop", "shop", "people", DbObjectKind.Table), DataBytes: 16384, IndexBytes: 0),
                new(new DbObjectRef("shop", "shop", "orders", DbObjectKind.Table), DataBytes: 49152, IndexBytes: 16384),
                new(new DbObjectRef("shop", "shop", "v_people", DbObjectKind.View)),
                new(new DbObjectRef("shop", "shop", "p_total", DbObjectKind.Procedure)),
            ]
            : []),
        Dialect.MaintenanceOptionsOf,
        (_, operation, count, options) =>
        {
            questions?.Add(TableMaintenance.Question(operation, count, options));
            return Task.FromResult(confirm);
        },
        run ?? ((operation, table, _, _) => Task.FromResult<IReadOnlyList<MaintenanceMessage>>(
            [new("shop." + table.Name, TableMaintenance.Caption(operation).ToLowerInvariant(), "status", "OK")])));

    private static async Task<string?> ScalarAsync(string cs, string sql)
    {
        await using var connection = new MySqlConnection(cs);
        await connection.OpenAsync();
        await using var cmd = new MySqlCommand(sql, connection);
        return Convert.ToString(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<MaintenanceDialog> OpenAsync(MaintenanceServices services)
    {
        var dialog = new MaintenanceDialog(services);
        dialog.Show();
        await WaitAsync(() => dialog.ObjectBoxes.Count == 3 ? "ok" : null);
        return dialog;
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task SoTabelasEViewsComOsEscolhidosNaArvoreMarcados()
    {
        using var culture = new CultureScope(CultureInfo.InvariantCulture);
        var dialog = await OpenAsync(Fake());

        // Nomes com "_" aparecem inteiros (o AccessText tirava o primeiro "_").
        Assert.Equal(["people", "orders", "v_people"], dialog.ObjectBoxes.Select(b => ((TextBlock)b.Content!).Text));
        Assert.Equal([true, false, false], dialog.ObjectBoxes.Select(b => b.IsChecked == true));
        Assert.StartsWith("1 selected objects, size: ", dialog.SelectionText, StringComparison.Ordinal);
        dialog.ObjectBoxes[2].IsChecked = true;
        Assert.StartsWith("2 selected objects, size: ", dialog.SelectionText, StringComparison.Ordinal);
        dialog.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task OpcoesAtivasConformeAOperacao()
    {
        var dialog = await OpenAsync(Fake());
        bool Enabled(MaintenanceOptions option) => dialog.OptionBox(option).IsEnabled;

        dialog.OperationBox.SelectedIndex = (int)MaintenanceOperation.Check;
        Assert.True(Enabled(MaintenanceOptions.Quick) && Enabled(MaintenanceOptions.ForUpgrade));
        Assert.False(Enabled(MaintenanceOptions.UseFrm));
        dialog.OperationBox.SelectedIndex = (int)MaintenanceOperation.Analyze;
        Assert.All(Enum.GetValues<MaintenanceOptions>().Where(o => o != MaintenanceOptions.None), o => Assert.False(Enabled(o)));
        dialog.OperationBox.SelectedIndex = (int)MaintenanceOperation.Checksum;
        Assert.True(Enabled(MaintenanceOptions.Quick) && Enabled(MaintenanceOptions.Extended));
        Assert.False(Enabled(MaintenanceOptions.Fast));
        dialog.OperationBox.SelectedIndex = (int)MaintenanceOperation.Repair;
        Assert.True(Enabled(MaintenanceOptions.UseFrm));
        Assert.False(Enabled(MaintenanceOptions.Medium));
        dialog.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task ErroNumObjetoNaoParaOsOutrosESoAsOpcoesAtivasSeguem()
    {
        var seen = new List<(string Table, MaintenanceOptions Options)>();
        var dialog = await OpenAsync(Fake((operation, table, options, _) =>
        {
            seen.Add((table.Name, options));
            return table.Name == "orders"
                ? throw new DbOperationException(new DbError(1146, "42S02", "Table 'shop.orders' doesn't exist"))
                : Task.FromResult<IReadOnlyList<MaintenanceMessage>>(
                    [new("shop." + table.Name, "check", "note", "The storage engine for the table doesn't support check"), new("shop." + table.Name, "check", "status", "OK")]);
        }));
        foreach (var box in dialog.ObjectBoxes)
        {
            box.IsChecked = true;
        }
        dialog.OptionBox(MaintenanceOptions.Quick).IsChecked = true;
        dialog.OptionBox(MaintenanceOptions.UseFrm).IsChecked = true;

        await dialog.ExecuteAsync();

        Assert.Equal(["people", "orders", "v_people"], seen.Select(s => s.Table));
        Assert.All(seen, s => Assert.Equal(MaintenanceOptions.Quick, s.Options));
        var rows = dialog.Results.Roots;
        Assert.Equal(5, rows.Count);
        Assert.Equal(["shop.orders", "check", "error", "Table 'shop.orders' doesn't exist"], rows[2].Cells);
        Assert.NotNull(rows[2].Background);
        Assert.NotNull(rows[0].Background);
        Assert.Null(rows[1].Background);
        Assert.Equal("3 object(s) processed.", dialog.StatusText);
        dialog.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task OptimizePerguntaENaoCorreSemOk()
    {
        var questions = new List<string>();
        var runs = 0;
        var dialog = await OpenAsync(Fake((_, _, _, _) =>
        {
            runs++;
            return Task.FromResult<IReadOnlyList<MaintenanceMessage>>([]);
        }, confirm: false, questions));
        dialog.OperationBox.SelectedIndex = (int)MaintenanceOperation.Optimize;

        await dialog.ExecuteAsync();
        dialog.OperationBox.SelectedIndex = (int)MaintenanceOperation.Check;
        await dialog.ExecuteAsync();

        Assert.StartsWith("Optimize 1 table(s)?", Assert.Single(questions), StringComparison.Ordinal);
        Assert.Equal(1, runs);
        dialog.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task CancelParaDepoisDoObjetoEmCurso()
    {
        var second = new TaskCompletionSource();
        var dialog = await OpenAsync(Fake(async (operation, table, _, ct) =>
        {
            if (table.Name == "orders")
            {
                second.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            return [new("shop." + table.Name, "check", "status", "OK")];
        }));
        foreach (var box in dialog.ObjectBoxes)
        {
            box.IsChecked = true;
        }

        var running = dialog.ExecuteAsync();
        await second.Task;
        var close = dialog.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "btnClose");
        Assert.Equal("Cancel", close.Content);
        Assert.True(dialog.IsRunning);
        close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await running;

        Assert.Single(dialog.Results.Roots);
        Assert.Equal("Processing cancelled: 1 of 3 object(s) done.", dialog.StatusText);
        Assert.Equal("Close", close.Content);
        Assert.True(dialog.IsVisible);
        dialog.Close();
    }

    [AvaloniaTheory(Timeout = UiTimeouts.Default)]
    [InlineData("light")]
    [InlineData("dark")]
    public async Task Snapshot(string theme)
    {
        using var culture = new CultureScope(CultureInfo.InvariantCulture);
        var dialog = new MaintenanceDialog(Fake((operation, table, _, _) => Task.FromResult<IReadOnlyList<MaintenanceMessage>>(table.Name switch
        {
            "orders" => [new("shop.orders", "check", "error", "Table 'shop.orders' is marked as crashed"), new("shop.orders", "check", "status", "Operation failed")],
            "v_people" => [new("shop.v_people", "check", "note", "Unknown storage engine 'InnoDB'"), new("shop.v_people", "check", "status", "OK")],
            _ => [new("shop." + table.Name, "check", "status", "OK")],
        })))
        {
            RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light,
        };
        dialog.Show();
        await WaitAsync(() => dialog.ObjectBoxes.Count == 3 ? "ok" : null);
        dialog.ObjectBoxes[1].IsChecked = true;
        dialog.ObjectBoxes[2].IsChecked = true;
        dialog.OptionBox(MaintenanceOptions.Quick).IsChecked = true;
        await dialog.ExecuteAsync();
        dialog.CaptureRenderedFrame();

        Tests.Snapshot.Match(dialog.CaptureRenderedFrame(), $"dialog-maintenance-{theme}");
        dialog.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task DaArvoreAoResultadoNoServidor()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "Sem servidor de teste.");
        var db = NewDatabaseName("eunomia_mu_");
        await ExecAsync(cs, $"CREATE DATABASE `{db}`; CREATE TABLE `{db}`.people (id INT PRIMARY KEY, name VARCHAR(20)); INSERT INTO `{db}`.people VALUES (1, 'Ana'); " +
            $"CREATE TABLE `{db}`.orders (id INT PRIMARY KEY);");
        try
        {
            var (window, context, session, connector, _) = CreateShell(cs, db);
            var questions = new List<string>();
            window.ConfirmOverride = (_, message, _) =>
            {
                questions.Add(message);
                return Task.FromResult((true, false));
            };
            Assert.True(await window.ConnectAsync(window, session, null));
            var model = window.Tree.Tree.Model!;
            var root = await WaitAsync(() => model.Roots.FirstOrDefault());
            var dbNode = await WaitAsync(() => root.Children?.FirstOrDefault(n => n.Name == db));
            await model.ExpandAsync(dbNode);
            var people = await WaitAsync(() => dbNode.Children?.FirstOrDefault(n => n.Name == "people"));
            window.Tree.Tree.FocusedNode = people;

            // O teste conduz o diálogo modal.
            UnexpectedDialogGuardAttribute.Allow(typeof(MaintenanceDialog));
            // O comando só acaba quando o diálogo modal fecha.
            var command = context.Commands.ExecuteAsync("tools.maintenance").AsTask();
            var dialog = await WaitAsync(() => window.MaintenanceWindow is { ObjectBoxes.Count: 2 } d ? d : null);
            Assert.Equal(["people"], dialog.ObjectBoxes.Where(b => b.IsChecked == true).Select(b => (string)b.Tag!));

            await dialog.ExecuteAsync();
            Assert.Contains(dialog.Results.Roots, r => r.Cells[0] == $"{db}.people" && r.Cells[2] == "status" && r.Cells[3] == "OK");

            dialog.OperationBox.SelectedIndex = (int)MaintenanceOperation.Optimize;
            await dialog.ExecuteAsync();
            Assert.Contains($"Session: {session.Path}", Assert.Single(questions), StringComparison.Ordinal);
            Assert.Contains(dialog.Results.Roots, r => r.Cells[1] == "optimize" && r.Cells[3] == "OK");
            Assert.Equal("Ana", await ScalarAsync(cs, $"SELECT name FROM `{db}`.people WHERE id = 1"));

            dialog.Close();
            Assert.True(await command);
            Assert.Null(window.MaintenanceWindow);
            await connector.DisconnectAllAsync();
            window.Close();
        }
        finally
        {
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`");
        }
    }
}
