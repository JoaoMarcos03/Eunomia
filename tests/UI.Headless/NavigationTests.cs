using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Eunomia.UI.Controls.SqlEditor;
using Eunomia.UI.Controls.Tree;
using static Eunomia.UI.Headless.Tests.RealServer;

namespace Eunomia.UI.Headless.Tests;

/// <summary>Seleção na árvore → tabs, captions e visibilidade (FAM-L04, L08; anexo D A11) contra o servidor real.</summary>
public sealed class NavigationTests
{
    private static TreeNode Child(TreeNode parent, string name) => parent.Children!.Single(n => n.Name == name);

    private static string Caption(TabItem tab) => ((StackPanel)tab.Header!).Children.OfType<TextBlock>().First().Text!;

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task SelecaoEscolheATabComoNoHeidiSql()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "Sem servidor de teste.");
        var db = NewDatabaseName("eunomia_nv_");
        await ExecAsync(cs, $"CREATE DATABASE `{db}`; CREATE TABLE `{db}`.people (id INT PRIMARY KEY); INSERT INTO `{db}`.people VALUES (1), (2); " +
            $"CREATE TABLE `{db}`.pets (id INT); CREATE PROCEDURE `{db}`.p() SELECT 1;");
        try
        {
            var (window, _, session, connector, _) = CreateShell(cs, db);
            Assert.True(await window.ConnectAsync(window, session, null));
            var tabs = window.Tabs;
            var model = window.Tree.Tree.Model!;
            var root = await WaitAsync(() => model.Roots.FirstOrDefault());
            var dbNode = await WaitAsync(() => root.Children?.FirstOrDefault());
            await model.ExpandAsync(dbNode);
            var open = connector.Open[0];

            // Sessão → Host (caption = sessão); só Host e Query visíveis.
            window.Tree.Tree.FocusedNode = root;
            await WaitAsync(() => ReferenceEquals(tabs.SelectedItem, tabs.HostTab) ? "ok" : null);
            Assert.Equal("it", Caption(tabs.HostTab));
            Assert.False(tabs.DatabaseTab.IsVisible);
            Assert.False(tabs.TableTab.IsVisible);
            Assert.False(tabs.DataTab.IsVisible);

            // Base → USE e Database tab.
            window.Tree.Tree.FocusedNode = dbNode;
            await WaitAsync(() => ReferenceEquals(tabs.SelectedItem, tabs.DatabaseTab) ? "ok" : null);
            Assert.Equal(db, open.Host.CurrentDatabase);
            Assert.True(dbNode.IsCurrent);
            Assert.Equal("Database: " + db, Caption(tabs.DatabaseTab));
            Assert.False(tabs.DataTab.IsVisible);

            // Tabela vinda do Database tab → Table tab com o CREATE code; Data visível.
            window.Tree.Tree.FocusedNode = Child(dbNode, "people");
            await WaitAsync(() => ReferenceEquals(tabs.SelectedItem, tabs.TableTab) ? "ok" : null);
            Assert.Equal("Table: people", Caption(tabs.TableTab));
            Assert.True(tabs.DataTab.IsVisible);
            await WaitAsync(() => (tabs.TableTab.Content as UI.Features.TableEditor.TableEditorView)?.CreateCodeText.Contains("CREATE TABLE", StringComparison.Ordinal) == true ? "ok" : null);
            await WaitAsync(() => open.Metadata.CachedTable(((Shell.Sessions.TreeObjectTag)Child(dbNode, "people").Tag!).Object!));

            // No Data tab, outra tabela mantém o Data tab e carrega os dados.
            tabs.SelectedItem = tabs.DataTab;
            window.Tree.Tree.FocusedNode = Child(dbNode, "pets");
            await WaitAsync(() => window.DataGrid.RowCount == 0 && ReferenceEquals(tabs.SelectedItem, tabs.DataTab) ? "ok" : null);
            window.Tree.Tree.FocusedNode = Child(dbNode, "people");
            await WaitAsync(() => window.DataGrid.RowCount == 2 ? "ok" : null);
            Assert.Same(tabs.DataTab, tabs.SelectedItem);

            // Procedure: sem Data tab, passa para o Table tab com o tipo na caption.
            window.Tree.Tree.FocusedNode = Child(dbNode, "p");
            await WaitAsync(() => ReferenceEquals(tabs.SelectedItem, tabs.TableTab) ? "ok" : null);
            Assert.Equal("Procedure: p", Caption(tabs.TableTab));
            Assert.False(tabs.DataTab.IsVisible);

            // Com uma query tab ativa, a seleção não muda de tab.
            tabs.SelectedItem = tabs.QueryTabs[0].Item;
            window.Tree.Tree.FocusedNode = dbNode;
            window.Tree.Tree.FocusedNode = root;
            await Task.Delay(100);
            Assert.Same(tabs.QueryTabs[0].Item, await WaitAsync(() => tabs.SelectedItem));

            await connector.DisconnectAllAsync();
            window.Close();
        }
        finally
        {
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`");
        }
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task UseQueFalhaVoltaAoNoAnterior()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "Sem servidor de teste.");
        var db = NewDatabaseName("eunomia_nu_");
        var gone = db + "_gone";
        await ExecAsync(cs, $"CREATE DATABASE `{db}`; CREATE DATABASE `{gone}`;");
        try
        {
            var (window, _, session, connector, log) = CreateShell(cs, $"{db};{gone}");
            Assert.True(await window.ConnectAsync(window, session, null));
            var model = window.Tree.Tree.Model!;
            var root = await WaitAsync(() => model.Roots.FirstOrDefault());
            await WaitAsync(() => root.Children?.Count == 2 ? "ok" : null);
            var first = Child(root, db);
            window.Tree.Tree.FocusedNode = first;
            await WaitAsync(() => connector.Open[0].Host.CurrentDatabase == db ? "ok" : null);
            await ExecAsync(cs, $"DROP DATABASE `{gone}`");

            window.Tree.Tree.FocusedNode = Child(root, gone);

            await WaitAsync(() => ReferenceEquals(window.Tree.Tree.FocusedNode, first) ? "ok" : null);
            Assert.Contains(log.Snapshot(), l => l.Text.Contains("Unknown database", StringComparison.OrdinalIgnoreCase));
            // Anexo D A11: o erro do USE abre um diálogo antes de voltar ao nó anterior.
            Assert.Contains(UnexpectedDialogGuardAttribute.TakeOpened(), d => d.Contains("Unknown database", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(db, connector.Open[0].Host.CurrentDatabase);

            await connector.DisconnectAllAsync();
            window.Close();
        }
        finally
        {
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`; DROP DATABASE IF EXISTS `{gone}`");
        }
    }
}
