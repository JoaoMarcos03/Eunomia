using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Eunomia.App.Settings;
using Eunomia.UI.Controls.Tree;
using Eunomia.UI.Features.QueryTab;
using static Eunomia.UI.Headless.Tests.RealServer;

namespace Eunomia.UI.Headless.Tests;

/// <summary>
/// Carga lenta no Data tab (tabela grande num servidor lento; aqui um filtro com <c>SLEEP</c> por linha): escolher outra
/// coisa na árvore ou correr uma query cancela-a, em vez de a ligação primária ficar ocupada até ao fim (feedback da
/// beta, 9 out 2026). Cada carga lenta demora 25 s ou mais; o que vem a seguir tem de aparecer muito antes.
/// </summary>
public sealed class DataLoadCancelTests
{
    private const string Digits = "(SELECT 0 n UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9)";

    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(10);

    private static TreeNode Child(TreeNode parent, string name) => parent.Children!.Single(n => n.Name == name);

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task EscolherOutraCoisaCancelaACargaDoDataTab()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "Sem servidor de teste.");
        var db = NewDatabaseName("eunomia_dl_");
        var db2 = NewDatabaseName("eunomia_dl2_");
        await ExecAsync(cs, $"CREATE DATABASE `{db}`; CREATE DATABASE `{db2}`; " +
            $"CREATE TABLE `{db}`.slow (id INT PRIMARY KEY); INSERT INTO `{db}`.slow SELECT a.n + 10 * b.n + 1 FROM {Digits} a, {Digits} b WHERE a.n + 10 * b.n < 40; " +
            $"CREATE TABLE `{db}`.tail (id INT PRIMARY KEY); INSERT INTO `{db}`.tail SELECT id FROM `{db}`.slow; " +
            $"CREATE TABLE `{db}`.small (id INT PRIMARY KEY); INSERT INTO `{db}`.small VALUES (1), (2), (3); " +
            $"CREATE TABLE `{db2}`.other (id INT PRIMARY KEY); INSERT INTO `{db2}`.other VALUES (1), (2);");
        try
        {
            var settings = new AppSettings();
            settings.Data.RowsPerStep = 5;
            var (window, context, session, connector, log) = CreateShell(cs, $"{db};{db2}", settings);
            Assert.True(await window.ConnectAsync(window, session, null));
            var model = window.Tree.Tree.Model!;
            var root = await WaitAsync(() => model.Roots.FirstOrDefault());
            await WaitAsync(() => root.Children?.Count == 2 ? "ok" : null);
            var dbNode = root.Children!.Single(n => n.Name == db);
            var db2Node = root.Children!.Single(n => n.Name == db2);
            await model.ExpandAsync(dbNode);
            await model.ExpandAsync(db2Node);
            var host = connector.Open.Single().Host;
            window.ShowDataTab();

            bool Showing(string name, int rows) =>
                window.DataBrowser?.Table.Ref.Name == name && window.DataGrid.RowCount == rows && !host.IsPrimaryBusy;

            async Task<TimeSpan> ShowAsync(TreeNode node, int rows)
            {
                var clock = Stopwatch.StartNew();
                window.Tree.Tree.FocusedNode = node;
                await WaitAsync(() => Showing(node.Name, rows) ? "ok" : null, TimeSpan.FromSeconds(60));
                return clock.Elapsed;
            }

            async Task FilterAsync(string filter)
            {
                window.DataView.FilterEditor.Text = filter;
                Assert.True(await context.Commands.ExecuteAsync("filter.apply"));
            }

            // "slow": cada linha do filtro dorme 5 s (primeira página de 5 linhas: 25 s).
            await ShowAsync(Child(dbNode, "slow"), 5);
            await FilterAsync("SLEEP(5) = 0");
            await WaitAsync(() => host.IsPrimaryBusy ? "ok" : null);

            // Uma tabela de outra base: o USE já não espera pelo fim da carga.
            Assert.True(await ShowAsync(Child(db2Node, "other"), 2) < Quick);

            // O nó da base: a carga acaba logo e a ligação primária fica livre.
            window.Tree.Tree.FocusedNode = Child(dbNode, "slow");
            await WaitAsync(() => host.IsPrimaryBusy ? "ok" : null);
            var clock = Stopwatch.StartNew();
            window.Tree.Tree.FocusedNode = dbNode;
            await WaitAsync(() => !host.IsPrimaryBusy ? "ok" : null, TimeSpan.FromSeconds(60));
            Assert.True(clock.Elapsed < Quick);

            // Uma query na mesma sessão: Run fica ativo e a carga cede-lhe a ligação. (Da base, o objeto abre no Table tab.)
            window.Tree.Tree.FocusedNode = Child(dbNode, "slow");
            window.ShowDataTab();
            await WaitAsync(() => host.IsPrimaryBusy ? "ok" : null);
            window.Tabs.SelectedItem = window.Tabs.QueryTabs[0].Item;
            window.SqlEditor.Text = "SELECT 42 AS answer";
            Assert.True(context.Commands.StateOf("query.run").CanExecute);
            clock.Restart();
            Assert.True(await context.Commands.ExecuteAsync("query.run"));
            Assert.True(clock.Elapsed < Quick);
            var view = Assert.IsType<QueryTabView>(window.Tabs.QueryTabs[0].Content);
            Assert.Equal("42", view.ResultGrids[0].Control.GetDisplayText(0, 0));

            // Show all a meio (cada linha acima da 5 dorme 1 s: 35 s) e outra tabela da mesma base.
            window.Tree.Tree.FocusedNode = Child(dbNode, "tail");
            window.ShowDataTab();
            await WaitAsync(() => Showing("tail", 5) ? "ok" : null);
            await FilterAsync("SLEEP(IF(id > 5, 1, 0)) = 0");
            await WaitAsync(() => Showing("tail", 5) && window.DataBrowser!.Options.Filter is { } f && f.StartsWith("SLEEP", StringComparison.Ordinal) ? "ok" : null);
            var showAll = context.Commands.ExecuteAsync("data.showAll").AsTask();
            await WaitAsync(() => host.IsPrimaryBusy ? "ok" : null);
            Assert.True(await ShowAsync(Child(dbNode, "small"), 3) < Quick);
            await showAll;

            // Voltar à tabela abandonada carrega-a de novo; nenhum cancelamento aparece como erro.
            await ShowAsync(Child(dbNode, "tail"), 5);
            Assert.DoesNotContain(log.Snapshot(), l => l.Kind == Eunomia.Presentation.Logging.LogLineKind.Error);

            await connector.DisconnectAllAsync();
            window.Close();
        }
        finally
        {
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`; DROP DATABASE IF EXISTS `{db2}`");
        }
    }
}
