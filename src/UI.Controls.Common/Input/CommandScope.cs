using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Eunomia.UI.Controls.Common.Input;

/// <summary>
/// Tags a control with the command context the focus is in (section 21.2: "grid", "editor", "filterPanel"…).
/// The keymap gathers the contexts from the focused element up to the window, from the innermost to the outermost.
/// </summary>
public sealed class CommandScope
{
    private CommandScope()
    {
    }

    public static readonly AttachedProperty<string?> ContextProperty =
        AvaloniaProperty.RegisterAttached<CommandScope, Control, string?>("Context");

    public static string? GetContext(Control control) => control.GetValue(ContextProperty);

    public static void SetContext(Control control, string? value) => control.SetValue(ContextProperty, value);

    /// <summary>Contexts from the focused element, from the innermost to the outermost, without duplicates.</summary>
    public static IReadOnlyList<string> ContextsOf(IInputElement? focused)
    {
        var result = new List<string>();
        for (var v = focused as Visual; v is not null; v = v.GetVisualParent())
        {
            if (v is Control c && GetContext(c) is { Length: > 0 } context && !result.Contains(context))
            {
                result.Add(context);
            }
        }
        return result;
    }
}

/// <summary>
/// Target of the Edit menu commands (Copy, Cut, Paste, Select all, Undo…). HeidiSQL routes these actions to the
/// focused control; here each of our controls implements the interface and the shell looks for it from the focus.
/// </summary>
public interface IEditTarget
{
    bool CanCopy { get; }

    bool CanCut { get; }

    bool CanPaste { get; }

    bool CanSelectAll { get; }

    bool CanUndo { get; }

    Task CopyAsync();

    Task CutAsync();

    Task PasteAsync();

    void SelectAll();

    void Undo();
}

/// <summary>Looks for the edit target from the focused element (itself, an adapter or an ancestor).</summary>
public static class EditTargets
{
    public static IEditTarget? Find(IInputElement? focused)
    {
        for (var v = focused as Visual; v is not null; v = v.GetVisualParent())
        {
            switch (v)
            {
                case IEditTarget target:
                    return target;
                case TextBox box:
                    return new TextBoxEditTarget(box);
            }
        }
        return null;
    }

    private sealed class TextBoxEditTarget(TextBox box) : IEditTarget
    {
        public bool CanCopy => box.CanCopy;

        public bool CanCut => box.CanCut;

        public bool CanPaste => box.CanPaste;

        public bool CanSelectAll => true;

        public bool CanUndo => box.CanUndo;

        public Task CopyAsync()
        {
            box.Copy();
            return Task.CompletedTask;
        }

        public Task CutAsync()
        {
            box.Cut();
            return Task.CompletedTask;
        }

        public Task PasteAsync()
        {
            box.Paste();
            return Task.CompletedTask;
        }

        public void SelectAll() => box.SelectAll();

        public void Undo() => box.Undo();
    }
}
