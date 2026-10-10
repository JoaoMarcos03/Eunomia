using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Eunomia.UI.Controls.Tree;
using static Eunomia.UI.Headless.Tests.RealServer;

namespace Eunomia.UI.Headless.Tests;

/// <summary>
/// Shell ligado a um servidor real (FAM-S07, L06): sessão como raiz da árvore, bases e tabelas, Data tab, Run (F9)
/// e status bar. Precisa do servidor de testes (tests/Integration/Start-TestServer.ps1); sem ele fica "skipped".
/// </summary>
public sealed class ShellSessionTests
{
    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task LigaMostraArvoreDadosERunNoServidorReal()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "Sem servidor de teste.");
        var database = NewDatabaseName("eunomia_ui_");
        await ExecAsync(cs, $"CREATE DATABASE `{database}`; CREATE TABLE `{database}`.`people` (id INT PRIMARY KEY, name VARCHAR(20)); INSERT INTO `{database}`.`people` VALUES (1,'Ana'),(2,'Rui'),(3,'Eva');");
        try
        {
            var (window, context, session, connector, log) = CreateShell(cs, database, color: 0x006400);

            Assert.True(await window.ConnectAsync(window, session, null));
            var root = await WaitAsync(() => window.Tree.Tree.Model!.Roots.FirstOrDefault());
            Assert.Equal("it", root.Name);
            Assert.Equal(0xFF006400u, root.SessionColor);
            var db = await WaitAsync(() => root.Children?.FirstOrDefault(c => c.Kind == TreeNodeKind.Database));
            Assert.Equal(database, db.Name);
            Assert.Same(connector.Open[0], window.ActiveSession);
            Assert.StartsWith("Connected: ", context.Status.Connected, StringComparison.Ordinal);
            // Uptime e hora do servidor numa só ida ao servidor (também no MySQL 8); o tooltip diz se a ligação vai cifrada.
            await WaitAsync(() => context.Status.Uptime.StartsWith("Uptime: ", StringComparison.Ordinal) && !context.Status.Uptime.Contains("unknown", StringComparison.Ordinal)
                && context.Status.ServerTime.StartsWith("Server time: ", StringComparison.Ordinal) ? "ok" : null, TimeSpan.FromSeconds(15));
            Assert.Contains("\nEncryption: ", context.Status.ServerTooltip, StringComparison.Ordinal);

            await window.Tree.Tree.Model!.ExpandAsync(db);
            var table = db.Children!.Single(c => c.Name == "people");
            Assert.Equal(TreeNodeKind.Table, table.Kind);
            window.ShowDataTab();
            window.Tree.Tree.FocusedNode = table;
            await WaitAsync(() => window.DataGrid.RowCount == 3 ? "ok" : null);

            window.Tabs.SelectedItem = window.Tabs.QueryTabs[0].Item;
            window.Tree.Tree.FocusedNode = db;
            window.SqlEditor.Text = $"SELECT name FROM `{database}`.`people` WHERE id > 1 ORDER BY id;";
            Dispatcher.UIThread.RunJobs();
            window.KeyPress(Key.F9, RawInputModifiers.None, PhysicalKey.F9, null);
            await WaitAsync(() => log.Snapshot().Any(l => l.Text.Contains("Affected rows", StringComparison.Ordinal)) ? "ok" : null);
            Assert.Contains(log.Snapshot(), l => l.Text.Contains("Found rows: 2", StringComparison.Ordinal));
            Assert.Contains(log.Snapshot(), l => l.Text.StartsWith("SELECT name FROM", StringComparison.Ordinal));

            await connector.DisconnectAllAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(window.Tree.Tree.Model!.Roots);
            window.Close();
        }
        finally
        {
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{database}`");
        }
    }
}
