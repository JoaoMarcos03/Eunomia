using Avalonia.Input;

namespace Eunomia.UI.Controls.Grid.Tests;

public sealed class GridColumnsTests
{
    private static GridColumns Make(params double[] widths)
    {
        var c = new GridColumns();
        c.SetWidths(widths);
        return c;
    }

    [Fact]
    public void SomasPrefixasEPesquisaBinaria()
    {
        var c = Make(50, 100, 30);

        Assert.Equal(180, c.TotalWidth);
        Assert.Equal(50, c.OffsetOf(1));
        Assert.Equal(0, c.ColumnAt(0));
        Assert.Equal(0, c.ColumnAt(49.9));
        Assert.Equal(1, c.ColumnAt(50));
        Assert.Equal(2, c.ColumnAt(179));
        Assert.Equal(-1, c.ColumnAt(180));
        Assert.Equal(-1, c.ColumnAt(-1));
    }

    [Fact]
    public void ColunasVisiveis()
    {
        var c = Make(Enumerable.Repeat(100.0, 60).ToArray());

        Assert.Equal((0, 9), c.VisibleRange(0, 1000));
        Assert.Equal((2, 12), c.VisibleRange(250, 1000));
        Assert.Equal((55, 59), c.VisibleRange(5550, 1000));
    }

    [Fact]
    public void LarguraMinima()
    {
        var c = Make(1, 50);

        Assert.Equal(GridColumns.MinWidth, c.WidthOf(0));
        c.SetWidth(1, 400);
        Assert.Equal(400 + GridColumns.MinWidth, c.TotalWidth);
    }
}

public sealed class GridSelectionTests
{
    [Fact]
    public void CliqueSelecionaSoALinhaFocada()
    {
        var s = new GridSelection();
        s.Focus(5, 2);
        s.Focus(7, 3);

        Assert.False(s.IsSelected(5));
        Assert.True(s.IsSelected(7));
        Assert.Equal(1, s.Count);
        Assert.Equal("r8 : c4 (1 sel)", s.StatusText);
    }

    [Fact]
    public void ShiftEstendeDaAncora()
    {
        var s = new GridSelection();
        s.Focus(10, 0);
        s.Focus(14, 0, SelectMode.Extend);
        Assert.Equal(5, s.Count);
        s.Focus(8, 0, SelectMode.Extend);

        Assert.Equal(3, s.Count);
        Assert.Equal([(8L, 10L)], s.Ranges);
        Assert.Equal(10, s.AnchorRow);
    }

    [Fact]
    public void CtrlAlternaLinhas()
    {
        var s = new GridSelection();
        s.Focus(1, 0);
        s.Focus(3, 0, SelectMode.Toggle);
        s.Focus(2, 0, SelectMode.Toggle);

        Assert.Equal([(1L, 3L)], s.Ranges);
        s.Focus(2, 0, SelectMode.Toggle);
        Assert.Equal([(1L, 1L), (3L, 3L)], s.Ranges);
        Assert.Equal(2, s.FocusedRow);
    }

    [Fact]
    public void CtrlSetaMoveSoOFoco()
    {
        var s = new GridSelection();
        s.Focus(1, 0);
        s.Focus(2, 0, SelectMode.FocusOnly);

        Assert.True(s.IsSelected(1));
        Assert.False(s.IsSelected(2));
        Assert.Equal(2, s.FocusedRow);
    }

    [Fact]
    public void CtrlAECtrlI()
    {
        var s = new GridSelection();
        s.Focus(2, 0);
        s.Focus(5, 0, SelectMode.Toggle);

        s.Invert(8);
        Assert.Equal([(0L, 1L), (3L, 4L), (6L, 7L)], s.Ranges);
        Assert.Equal(6, s.Count);

        s.SelectAll(1_000_000);
        Assert.Equal(1_000_000, s.Count);
        Assert.Equal([(0L, 999_999L)], s.Ranges);
        s.Invert(1_000_000);
        Assert.Equal(0, s.Count);
    }

    [Fact]
    public void SemFocoNaoHaStatus()
    {
        Assert.Equal(string.Empty, new GridSelection().StatusText);
    }
}

public sealed class GridEditBufferTests
{
    [Fact]
    public void AlteracoesFicamPendentesAteGravar()
    {
        var b = new GridEditBuffer();
        b.SetValue(3, 1, new CellValue("x"));
        b.SetValue(3, 2, CellValue.Null);

        Assert.True(b.IsModified(3, 1));
        Assert.True(b.TryGetValue(3, 2, out var v) && v.IsNull);
        Assert.False(b.IsModified(4, 1));

        long committed = -1;
        b.RowCommitted += (_, r) => committed = r;
        Assert.True(b.Commit());

        Assert.Equal(3, committed);
        Assert.False(b.IsModified(3, 1));
        Assert.True(b.TryGetValue(3, 1, out var saved) && saved.Text == "x");
        Assert.False(b.Commit());
    }

    [Fact]
    public void NaoAceitaAlteracoesEmDuasLinhasSemGravar()
    {
        var b = new GridEditBuffer();
        b.SetValue(1, 0, new CellValue("a"));

        Assert.Throws<InvalidOperationException>(() => b.SetValue(2, 0, new CellValue("b")));
    }

    [Fact]
    public void DescartarRepoeOValorOriginal()
    {
        var b = new GridEditBuffer();
        b.SetValue(1, 0, new CellValue("a"));
        b.Discard();

        Assert.False(b.TryGetValue(1, 0, out _));
        Assert.False(b.HasPending);
    }

    [Fact]
    public void InserirAcrescentaNoFimEDescartarRemove()
    {
        var b = new GridEditBuffer();

        Assert.Equal(1000, b.InsertRow(1000));
        Assert.True(b.PendingIsInsert);
        Assert.Equal(1, b.InsertedRows);
        b.Discard();
        Assert.Equal(0, b.InsertedRows);

        Assert.Equal(1000, b.InsertRow(1000));
        b.SetValue(1000, 1, new CellValue("novo"));
        b.Commit();
        Assert.Equal(1001, b.InsertRow(1000));
    }
}

public sealed class GridKeymapTests
{
    private static readonly GridKeymap Map = new();

    [Theory]
    [InlineData(Key.Home, KeyModifiers.None, GridCommand.FirstColumn)]
    [InlineData(Key.End, KeyModifiers.None, GridCommand.LastColumn)]
    [InlineData(Key.Home, KeyModifiers.Control, GridCommand.FirstRow)]
    [InlineData(Key.End, KeyModifiers.Control, GridCommand.LastRow)]
    [InlineData(Key.Enter, KeyModifiers.None, GridCommand.BeginEdit)]
    [InlineData(Key.F2, KeyModifiers.None, GridCommand.BeginEdit)]
    [InlineData(Key.Down, KeyModifiers.None, GridCommand.MoveDown)]
    [InlineData(Key.PageDown, KeyModifiers.None, GridCommand.PageDown)]
    [InlineData(Key.A, KeyModifiers.Control, GridCommand.SelectAll)]
    [InlineData(Key.I, KeyModifiers.Control, GridCommand.InvertSelection)]
    [InlineData(Key.C, KeyModifiers.Control, GridCommand.Copy)]
    [InlineData(Key.Enter, KeyModifiers.Control, GridCommand.CommitRow)]
    [InlineData(Key.Escape, KeyModifiers.None, GridCommand.DiscardRow)]
    [InlineData(Key.Tab, KeyModifiers.None, GridCommand.None)]
    public void Navegacao(Key key, KeyModifiers modifiers, GridCommand expected)
    {
        Assert.Equal(expected, Map.Resolve(key, modifiers, GridKeyContext.Navigation));
    }

    [Theory]
    [InlineData(Key.Tab, KeyModifiers.None, GridCommand.NextCell)]
    [InlineData(Key.Tab, KeyModifiers.Shift, GridCommand.PreviousCell)]
    [InlineData(Key.Escape, KeyModifiers.None, GridCommand.CancelEdit)]
    [InlineData(Key.Enter, KeyModifiers.None, GridCommand.EndEdit)]
    [InlineData(Key.Home, KeyModifiers.None, GridCommand.None)]
    [InlineData(Key.C, KeyModifiers.Control, GridCommand.None)]
    public void Edicao(Key key, KeyModifiers modifiers, GridCommand expected)
    {
        Assert.Equal(expected, Map.Resolve(key, modifiers, GridKeyContext.Editing));
    }

    [Fact]
    public void CtrlInsertNaoEhCopy()
    {
        // No HeidiSQL Ctrl+Ins duplica a linha (FAM-G09); não pode ficar como Copy.
        Assert.Equal(GridCommand.None, Map.Resolve(Key.Insert, KeyModifiers.Control, GridKeyContext.Navigation));
    }

    [Fact]
    public void KeymapEhReconfiguravel()
    {
        var map = new GridKeymap { Bindings = [new(Key.F5, KeyModifiers.None, GridKeyContext.Navigation, GridCommand.Copy)] };

        Assert.Equal(GridCommand.Copy, map.Resolve(Key.F5, KeyModifiers.None, GridKeyContext.Navigation));
        Assert.Equal(GridCommand.None, map.Resolve(Key.C, KeyModifiers.Control, GridKeyContext.Navigation));
    }
}
