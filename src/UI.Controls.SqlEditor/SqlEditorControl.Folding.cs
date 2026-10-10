using Avalonia.Threading;
using AvaloniaEdit.Folding;

namespace Eunomia.UI.Controls.SqlEditor;

/// <summary>
/// Code folding (Query ▸ Code folding; secção 6 da spec): regiões entre <c>#region</c> e <c>#endregion</c>, recalculadas
/// 300 ms depois da última edição. A margem com os marcadores só aparece quando o texto tem regiões, para um editor sem
/// regiões ficar igual. Insert region start/end marker e Fold selection escrevem os marcadores num só undo.
/// </summary>
public sealed partial class SqlEditorControl
{
    private FoldingManager? _folding;
    private DispatcherTimer? _foldingTimer;
    private bool _codeFolding;

    /// <summary>Liga ou desliga o code folding; desligar desdobra tudo e tira a margem.</summary>
    public bool CodeFolding
    {
        get => _codeFolding;
        set
        {
            _codeFolding = value;
            UpdateFoldings();
        }
    }

    /// <summary>Regiões conhecidas e se estão dobradas (testes).</summary>
    public IReadOnlyList<(int Start, int End, bool Folded)> Foldings =>
        _folding is null ? [] : [.. _folding.AllFoldings.Select(f => (f.StartOffset, f.EndOffset, f.IsFolded))];

    /// <summary>Dobra a região que começa em <paramref name="offset"/> (testes e Fold selection).</summary>
    public void FoldAt(int offset)
    {
        if (_folding?.GetFoldingsAt(offset).FirstOrDefault() is { } section)
        {
            section.IsFolded = true;
        }
    }

    /// <summary>Recalcula as regiões já (sem esperar pela pausa na escrita).</summary>
    public void UpdateFoldings()
    {
        _foldingTimer?.Stop();
        var regions = _codeFolding ? RegionFolding.Find(Editor.Document, line => _colorizer.StateAt(Editor.Document, line)) : [];
        if (regions.Count == 0)
        {
            if (_folding is { } manager)
            {
                FoldingManager.Uninstall(manager);
                _folding = null;
            }
            return;
        }
        if (_folding is null)
        {
            _folding = FoldingManager.Install(Editor.TextArea);
            ApplyFoldingTheme();
        }
        _folding.UpdateFoldings(regions.Select(r => new NewFolding(r.StartOffset, r.EndOffset) { Name = r.Name }), -1);
    }

    /// <summary>Marcadores [+]/[−] com as cores do editor (por omissão o AvaloniaEdit pinta-os de branco, também no tema escuro).</summary>
    private void ApplyFoldingTheme()
    {
        foreach (var margin in Editor.TextArea.LeftMargins.OfType<FoldingMargin>())
        {
            margin.FoldingMarkerBrush = _palette.Gutter;
            margin.FoldingMarkerBackgroundBrush = _palette.Background;
            margin.SelectedFoldingMarkerBrush = _palette.Foreground;
            margin.SelectedFoldingMarkerBackgroundBrush = _palette.Background;
        }
    }

    /// <summary>Insert region start marker: uma linha <c>#region </c> antes da linha do cursor, com o cursor no fim dela.</summary>
    public void InsertRegionStart()
    {
        var line = Editor.Document.GetLineByOffset(Editor.CaretOffset);
        Editor.Document.Insert(line.Offset, RegionFolding.Start + " " + Environment.NewLine);
        Editor.CaretOffset = line.Offset + RegionFolding.Start.Length + 1;
        UpdateFoldings();
    }

    /// <summary>Insert region end marker: uma linha <c>#endregion</c> depois da linha do cursor.</summary>
    public void InsertRegionEnd()
    {
        var line = Editor.Document.GetLineByOffset(Editor.CaretOffset);
        Editor.Document.Insert(line.Offset + line.Length, Environment.NewLine + RegionFolding.End);
        UpdateFoldings();
    }

    /// <summary>Fold selection: as linhas da seleção entre marcadores novos, já dobradas.</summary>
    public void FoldSelection()
    {
        var document = Editor.Document;
        var selection = Editor.TextArea.Selection;
        if (selection.IsEmpty || !_codeFolding)
        {
            return;
        }
        var first = document.GetLineByOffset(Editor.SelectionStart);
        var last = document.GetLineByOffset(Math.Max(Editor.SelectionStart, Editor.SelectionStart + Editor.SelectionLength - 1));
        var start = first.Offset;
        using (document.RunUpdate())
        {
            document.Insert(last.Offset + last.Length, Environment.NewLine + RegionFolding.End);
            document.Insert(start, RegionFolding.Start + Environment.NewLine);
        }
        UpdateFoldings();
        FoldAt(start);
        Editor.TextArea.ClearSelection();
    }

    private void ScheduleFoldings()
    {
        if (!_codeFolding && _folding is null)
        {
            return;
        }
        if (_foldingTimer is null)
        {
            _foldingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _foldingTimer.Tick += (_, _) => UpdateFoldings();
        }
        _foldingTimer.Stop();
        _foldingTimer.Start();
    }
}
