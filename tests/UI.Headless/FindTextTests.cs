using System.Globalization;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Eunomia.UI.Features.QueryTab;
using static Eunomia.UI.Headless.Tests.RealServer;

namespace Eunomia.UI.Headless.Tests;

/// <summary>Find text on server (Ctrl+Shift+F; anexo B §2.4) contra o servidor de teste.</summary>
public sealed class FindTextTests
{
    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task ProcuraNasTabelasEAbreOsResultados()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "Sem servidor de teste.");
        using var culture = new CultureScope(CultureInfo.InvariantCulture);
        // O teste conduz o diálogo modal do Find text on server.
        UnexpectedDialogGuardAttribute.Allow(typeof(Eunomia.UI.Shell.Views.FindTextDialog));
        var db = NewDatabaseName("eunomia_ft_");
        await ExecAsync(cs, $"CREATE DATABASE `{db}`; " +
            $"CREATE TABLE `{db}`.people (id INT PRIMARY KEY, name VARCHAR(20)); INSERT INTO `{db}`.people VALUES (1, 'Ana Silva'), (2, 'Rui'), (3, 'ANA'); " +
            $"CREATE TABLE `{db}`.notes (id INT PRIMARY KEY, body TEXT); INSERT INTO `{db}`.notes VALUES (1, 'call ana'), (2, 'nothing');" +
            $"CREATE TABLE `{db}`.numbers (n INT);");
        try
        {
            var (window, context, session, connector, _) = CreateShell(cs, db);
            Assert.True(await window.ConnectAsync(window, session, null));
            var model = window.Tree.Tree.Model!;
            var root = await WaitAsync(() => model.Roots.FirstOrDefault());
            var dbNode = await WaitAsync(() => root.Children?.FirstOrDefault(n => n.Name == db));
            window.Tree.Tree.FocusedNode = dbNode;
            await WaitAsync(() => ReferenceEquals(window.Tabs.SelectedItem, window.Tabs.DatabaseTab) ? "ok" : null);

            // Ctrl+Shift+F abre o diálogo na base focada, com todas as tabelas escolhidas.
            Assert.True(window.TryExecuteShortcut("Ctrl+Shift+F"));
            var dialog = await WaitAsync(() => window.FindTextWindow is { TableBoxes.Count: 3 } d ? d : null);
            Assert.All(dialog.TableBoxes, b => Assert.True(b.IsChecked));

            // Sem distinção de maiúsculas: "ana" em people (2) e notes (1); numbers sem texto também é contada.
            dialog.FindTextBox.Text = "ana";
            await dialog.FindAsync();
            var rows = dialog.Results.Roots.ToDictionary(n => n[1], n => n[2]);
            Assert.Equal("2", rows["people"]);
            Assert.Equal("1", rows["notes"]);
            Assert.Equal("0", rows["numbers"]);
            Assert.Equal("3 matching row(s) in 2 of 3 table(s).", dialog.StatusText);
            Assert.True(dialog.CanSeeResults);

            // Case sensitive e match exato.
            dialog.CaseSensitiveBox.IsChecked = true;
            dialog.MatchBox.SelectedIndex = 1;
            dialog.FindTextBox.Text = "ANA";
            await dialog.FindAsync();
            Assert.Equal("1", dialog.Results.Roots.Single(n => n[1] == "people")[2]);
            Assert.Equal("0", dialog.Results.Roots.Single(n => n[1] == "notes")[2]);

            if (Environment.GetEnvironmentVariable("EUNOMIA_REVIEW_DIR") is { Length: > 0 } dir)
            {
                dialog.CaptureRenderedFrame();
                dialog.CaptureRenderedFrame()!.Save(Path.Combine(dir, "find-text.png"), PngBitmapEncoderOptions.Default);
            }

            // See results: uma query tab nova com o SELECT, a correr.
            var tabs = window.Tabs.QueryTabs.Count;
            dialog.SeeResults();
            await WaitAsync(() => window.FindTextWindow is null ? "closed" : null);
            Assert.Equal(tabs + 1, window.Tabs.QueryTabs.Count);
            Assert.StartsWith($"SELECT * FROM `{db}`.`people` WHERE", window.SqlEditor.Text, StringComparison.Ordinal);
            var view = Assert.IsType<QueryTabView>(window.Tabs.QueryTabs[^1].Content);
            await WaitAsync(() => view.ActiveGrid is { RowCount: 1 } ? "ok" : null);

            await connector.DisconnectAllAsync();
            window.Close();
        }
        finally
        {
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`");
        }
    }
}
