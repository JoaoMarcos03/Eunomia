using System.Globalization;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Eunomia.UI.Controls.Grid;
using Eunomia.UI.Controls.Tree;
using MySqlConnector;
using static Eunomia.UI.Headless.Tests.RealServer;

namespace Eunomia.UI.Headless.Tests;

/// <summary>Edição no Data tab contra o servidor (Fase 7; anexo D B15–B17; FAM-G08, G09; D3).</summary>
public sealed class DataEditingTests
{
    private static async Task<string?> ScalarAsync(string cs, string sql)
    {
        await using var connection = new MySqlConnection(cs);
        await connection.OpenAsync();
        await using var cmd = new MySqlCommand(sql, connection);
        return Convert.ToString(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task EditarInserirDuplicarApagarEErros()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "Sem servidor de teste.");
        var db = NewDatabaseName("eunomia_ed_");
        await ExecAsync(cs, $"CREATE DATABASE `{db}`; CREATE TABLE `{db}`.people (id INT AUTO_INCREMENT PRIMARY KEY, name VARCHAR(20), age INT, note TEXT, created DATETIME) ENGINE=InnoDB; " +
            $"INSERT INTO `{db}`.people (name, age, note) VALUES ('Ana', 30, REPEAT('n', 400)), ('Rui', 40, 'curta');");
        try
        {
            var (window, context, session, _, _) = CreateShell(cs, db);
            var errors = new List<string>();
            window.EditErrorOverride = errors.Add;
            window.ConfirmOverride = (_, _, _) => Task.FromResult((true, false));
            Assert.True(await window.ConnectAsync(window, session, null));
            var model = window.Tree.Tree.Model!;
            var root = await WaitAsync(() => model.Roots.FirstOrDefault());
            var dbNode = await WaitAsync(() => root.Children?.FirstOrDefault(n => n.Name == db));
            await model.ExpandAsync(dbNode);
            window.ShowDataTab();
            window.Tree.Tree.FocusedNode = dbNode.Children!.Single(n => n.Name == "people");
            var grid = window.DataGrid;
            await WaitAsync(() => grid.RowCount == 2 ? "ok" : null);
            Assert.False(grid.IsReadOnly);
            var name = 1;
            var age = 2;
            var note = 3;
            var created = 4;

            // UPDATE pela chave; a célula mostra o valor relido (a nota cortada a 256 vem completa).
            grid.FocusCell(0, name);
            Assert.True(await grid.SetCellValueAsync(0, name, new CellValue("Zé")));
            Assert.True(await grid.SetCellValueAsync(0, created, new CellValue("'2001-02-03' + INTERVAL 1 HOUR", IsFunction: true)));
            Assert.True(await grid.PostAsync());
            Assert.Equal("Zé|2001-02-03 01:00:00", await ScalarAsync(cs, $"SELECT CONCAT_WS('|', name, created) FROM `{db}`.people WHERE id = 1"));
            Assert.Equal("2001-02-03 01:00:00", grid.GetDisplayText(0, created));
            Assert.Equal(400, grid.GetDisplayText(0, note)!.Length);
            Assert.False(grid.Edits.HasPending);

            // O mesmo valor não é erro (o servidor conta 0 linhas alteradas).
            Assert.True(await grid.SetCellValueAsync(0, age, new CellValue("30")));
            Assert.True(await grid.PostAsync());
            Assert.Empty(errors);

            // Insert value ▸: NULL (Ctrl+Shift+N), a data do servidor (o valor está na caption) e o default literal.
            window.CaptureRenderedFrame();
            grid.Focus();
            grid.FocusCell(1, age);
            Assert.True(await context.Commands.ExecuteAsync("grid.setNull"));
            grid.FocusCell(1, created);
            window.RefreshInsertValues();
            var date = context.Commands.StateOf("insertValue.date").Caption;
            Assert.StartsWith("date: ", date, StringComparison.Ordinal);
            Assert.True(await context.Commands.ExecuteAsync("insertValue.date"));
            grid.FocusCell(1, name);
            Assert.False(context.Commands.StateOf("insertValue.default").CanExecute);
            Assert.True(await grid.PostAsync());
            Assert.Equal("NULL|" + date[6..] + " 00:00:00", await ScalarAsync(cs, $"SELECT CONCAT_WS('|', IFNULL(age, 'NULL'), created) FROM `{db}`.people WHERE id = 2"));

            // Insert row: só as colunas alteradas; o id vem do LAST_INSERT_ID().
            Assert.True(await context.Commands.ExecuteAsync("grid.insertRow"));
            Assert.Equal(3, grid.RowCount);
            Assert.True(await grid.SetCellValueAsync(2, name, new CellValue("Nova")));
            Assert.True(await grid.PostAsync());
            Assert.Equal("3", grid.GetDisplayText(2, 0));
            Assert.Equal("Nova", await ScalarAsync(cs, $"SELECT name FROM `{db}`.people WHERE id = 3"));

            // Duplicate row without keys: copia tudo menos o id e grava ao fazer Post.
            grid.FocusCell(1, name);
            Assert.True(await context.Commands.ExecuteAsync("grid.duplicateRow"));
            Assert.True(await grid.PostAsync());
            Assert.Equal("2", await ScalarAsync(cs, $"SELECT COUNT(*) FROM `{db}`.people WHERE name = 'Rui' AND age IS NULL AND note = 'curta'"));

            // Delete selected row(s): pergunta, apaga e recarrega.
            grid.FocusCell(0, name);
            Assert.True(await context.Commands.ExecuteAsync("grid.deleteRows"));
            await WaitAsync(() => grid.RowCount == 3 ? "ok" : null);
            Assert.Equal("0", await ScalarAsync(cs, $"SELECT COUNT(*) FROM `{db}`.people WHERE id = 1"));

            // A linha apagada por outra ligação: o UPDATE dá erro e a linha fica alterada.
            var victim = grid.GetDisplayText(0, 0);
            await ExecAsync(cs, $"DELETE FROM `{db}`.people WHERE id = {victim}");
            Assert.True(await grid.SetCellValueAsync(0, name, new CellValue("perdido")));
            Assert.False(await grid.PostAsync());
            Assert.StartsWith("The UPDATE affected 0 rows, but 1 was expected.", Assert.Single(errors), StringComparison.Ordinal);
            Assert.True(grid.Edits.HasPending);
            grid.CancelEditing();
            Assert.False(grid.Edits.HasPending);
            window.Close();
        }
        finally
        {
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`");
        }
    }
}
