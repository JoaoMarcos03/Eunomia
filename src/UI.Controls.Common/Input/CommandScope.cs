using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Eunomia.UI.Controls.Common.Input;

/// <summary>
/// Marca um controlo com o contexto de comandos onde o foco está (secção 21.2: "grid", "editor", "filterPanel"…).
/// O keymap junta os contextos do elemento focado até à janela, do mais interior para o mais exterior.
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

    /// <summary>Contextos a partir do elemento focado, do mais interior para o mais exterior, sem repetidos.</summary>
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
/// Alvo dos comandos do menu Edit (Copy, Cut, Paste, Select all, Undo…). O HeidiSQL encaminha estas actions para o
/// controlo focado; aqui cada controlo próprio implementa a interface e o shell procura-a a partir do foco.
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

/// <summary>Procura o alvo de edição a partir do elemento focado (o próprio, um adaptador ou um ancestral).</summary>
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
