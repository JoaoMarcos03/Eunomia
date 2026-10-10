using Avalonia.Input;
using Eunomia.UI.Controls.Common.Input;

namespace Eunomia.UI.Controls.Grid;

public enum GridCommand
{
    None,
    MoveUp,
    MoveDown,
    MoveLeft,
    MoveRight,
    PageUp,
    PageDown,
    FirstColumn,
    LastColumn,
    FirstRow,
    LastRow,
    ExtendUp,
    ExtendDown,
    ExtendPageUp,
    ExtendPageDown,
    ExtendFirstRow,
    ExtendLastRow,
    FocusUp,
    FocusDown,
    BeginEdit,
    EndEdit,
    CancelEdit,
    NextCell,
    PreviousCell,
    CommitRow,
    DiscardRow,
    SelectAll,
    InvertSelection,
    Copy,
}

/// <summary>Shortcut context: grid navigation or an open in-place editor.</summary>
public enum GridKeyContext
{
    Navigation,
    Editing,
}

public readonly record struct GridKeyBinding(Key Key, KeyModifiers Modifiers, GridKeyContext Context, GridCommand Command);

/// <summary>
/// Declarative grid keymap (section 15.1, InputController): the keys of section 4.4 and FAM-G05/G06.
/// Reconfigurable by replacing <see cref="Bindings"/>. Conflicts are resolved by context
/// (Esc cancels editing if the editor is open; otherwise it discards the row).
/// </summary>
public sealed class GridKeymap
{
    public static IReadOnlyList<GridKeyBinding> Default { get; } =
    [
        new(Key.Up, KeyModifiers.None, GridKeyContext.Navigation, GridCommand.MoveUp),
        new(Key.Down, KeyModifiers.None, GridKeyContext.Navigation, GridCommand.MoveDown),
        new(Key.Left, KeyModifiers.None, GridKeyContext.Navigation, GridCommand.MoveLeft),
        new(Key.Right, KeyModifiers.None, GridKeyContext.Navigation, GridCommand.MoveRight),
        new(Key.PageUp, KeyModifiers.None, GridKeyContext.Navigation, GridCommand.PageUp),
        new(Key.PageDown, KeyModifiers.None, GridKeyContext.Navigation, GridCommand.PageDown),
        new(Key.Home, KeyModifiers.None, GridKeyContext.Navigation, GridCommand.FirstColumn),
        new(Key.End, KeyModifiers.None, GridKeyContext.Navigation, GridCommand.LastColumn),
        new(Key.Home, KeyModifiers.Control, GridKeyContext.Navigation, GridCommand.FirstRow),
        new(Key.End, KeyModifiers.Control, GridKeyContext.Navigation, GridCommand.LastRow),
        new(Key.Up, KeyModifiers.Shift, GridKeyContext.Navigation, GridCommand.ExtendUp),
        new(Key.Down, KeyModifiers.Shift, GridKeyContext.Navigation, GridCommand.ExtendDown),
        new(Key.PageUp, KeyModifiers.Shift, GridKeyContext.Navigation, GridCommand.ExtendPageUp),
        new(Key.PageDown, KeyModifiers.Shift, GridKeyContext.Navigation, GridCommand.ExtendPageDown),
        new(Key.Home, KeyModifiers.Control | KeyModifiers.Shift, GridKeyContext.Navigation, GridCommand.ExtendFirstRow),
        new(Key.End, KeyModifiers.Control | KeyModifiers.Shift, GridKeyContext.Navigation, GridCommand.ExtendLastRow),
        new(Key.Up, KeyModifiers.Control, GridKeyContext.Navigation, GridCommand.FocusUp),
        new(Key.Down, KeyModifiers.Control, GridKeyContext.Navigation, GridCommand.FocusDown),
        new(Key.Enter, KeyModifiers.None, GridKeyContext.Navigation, GridCommand.BeginEdit),
        new(Key.F2, KeyModifiers.None, GridKeyContext.Navigation, GridCommand.BeginEdit),
        new(Key.Enter, KeyModifiers.Control, GridKeyContext.Navigation, GridCommand.CommitRow),
        new(Key.Escape, KeyModifiers.None, GridKeyContext.Navigation, GridCommand.DiscardRow),
        new(Key.A, KeyModifiers.Control, GridKeyContext.Navigation, GridCommand.SelectAll),
        new(Key.I, KeyModifiers.Control, GridKeyContext.Navigation, GridCommand.InvertSelection),
        new(Key.C, KeyModifiers.Control, GridKeyContext.Navigation, GridCommand.Copy),

        new(Key.Enter, KeyModifiers.None, GridKeyContext.Editing, GridCommand.EndEdit),
        new(Key.Escape, KeyModifiers.None, GridKeyContext.Editing, GridCommand.CancelEdit),
        new(Key.Tab, KeyModifiers.None, GridKeyContext.Editing, GridCommand.NextCell),
        new(Key.Tab, KeyModifiers.Shift, GridKeyContext.Editing, GridCommand.PreviousCell),
        new(Key.Up, KeyModifiers.None, GridKeyContext.Editing, GridCommand.MoveUp),
        new(Key.Down, KeyModifiers.None, GridKeyContext.Editing, GridCommand.MoveDown),
        new(Key.Enter, KeyModifiers.Control, GridKeyContext.Editing, GridCommand.CommitRow),
    ];

    public IReadOnlyList<GridKeyBinding> Bindings { get; set; } = Default;

    public GridCommand Resolve(Key key, KeyModifiers modifiers, GridKeyContext context)
    {
        modifiers = PlatformKeys.Normalize(modifiers);
        foreach (var b in Bindings)
        {
            if (b.Key == key && b.Modifiers == modifiers && b.Context == context)
            {
                return b.Command;
            }
        }
        return GridCommand.None;
    }
}
