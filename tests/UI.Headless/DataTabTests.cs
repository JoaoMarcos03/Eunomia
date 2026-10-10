using System.Globalization;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Eunomia.App.Data;
using Eunomia.App.Settings;
using Eunomia.Engine.Results;
using Eunomia.Infra.Settings;
using Eunomia.UI.Controls.Grid;
using Eunomia.UI.Controls.Tree;
using static Eunomia.UI.Headless.Tests.RealServer;

namespace Eunomia.UI.Headless.Tests;

/// <summary>Data tab com uma tabela real (Fase 5; secção 4.4; FAM-G02, G03, G04).</summary>
public sealed class DataTabTests
{
    private const string Digits = "(SELECT 0 n UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9)";

    private static TreeNode Child(TreeNode parent, string name) => parent.Children!.Single(n => n.Name == name);

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task FiltroWhereRecentesEMultiColuna()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "Sem servidor de teste.");
        using var culture = new CultureScope(CultureInfo.InvariantCulture);
        var db = NewDatabaseName("eunomia_df_");
        await ExecAsync(cs, $"CREATE DATABASE `{db}`; CREATE TABLE `{db}`.t (id INT PRIMARY KEY, name VARCHAR(20)) ENGINE=InnoDB; " +
            $"INSERT INTO `{db}`.t VALUES (1, 'ana'), (2, 'rui'), (3, 'eva'), (4, 'ana maria');");
        try
        {
            var (window, context, session, connector, _) = CreateShell(cs, db);
            Assert.True(await window.ConnectAsync(window, session, null));
            var model = window.Tree.Tree.Model!;
            var root = await WaitAsync(() => model.Roots.FirstOrDefault());
            var dbNode = await WaitAsync(() => root.Children?.FirstOrDefault());
            await model.ExpandAsync(dbNode);
            window.ShowDataTab();
            window.Tree.Tree.FocusedNode = Child(dbNode, "t");
            await WaitAsync(() => window.DataGrid.RowCount == 4 ? "ok" : null);
            Assert.False(window.DataView.FilterPanel.IsVisible);

            // WHERE escrito no painel (um WHERE inicial é ignorado), aplicado com F9 (filter.apply).
            window.DataView.FilterEditor.Text = "WHERE id > 2";
            Assert.True(await context.Commands.ExecuteAsync("filter.apply"));
            await WaitAsync(() => window.DataGrid.RowCount == 2 && window.DataView.Label.Text!.Contains("rows match to filter", StringComparison.Ordinal) ? "ok" : null);
            Assert.Contains("WHERE id > 2", window.DataBrowser!.Select.Sql, StringComparison.Ordinal);
            Assert.True(window.DataView.RecentFilters.IsVisible);

            // Multi column filter: reescreve o filtro com LIKE em todas as colunas.
            window.DataView.MultiColumnBox.Text = "ana";
            await WaitAsync(() => window.DataView.FilterEditor.Text == "`id` LIKE '%ana%' OR `name` LIKE '%ana%'" ? "ok" : null);
            Assert.True(await context.Commands.ExecuteAsync("filter.apply"));
            await WaitAsync(() => window.DataGrid.RowCount == 2 && window.DataBrowser!.Options.Filter!.Contains("LIKE", StringComparison.Ordinal) ? "ok" : null);
            Assert.Equal(2, ((System.Collections.IList)window.DataView.RecentFilters.ItemsSource!).Count);

            // Clear (Ctrl+W): sem filtro, todas as linhas.
            Assert.True(await context.Commands.ExecuteAsync("filter.clear"));
            await WaitAsync(() => window.DataGrid.RowCount == 4 && window.DataBrowser!.Options.Filter is null ? "ok" : null);
            Assert.Equal(string.Empty, window.DataView.MultiColumnBox.Text);

            // Quick Filter (Ctrl+Shift+1): valores da coluna focada nas linhas selecionadas.
            window.DataGrid.Selection.Focus(0, 1);
            window.DataGrid.Selection.Focus(1, 1, SelectMode.Extend);
            Assert.True(await context.Commands.ExecuteAsync("quickFilter.focused1"));
            await WaitAsync(() => window.DataGrid.RowCount == 2 && window.DataBrowser!.Options.Filter == "`name` IN ('ana', 'rui')" ? "ok" : null);
            Assert.True(window.DataView.FilterPanel.IsVisible);
            Assert.Equal("`name` IN ('ana', 'rui')", context.Commands.StateOf("quickFilter.focused1").Caption);
            Assert.True(await context.Commands.ExecuteAsync("quickFilter.null"));
            await WaitAsync(() => window.DataGrid.RowCount == 0 && window.DataBrowser!.Options.Filter == "`name` IS NULL" ? "ok" : null);
            Assert.True(await context.Commands.ExecuteAsync("filter.remove"));
            await WaitAsync(() => window.DataGrid.RowCount == 4 ? "ok" : null);

            // Filter panel local (regex, sem maiúsculas) sobre as linhas carregadas; F5 limpa-o.
            window.LocalFilterBox.Text = "^ANA";
            await window.ApplyLocalFilterAsync();
            Assert.Equal(2, window.DataGrid.RowCount);
            Assert.Equal("2 out of 4 matching. 2 hidden.", window.LocalFilterInfo.Text);
            window.LocalFilterBox.Text = "(";
            await window.ApplyLocalFilterAsync();
            Assert.Equal(4, window.DataGrid.RowCount);
            window.LocalFilterBox.Text = "eva";
            await window.ApplyLocalFilterAsync();
            Assert.Equal(1, window.DataGrid.RowCount);
            window.DataGrid.Focus();
            Assert.True(await context.Commands.ExecuteAsync("view.refresh"));
            await WaitAsync(() => window.DataGrid.RowCount == 4 && window.LocalFilterBox.Text == string.Empty ? "ok" : null);

            // Export grid rows para ficheiro: JSON em UTF-8 com BOM, todas as linhas carregadas.
            var file = Path.Combine(Path.GetTempPath(), $"eunomia-export-{Guid.NewGuid():N}.json");
            window.ExportDialogOverride = () => new UI.Features.Transfer.GridExportRequest(
                App.Export.GridExportFormat.Json, false, file, "UTF-8-BOM", false, new App.Export.GridExportOptions { ToClipboard = false }, false, true, false);
            try
            {
                Assert.True(await context.Commands.ExecuteAsync("export.gridRows"));
                var bytes = await File.ReadAllBytesAsync(file);
                Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
                var json = System.Text.Encoding.UTF8.GetString(bytes[3..]);
                Assert.StartsWith("{\r\n\t\"query\": \"SELECT", json, StringComparison.Ordinal);
                Assert.Contains("\"name\": \"ana maria\"", json, StringComparison.Ordinal);
            }
            finally
            {
                File.Delete(file);
            }

            // More values: os valores mais frequentes com a contagem.
            var top = await window.DataBrowser!.TopValuesAsync("name", CancellationToken.None);
            Assert.Equal(4, top.Count);
            Assert.All(top, v => Assert.Equal(1, v.Count));

            await connector.DisconnectAllAsync();
            window.Close();
        }
        finally
        {
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`");
        }
    }

    /// <summary>
    /// Mudar de tab mete o texto do filtro local da lista na caixa; o TextChanged do Avalonia chega depois e reaplicava o
    /// filtro 500 ms mais tarde, com o foco e o scroll a voltar à primeira linha a meio do trabalho.
    /// </summary>
    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task FiltroLocalVazioNaoMexeNoFocoNemNoScroll()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "Sem servidor de teste.");
        var db = NewDatabaseName("eunomia_lf_");
        await ExecAsync(cs, $"CREATE DATABASE `{db}`; CREATE TABLE `{db}`.t (id INT PRIMARY KEY); " +
            $"INSERT INTO `{db}`.t SELECT a.n + (b.n * 10) + (c.n * 100) + 1 FROM {Digits} a, {Digits} b, {Digits} c;");
        try
        {
            var (window, _, session, connector, _) = CreateShell(cs, db);
            Assert.True(await window.ConnectAsync(window, session, null));
            var model = window.Tree.Tree.Model!;
            var root = await WaitAsync(() => model.Roots.FirstOrDefault());
            var dbNode = await WaitAsync(() => root.Children?.FirstOrDefault());
            await model.ExpandAsync(dbNode);
            window.ShowDataTab();
            window.Tree.Tree.FocusedNode = Child(dbNode, "t");
            await WaitAsync(() => window.DataGrid.RowCount == 1000 ? "ok" : null);

            async Task PauseAsync()
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (clock.Elapsed < TimeSpan.FromMilliseconds(900))
                {
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    await Task.Delay(20);
                }
            }

            // Sem filtro: o foco e o scroll ficam onde o utilizador os pôs.
            await PauseAsync();
            window.DataGrid.Selection.Focus(500, 0);
            window.DataGrid.TopRow = 490;
            await PauseAsync();
            Assert.Equal(500, window.DataGrid.Selection.FocusedRow);
            Assert.Equal(490, window.DataGrid.TopRow);

            // Com filtro local ("5": 271 linhas), ida à query tab e volta: o filtro não é reaplicado.
            window.LocalFilterBox.Text = "5";
            await window.ApplyLocalFilterAsync();
            await PauseAsync();
            Assert.Equal(271, window.DataGrid.RowCount);
            window.DataGrid.Selection.Focus(100, 0);
            window.DataGrid.TopRow = 95;
            window.Tabs.SelectedItem = window.Tabs.QueryTabs[0].Item;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.ShowDataTab();
            await PauseAsync();
            Assert.Equal(271, window.DataGrid.RowCount);
            Assert.Equal(100, window.DataGrid.Selection.FocusedRow);
            Assert.Equal(95, window.DataGrid.TopRow);

            await connector.DisconnectAllAsync();
            window.Close();
        }
        finally
        {
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`");
        }
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task PaginaOrdenaEEscondeColunas()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "Sem servidor de teste.");
        using var culture = new CultureScope(CultureInfo.InvariantCulture);
        var db = NewDatabaseName("eunomia_dt_");
        await ExecAsync(cs, $"CREATE DATABASE `{db}`; CREATE TABLE `{db}`.t (id INT PRIMARY KEY, name VARCHAR(20), body TEXT) ENGINE=InnoDB; " +
            $"INSERT INTO `{db}`.t SELECT seq, CONCAT('n', LPAD(seq, 4, '0')), IF(seq = 1, REPEAT('b', 300), 'curto') " +
            $"FROM (SELECT a.n + (b.n * 10) + (c.n * 100) + (d.n * 1000) + 1 AS seq FROM {Digits} a, {Digits} b, {Digits} c, {Digits} d) s WHERE seq <= 2500;");
        var dir = Path.Combine(Path.GetTempPath(), "eunomia-dt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            using var appDb = new SqliteAppDatabase(Path.Combine(dir, "app.db"));
            var (window, context, session, connector, _) = CreateShell(cs, db, appDatabase: appDb);
            Assert.True(await window.ConnectAsync(window, session, null));
            var model = window.Tree.Tree.Model!;
            var root = await WaitAsync(() => model.Roots.FirstOrDefault());
            var dbNode = await WaitAsync(() => root.Children?.FirstOrDefault());
            await model.ExpandAsync(dbNode);
            window.ShowDataTab();
            window.Tree.Tree.FocusedNode = Child(dbNode, "t");

            // Primeiras 1.000 linhas, label com limite e a coluna longa cortada.
            await WaitAsync(() => window.DataGrid.RowCount == 1000 && window.DataView.Label.Text!.Contains("limited to 1,000", StringComparison.Ordinal) ? "ok" : null);
            Assert.StartsWith($"{db}.t: ", window.DataView.Label.Text, StringComparison.Ordinal);
            Assert.EndsWith(" [...]", window.DataGrid.GetDisplayText(0, 2), StringComparison.Ordinal);
            Assert.True(window.DataView.NextButton.IsEnabled);
            Assert.Equal("Columns (3/3)", window.DataView.ColumnsButton.Content);

            // Copy as / Export: a linha 1 tem body com 300 caracteres (cortado no grid); o export relê sem LEFT().
            window.DataGrid.Selection.Focus(0, 0);
            var export = await window.ReadDataGridExportAsync(selectedOnly: true);
            Assert.NotNull(export);
            Assert.Single(export.Rows);
            Assert.Equal(["1", "n0001", new string('b', 300)], export.Rows[0]);
            Assert.Equal(["id"], export.KeyColumns);

            // Teclado (FAM-G06): PgDn na última linha faz Next; Ctrl+End faz Show all e vai à última; ↓ na última não insere
            // (o Data tab é só de leitura até à Fase 7).
            window.DataGrid.Focus();
            window.DataGrid.Selection.Focus(999, 0);
            window.KeyPress(Key.PageDown, RawInputModifiers.None, PhysicalKey.PageDown, null);
            await WaitAsync(() => window.DataGrid.RowCount == 2000 ? "ok" : null);
            Assert.Equal(1000, window.DataGrid.Selection.FocusedRow);
            window.KeyPress(Key.End, RawInputModifiers.Control, PhysicalKey.End, null);
            await WaitAsync(() => window.DataGrid.RowCount == 2500 && window.DataGrid.Selection.FocusedRow == 2499 ? "ok" : null);
            await WaitAsync(() => window.DataView.Label.Text == $"{db}.t: 2,500 rows total (exact)" ? "ok" : null);
            Assert.False(window.DataView.NextButton.IsEnabled);
            // ↓ na última linha acrescenta uma linha nova (Fase 7); sem alterações, Esc descarta-a.
            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            await WaitAsync(() => window.DataGrid.RowCount == 2501 ? "ok" : null);
            window.DataGrid.CancelEditing();
            Assert.Equal(2500, window.DataGrid.RowCount);
            Assert.False(window.DataGrid.IsEditing);

            // Shift+clique no header "name": ORDER BY name DESC, indicador e estado gravado; mantém as linhas pedidas.
            var grid = window.DataGrid;
            var header = grid.TranslatePoint(new Avalonia.Point(grid.CellBounds(0, 1).Center.X, grid.HeaderHeight / 2), window)!.Value;
            window.MouseDown(header, MouseButton.Left, RawInputModifiers.Shift);
            window.MouseUp(header, MouseButton.Left, RawInputModifiers.Shift);
            var browser = await WaitAsync(() => window.DataBrowser is { Options.Sort.Count: 1 } b && window.DataGrid.RowCount == 2500 ? b : null);
            Assert.Equal(new DataSort("name", true), browser.Options.Sort[0]);
            Assert.Equal(new GridSortIndicator(true, 1), window.DataGrid.SortIndicators[1]);
            Assert.Equal("n2500", window.DataGrid.GetDisplayText(0, 1));
            Assert.Equal("Sorting (1)", window.DataView.SortingButton.Content);
            var key = new TableKey(session.Id.ToString("D"), db, "t");
            Assert.Equal("1_name", appDb.GetTableState(key, "Sort"));

            // Alt+S: Reset sorting.
            Assert.True(await context.Commands.ExecuteAsync("grid.resetSorting"));
            await WaitAsync(() => window.DataBrowser is { Options.Sort.Count: 0 } && window.DataGrid.SortIndicators.Count == 0 ? "ok" : null);
            Assert.Null(appDb.GetTableState(key, "Sort"));

            // Colunas ocultas gravadas por tabela: o SELECT deixa a coluna de fora.
            appDb.SetTableState(key, "HiddenColumns", "body");
            Assert.True(await context.Commands.ExecuteAsync("view.refresh"));
            await WaitAsync(() => window.DataGrid.Source?.Schema.Count == 2 ? "ok" : null);
            Assert.Equal("Columns (2/3)", window.DataView.ColumnsButton.Content);
            Assert.DoesNotContain("body", window.DataBrowser!.Select.Sql, StringComparison.Ordinal);

            await connector.DisconnectAllAsync();
            window.Close();
        }
        finally
        {
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`");
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }
}
