using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Eunomia.Engine.Results;

namespace Eunomia.UI.Controls.Grid;

/// <summary>
/// Automation peer do grid (secção 15.1). O Avalonia 12.1 não tem os padrões UIA Grid/Table
/// (<c>IGridProvider</c>, <c>ITableProvider</c>), por isso o spike expõe o grid como <c>DataGrid</c> com um
/// filho <c>DataItem</c> para a célula focada, com nome "header, valor" e ajuda "linha N, coluna M".
/// A mudança de célula dispara alteração de nome com <c>LiveSetting</c> polite, para o leitor de ecrã anunciar.
/// Validação com Narrator e NVDA: manual (relatório).
/// </summary>
public sealed class DataGridAutomationPeer : ControlAutomationPeer
{
    private readonly FocusedCellAutomationPeer _cell;
    private string _lastName = string.Empty;

    public DataGridAutomationPeer(DataGridControl owner)
        : base(owner)
    {
        Grid = owner;
        _cell = new FocusedCellAutomationPeer(this);
        owner.Selection.Changed += (_, _) => OnFocusedCellChanged();
    }

    public DataGridControl Grid { get; }

    /// <summary>Texto que o leitor de ecrã lê para a célula focada.</summary>
    public string FocusedCellName
    {
        get
        {
            var row = Grid.Selection.FocusedRow;
            var column = Grid.Selection.FocusedColumn;
            if (Grid.Source is null || row < 0 || column >= Grid.Source.Schema.Count)
            {
                return string.Empty;
            }
            var header = Grid.Source.Schema[column].Name;
            var value = Grid.GetDisplayText(row, column) ?? string.Empty;
            return $"{header}, {value}";
        }
    }

    public string FocusedCellHelp => $"row {Grid.Selection.FocusedRow + 1}, column {Grid.Selection.FocusedColumn + 1}";

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.DataGrid;

    protected override string GetNameCore() => "Data grid";

    protected override string? GetHelpTextCore() => Grid.Selection.StatusText;

    protected override AutomationLiveSetting GetLiveSettingCore() => AutomationLiveSetting.Polite;

    protected override IReadOnlyList<AutomationPeer> GetOrCreateChildrenCore() => [_cell];

    private void OnFocusedCellChanged()
    {
        var name = FocusedCellName;
        _cell.RaiseName(_lastName, name);
        _lastName = name;
    }

    private sealed class FocusedCellAutomationPeer(DataGridAutomationPeer parent) : AutomationPeer
    {
        public void RaiseName(string oldName, string newName) =>
            RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, oldName, newName);

        protected override void BringIntoViewCore()
        {
        }

        protected override string? GetAcceleratorKeyCore() => null;

        protected override string? GetAccessKeyCore() => null;

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.DataItem;

        protected override string? GetAutomationIdCore() => "FocusedCell";

        protected override Rect GetBoundingRectangleCore()
        {
            var grid = parent.Grid;
            if (grid.Selection.FocusedRow < 0 || grid.Columns.Count == 0)
            {
                return default;
            }
            var cell = grid.CellBounds(grid.Selection.FocusedRow, grid.Selection.FocusedColumn);
            return grid.TranslatePoint(cell.TopLeft, (Visual?)Avalonia.Controls.TopLevel.GetTopLevel(grid) ?? grid) is { } p
                ? new Rect(p, cell.Size)
                : cell;
        }

        protected override IReadOnlyList<AutomationPeer> GetOrCreateChildrenCore() => [];

        protected override string GetClassNameCore() => "DataGridCell";

        protected override AutomationPeer? GetLabeledByCore() => null;

        protected override string GetNameCore() => parent.FocusedCellName;

        protected override string? GetHelpTextCore() => parent.FocusedCellHelp;

        protected override AutomationLandmarkType? GetLandmarkTypeCore() => null;

        protected override int GetHeadingLevelCore() => 0;

        protected override AutomationPeer GetParentCore() => parent;

        protected override bool TrySetParent(AutomationPeer? newParent) => false;

        protected override bool HasKeyboardFocusCore() => parent.Grid.IsKeyboardFocusWithin;

        protected override bool IsContentElementCore() => true;

        protected override bool IsControlElementCore() => true;

        protected override bool IsEnabledCore() => true;

        protected override bool IsKeyboardFocusableCore() => true;

        protected override void SetFocusCore() => parent.Grid.Focus();

        protected override bool ShowContextMenuCore() => false;

        protected override AutomationLiveSetting GetLiveSettingCore() => AutomationLiveSetting.Polite;

        protected override bool IsOffscreenCore() => false;

        protected override string? GetItemTypeCore() => null;

        protected override string? GetItemStatusCore() => DisplayFormatter.NullText == parent.Grid.GetDisplayText(parent.Grid.Selection.FocusedRow, parent.Grid.Selection.FocusedColumn) ? "NULL" : null;

        protected override string? GetPlaceholderTextCore() => null;

        protected override string GetLocalizedControlTypeCore() => "cell";
    }
}
