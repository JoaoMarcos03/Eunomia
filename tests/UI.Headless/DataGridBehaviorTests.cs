using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Eunomia.Engine.Results;
using Eunomia.Engine.Results.Synthetic;
using Eunomia.UI.Controls.Grid;

namespace Eunomia.UI.Headless.Tests;

/// <summary>Suíte de teclado e edição do grid (secção 4.4, FAM-G05, FAM-G06, FAM-G08, D10) sobre 1M × 60.</summary>
public sealed class DataGridBehaviorTests
{
    private static async Task<(Window Window, DataGridControl Grid, SyntheticRowSource Source)> ShowAsync()
    {
        var source = new SyntheticRowSource();
        var view = new DataGridView();
        view.Control.Culture = System.Globalization.CultureInfo.InvariantCulture;
        var window = new Window { Width = 900, Height = 500, Content = view };
        window.Show();
        view.Control.SetSource(source);
        await source.RequestRangeAsync(0, 2048);
        window.CaptureRenderedFrame();
        view.Control.Focus();
        return (window, view.Control, source);
    }

    private static void Press(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None) =>
        window.KeyPress(key, modifiers, PhysicalKey.None, null);

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task HomeEndEControlHomeEnd()
    {
        var (window, grid, _) = await ShowAsync();

        Press(window, Key.End);
        Assert.Equal(59, grid.Selection.FocusedColumn);
        Press(window, Key.Home);
        Assert.Equal(0, grid.Selection.FocusedColumn);
        Press(window, Key.End, RawInputModifiers.Control);
        Assert.Equal(999_999, grid.Selection.FocusedRow);
        Assert.Equal(grid.MaxTopRow, grid.TopRow);
        Press(window, Key.Home, RawInputModifiers.Control);
        Assert.Equal(0, grid.Selection.FocusedRow);
        Assert.Equal(0, grid.TopRow);
        window.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task SetasPaginasESelecaoComShift()
    {
        var (window, grid, _) = await ShowAsync();

        Press(window, Key.Down);
        Press(window, Key.Down);
        Press(window, Key.Right);
        Assert.Equal("r3 : c2 (1 sel)", grid.Selection.StatusText);

        Press(window, Key.Down, RawInputModifiers.Shift);
        Press(window, Key.Down, RawInputModifiers.Shift);
        Assert.Equal(3, grid.Selection.Count);

        Press(window, Key.PageDown);
        Assert.Equal(4 + grid.ViewportRows - 1, grid.Selection.FocusedRow);
        Assert.Equal(1, grid.Selection.Count);
        window.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task CtrlAECtrlI()
    {
        var (window, grid, _) = await ShowAsync();

        Press(window, Key.A, RawInputModifiers.Control);
        Assert.Equal(1_000_000, grid.Selection.Count);
        Press(window, Key.I, RawInputModifiers.Control);
        Assert.Equal(0, grid.Selection.Count);
        window.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task EnterEditaEnterTerminaEMudarDeLinhaGrava()
    {
        var (window, grid, _) = await ShowAsync();
        Press(window, Key.Right);

        Press(window, Key.Enter);
        Assert.True(grid.IsEditing);
        grid.Editor!.Text = "";
        window.KeyTextInput("123");
        Press(window, Key.Enter);

        Assert.False(grid.IsEditing);
        Assert.True(grid.Edits.IsModified(0, 1));
        Assert.Equal("123", grid.GetDisplayText(0, 1));

        // Mudar de coluna na mesma linha não grava.
        Press(window, Key.Right);
        Assert.True(grid.Edits.HasPending);
        // Mudar de linha grava.
        Press(window, Key.Down);
        Assert.False(grid.Edits.HasPending);
        Assert.Equal("123", grid.GetDisplayText(0, 1));
        window.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task F2EditaEEscCancela()
    {
        var (window, grid, _) = await ShowAsync();
        var before = grid.GetDisplayText(0, 2);

        Press(window, Key.F2);
        Assert.True(grid.IsEditing);
        window.KeyTextInput("zzz");
        Press(window, Key.Escape);

        Assert.False(grid.IsEditing);
        Assert.False(grid.Edits.HasPending);
        Assert.Equal(before, grid.GetDisplayText(0, 2));
        window.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task TabEmEdicaoAvancaDeCelula()
    {
        var (window, grid, _) = await ShowAsync();
        Press(window, Key.Right);

        Press(window, Key.Enter);
        Press(window, Key.Tab);
        Assert.True(grid.IsEditing);
        Assert.Equal(2, grid.Selection.FocusedColumn);
        Press(window, Key.Tab, RawInputModifiers.Shift);
        Assert.True(grid.IsEditing);
        Assert.Equal(1, grid.Selection.FocusedColumn);
        Press(window, Key.Escape);
        window.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task EscForaDeEdicaoDescartaALinha()
    {
        var (window, grid, _) = await ShowAsync();
        var before = grid.GetDisplayText(0, 0);

        Press(window, Key.Enter);
        window.KeyTextInput("9");
        Press(window, Key.Enter);
        Assert.True(grid.Edits.HasPending);
        Press(window, Key.Escape);

        Assert.False(grid.Edits.HasPending);
        Assert.Equal(before, grid.GetDisplayText(0, 0));
        window.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task SetaParaBaixoNaUltimaLinhaInsereLinha()
    {
        var (window, grid, source) = await ShowAsync();
        await source.RequestRangeAsync(999_000, 1000);

        Press(window, Key.End, RawInputModifiers.Control);
        Press(window, Key.Down);

        Assert.Equal(1_000_001, grid.RowCount);
        Assert.Equal(1_000_000, grid.Selection.FocusedRow);
        Assert.True(grid.Edits.PendingIsInsert);
        Assert.Equal(DisplayFormatter.NullText, grid.GetDisplayText(1_000_000, 1));

        // Esc descarta a linha nova.
        Press(window, Key.Escape);
        Assert.Equal(1_000_000, grid.RowCount);
        window.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task PageDownNaUltimaLinhaPedeNext()
    {
        var (window, grid, _) = await ShowAsync();
        var requested = 0;
        grid.NextPageRequested += (_, _) => requested++;

        Press(window, Key.End, RawInputModifiers.Control);
        Press(window, Key.PageDown);

        Assert.Equal(1, requested);
        window.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task CtrlCCopiaSoACelulaFocadaComValorCompleto()
    {
        var (window, grid, source) = await ShowAsync();
        var block = await source.GetBlockAsync(0);
        var nullRow = Enumerable.Range(0, block.RowCount).First(r => block.IsNull(r, 1));
        var textColumn = source.Schema.Columns.ToList().FindIndex(c => c.NativeType == "TEXT");
        var truncatedRow = Enumerable.Range(0, block.RowCount).First(r => block.IsTruncated(r, textColumn));

        // Várias linhas selecionadas: copia só a célula focada (D10). Inteiro sem separador.
        grid.Selection.Focus(1233, 0);
        grid.Selection.Focus(1240, 0, SelectMode.Extend);
        var (text, isNull) = await grid.GetCopyTextAsync();
        Assert.Equal("1241", text);
        Assert.False(isNull);
        Assert.Equal("1,241", grid.GetDisplayText(1240, 0));

        // NULL copia texto vazio e fica marcado.
        grid.Selection.Focus(nullRow, 1);
        (text, isNull) = await grid.GetCopyTextAsync();
        Assert.Equal(string.Empty, text);
        Assert.True(isNull);

        // Texto cortado no grid: Ctrl+C copia o valor completo, sem o sufixo.
        grid.Selection.Focus(truncatedRow, textColumn);
        (text, _) = await grid.GetCopyTextAsync();
        Assert.True(text.Length > SyntheticRowSource.TextTruncateLength);
        Assert.DoesNotContain(DisplayFormatter.TruncatedSuffix, text, StringComparison.Ordinal);
        Assert.EndsWith(DisplayFormatter.TruncatedSuffix, grid.GetDisplayText(truncatedRow, textColumn), StringComparison.Ordinal);

        Press(window, Key.C, RawInputModifiers.Control);
        window.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task ScrollPorRodaETeclasMantemFocoVisivel()
    {
        var (window, grid, _) = await ShowAsync();

        window.MouseWheel(new Avalonia.Point(300, 200), new Avalonia.Vector(0, -10));
        Assert.Equal(30, grid.TopRow);
        window.MouseWheel(new Avalonia.Point(300, 200), new Avalonia.Vector(0, -1), RawInputModifiers.Shift);
        Assert.True(grid.ScrollX > 0);

        Press(window, Key.Down);
        Assert.InRange(grid.Selection.FocusedRow, grid.TopRow, grid.TopRow + grid.ViewportRows - 1);
        window.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task CliqueSelecionaCtrlCliqueAlternaShiftCliqueEstende()
    {
        var (window, grid, _) = await ShowAsync();
        var x = grid.CellBounds(0, 1).Center.X;

        void Click(long row, RawInputModifiers m)
        {
            var p = new Avalonia.Point(x, grid.CellBounds(row, 1).Center.Y);
            window.MouseDown(p, MouseButton.Left, m);
            window.MouseUp(p, MouseButton.Left, m);
        }

        Click(2, RawInputModifiers.None);
        Assert.Equal(2, grid.Selection.FocusedRow);
        Click(5, RawInputModifiers.Shift);
        Assert.Equal(4, grid.Selection.Count);
        Click(3, RawInputModifiers.Control);
        Assert.Equal(3, grid.Selection.Count);
        Assert.False(grid.Selection.IsSelected(3));
        window.Close();
    }
}
