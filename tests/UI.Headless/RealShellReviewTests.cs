using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Eunomia.UI.Controls.Tree;
using Eunomia.UI.Features.QueryTab;
using static Eunomia.UI.Headless.Tests.RealServer;

namespace Eunomia.UI.Headless.Tests;

/// <summary>
/// Fase 13: a janela principal ligada ao servidor de teste, nos ecrãs do dia a dia (Host, Database, Table, Data e
/// Query com resultados), em claro e escuro. Os tempos e a versão do servidor mudam, por isso não há baseline: o
/// teste verifica os fluxos e, com <c>EUNOMIA_REVIEW_DIR</c>, grava os PNG para revisão.
/// </summary>
public sealed class RealShellReviewTests
{
    private static TreeNode Child(TreeNode parent, string name) => parent.Children!.Single(n => n.Name == name);

    [AvaloniaTheory(Timeout = UiTimeouts.Default)]
    [InlineData("light")]
    [InlineData("dark")]
    public async Task EcrasDoDiaADia(string theme)
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "Sem servidor de teste.");
        using var culture = new CultureScope(CultureInfo.InvariantCulture);
        var db = NewDatabaseName("eunomia_rv_");
        await ExecAsync(cs, $"CREATE DATABASE `{db}`; " +
            $"CREATE TABLE `{db}`.customers (id INT UNSIGNED AUTO_INCREMENT PRIMARY KEY, name VARCHAR(120) NOT NULL, email VARCHAR(200) NULL UNIQUE, created DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP, balance DECIMAL(10,2) NULL); " +
            $"INSERT INTO `{db}`.customers (name, email, created, balance) VALUES ('Ana Silva', 'ana@example.com', '2026-01-15 09:30:00', 120.50), ('Rui Costa', NULL, '2026-02-01 18:04:05', NULL), ('Marta Sousa', 'marta@example.com', '2026-03-10 07:00:00', 99.90); " +
            $"CREATE TABLE `{db}`.orders (id INT PRIMARY KEY, customer_id INT UNSIGNED, total DECIMAL(10,2), FOREIGN KEY (customer_id) REFERENCES `{db}`.customers (id)); " +
            $"CREATE VIEW `{db}`.v_totals AS SELECT customer_id, SUM(total) AS total FROM `{db}`.orders GROUP BY customer_id;");
        try
        {
            var (window, context, session, _, _) = CreateShell(cs, db, color: 0x2E7D32);
            window.RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
            Assert.True(await window.ConnectAsync(window, session, null));
            var tabs = window.Tabs;
            var model = window.Tree.Tree.Model!;
            var root = await WaitAsync(() => model.Roots.FirstOrDefault());
            var dbNode = await WaitAsync(() => root.Children?.FirstOrDefault(n => n.Name == db));

            // Host tab.
            window.Tree.Tree.FocusedNode = root;
            await WaitAsync(() => ReferenceEquals(tabs.SelectedItem, tabs.HostTab) ? "ok" : null);
            await Settle();
            Save(window, $"real-host-{theme}");
            var host = window.HostView;
            host.SelectedItem = host.VariablesTab;
            await WaitAsync(() => host.VariableList.Roots.Count > 0 ? "ok" : null);
            await Settle();
            Save(window, $"real-variables-{theme}");
            host.SelectedItem = host.CommandStatisticsTab;
            await WaitAsync(() => host.CommandList.Roots.Count > 0 ? "ok" : null);
            await Settle();
            Save(window, $"real-commands-{theme}");
            host.SelectedItem = host.DatabasesTab;

            // Database tab.
            await model.ExpandAsync(dbNode);
            window.Tree.Tree.FocusedNode = dbNode;
            await WaitAsync(() => ReferenceEquals(tabs.SelectedItem, tabs.DatabaseTab) ? "ok" : null);
            await Settle();
            Save(window, $"real-database-{theme}");

            // Table tab (table editor) e Data tab.
            var customers = TreeModel.ObjectsOf(dbNode).Single(n => n.Name == "customers");
            window.Tree.Tree.FocusedNode = customers;
            await WaitAsync(() => ReferenceEquals(tabs.SelectedItem, tabs.TableTab) ? "ok" : null);
            await Settle();
            Save(window, $"real-table-{theme}");
            tabs.SelectedItem = tabs.DataTab;
            await WaitAsync(() => window.DataGrid.RowCount == 3 ? "ok" : null);
            await window.DataGrid.PendingAutoSize;
            window.DataGrid.Focus();
            window.DataGrid.Selection.Focus(1, 2);
            await Settle();
            Save(window, $"real-data-{theme}");

            // View no editor por código.
            window.Tree.Tree.FocusedNode = TreeModel.ObjectsOf(dbNode).Single(n => n.Name == "v_totals");
            tabs.SelectedItem = tabs.TableTab;
            await WaitAsync(() => window.CodeEditor is { Code.Length: > 0 } ? "ok" : null);
            await Settle();
            Save(window, $"real-view-{theme}");

            // Query tab com resultados.
            window.Tabs.SelectedItem = window.Tabs.QueryTabs[0].Item;
            window.SqlEditor.Text = $"SELECT c.id, c.name, c.balance\nFROM `{db}`.customers c\nWHERE c.balance IS NOT NULL\nORDER BY c.name;";
            Assert.True(await context.Commands.ExecuteAsync("query.run"));
            var view = Assert.IsType<QueryTabView>(window.Tabs.QueryTabs[0].Content);
            await WaitAsync(() => view.ActiveGrid is { RowCount: 2 } ? "ok" : null);
            await view.ActiveGrid!.PendingAutoSize;
            await Settle();
            Save(window, $"real-query-{theme}");
            window.Close();
        }
        finally
        {
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`");
        }
    }

    private static async Task Settle()
    {
        for (var i = 0; i < 10; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(30);
        }
    }

    private static void Save(Window window, string name)
    {
        window.CaptureRenderedFrame();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (Environment.GetEnvironmentVariable("EUNOMIA_REVIEW_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            frame.Save(Path.Combine(dir, name + ".png"), PngBitmapEncoderOptions.Default);
        }
    }
}
