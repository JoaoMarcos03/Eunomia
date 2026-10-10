using Avalonia.Threading;
using AvaloniaEdit.Folding;

namespace Eunomia.UI.Controls.SqlEditor;

/// <summary>
/// Code folding (Query ▸ Code folding; spec section 6): regions between <c>#region</c> and <c>#endregion</c>, recomputed
/// 300 ms after the last edit. The margin with the markers only appears when the text has regions, so an editor without
/// regions looks the same. Insert region start/end marker and Fold selection write the markers as a single undo step.
/// </summary>
public sealed partial class SqlEditorControl
{
    private FoldingManager? _folding;
    private DispatcherTimer? _foldingTimer;
    private bool _codeFolding;

    /// <summary>Turns code folding on or off; turning it off unfolds everything and removes the margin.</summary>
    public bool CodeFolding
    {
        get => _codeFolding;
        set
        {
            _codeFolding = value;
            UpdateFoldings();
        }
    }

    /// <summary>Known regions and whether they are folded (tests).</summary>
    public IReadOnlyList<(int Start, int End, bool Folded)> Foldings =>
        _folding is null ? [] : [.. _folding.AllFoldings.Select(f => (f.StartOffset, f.EndOffset, f.IsFolded))];

    /// <summary>Folds the region that starts at <paramref name="offset"/> (tests and Fold selection).</summary>
    public void FoldAt(int offset)
    {
        if (_folding?.GetFoldingsAt(offset).FirstOrDefault() is { } section)
        {
            section.IsFolded = true;
        }
    }

    /// <summary>Recomputes the regions now (without waiting for a pause in typing).</summary>
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

    /// <summary>[+]/[−] markers with the editor colours (by default AvaloniaEdit paints them white, in the dark theme too).</summary>
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

    /// <summary>Insert region start marker: a <c>#region </c> line before the cursor line, with the cursor at its end.</summary>
    public void InsertRegionStart()
    {
        var line = Editor.Document.GetLineByOffset(Editor.CaretOffset);
        Editor.Document.Insert(line.Offset, RegionFolding.Start + " " + Environment.NewLine);
        Editor.CaretOffset = line.Offset + RegionFolding.Start.Length + 1;
        UpdateFoldings();
    }

    /// <summary>Insert region end marker: an <c>#endregion</c> line after the cursor line.</summary>
    public void InsertRegionEnd()
    {
        var line = Editor.Document.GetLineByOffset(Editor.CaretOffset);
        Editor.Document.Insert(line.Offset + line.Length, Environment.NewLine + RegionFolding.End);
        UpdateFoldings();
    }

    /// <summary>Fold selection: the selected lines between new markers, already folded.</summary>
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
