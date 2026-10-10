using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Eunomia.Engine.Results.Synthetic;
using Eunomia.UI.Controls.Grid;
using Eunomia.UI.Controls.SqlEditor;
using Eunomia.UI.Controls.Tree;

namespace Eunomia.UI.Headless.Tests;

/// <summary>
/// Snapshots do grid, da árvore e do editor (passo 7 do spike), nos temas claro e escuro e a 100% e 150%.
/// Dados determinísticos e cultura invariante; o editor não tem foco (o caret pisca).
/// </summary>
public sealed class SnapshotTests
{
    private static Window CreateWindow(Control content, double width, double height, ThemeVariant theme, double scaling)
    {
        var window = new Window { Width = width, Height = height, Content = content, RequestedThemeVariant = theme };
        window.Show();
        window.Activate();
        if (scaling != 1.0)
        {
            window.SetRenderScaling(scaling);
        }
        return window;
    }

    private static async Task<DataGridView> GridAsync()
    {
        var source = new SyntheticRowSource();
        var view = new DataGridView();
        view.Control.Culture = CultureInfo.InvariantCulture;
        view.Control.SessionColor = SyntheticTreeProvider.SessionColors[0];
        view.Control.SetSource(source);
        await source.RequestRangeAsync(0, 1024);
        await view.Control.AutoSizeColumnsAsync(0);
        view.Control.Selection.Focus(3, 2);
        view.Control.Selection.Focus(5, 2, SelectMode.Extend);
        view.Control.Edits.SetValue(5, 3, new CellValue("1.50"));
        return view;
    }

    [AvaloniaTheory(Timeout = UiTimeouts.Default)]
    [InlineData("grid-light-100", "light", 1.0)]
    [InlineData("grid-dark-100", "dark", 1.0)]
    [InlineData("grid-light-150", "light", 1.5)]
    public async Task Grid(string name, string theme, double scaling)
    {
        var view = await GridAsync();
        var window = CreateWindow(view, 900, 420, theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light, scaling);
        view.Control.Focus();
        window.CaptureRenderedFrame();

        Snapshot.Match(window.CaptureRenderedFrame(), name);
        window.Close();
    }

    private static async Task<DatabaseTreeView> TreeAsync(string databaseFilter, string tableFilter)
    {
        var view = new DatabaseTreeView();
        view.Tree.Culture = CultureInfo.InvariantCulture;
        var model = new TreeModel(SyntheticTreeProvider.CreateSessions(), new SyntheticTreeProvider());
        view.Tree.Model = model;
        var session = model.Roots[0];
        await model.ExpandAsync(session);
        await model.ExpandAsync(session.Children![5]);
        await model.ExpandAsync(session.Children![8]);
        session.Children![5].Children![2].IsFavorite = true;
        view.DatabaseFilterBox.Text = databaseFilter;
        view.TableFilterBox.Text = tableFilter;
        // O TextChanged da caixa pode chegar depois; aplica já para o nó focado ser um dos visíveis.
        model.SetDatabaseFilter(databaseFilter);
        model.SetTableFilter(tableFilter);
        view.Tree.FocusedNode = model.Visible.First(n => n.IsObject);
        return view;
    }

    [AvaloniaTheory(Timeout = UiTimeouts.Default)]
    [InlineData("tree-light-100", "light", 1.0, "", "")]
    [InlineData("tree-dark-100", "dark", 1.0, "", "")]
    [InlineData("tree-filtered-light-100", "light", 1.0, "^(?!sys|tmp)", "b2b;prd_products|ecm_*product")]
    [InlineData("tree-light-150", "light", 1.5, "", "")]
    public async Task Tree(string name, string theme, double scaling, string databaseFilter, string tableFilter)
    {
        var view = await TreeAsync(databaseFilter, tableFilter);
        var window = CreateWindow(view, 320, 560, theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light, scaling);
        view.Tree.Focus();
        window.CaptureRenderedFrame();

        Snapshot.Match(window.CaptureRenderedFrame(), name);
        window.Close();
    }

    private const string Sql = """
        -- Consulta de teste do spike
        SELECT p.`id`, p.`name`, COUNT(*) AS total, SUM(p.price * 1.23) AS gross
        FROM `elmwood`.`prd_products` p
        WHERE p.created >= '2026-01-01' AND p.status IN ('active', "draft")
          AND @@SESSION.sql_mode IS NOT NULL # comentário com #
        /* comentário de bloco
           em duas linhas */
        GROUP BY p.`id`
        HAVING total > 0x1F
        ORDER BY gross DESC
        LIMIT 100;
        """;

    [AvaloniaTheory]
    [InlineData("editor-light-100", "light", 1.0)]
    [InlineData("editor-dark-100", "dark", 1.0)]
    [InlineData("editor-light-150", "light", 1.5)]
    public void Editor(string name, string theme, double scaling)
    {
        var editor = new SqlEditorControl { Text = Sql };
        var window = CreateWindow(editor, 760, 260, theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light, scaling);
        window.CaptureRenderedFrame();

        Snapshot.Match(window.CaptureRenderedFrame(), name);
        window.Close();
    }
}
