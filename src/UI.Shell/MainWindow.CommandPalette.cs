using Avalonia.Input;
using Eunomia.App.Commands;
using Eunomia.UI.Shell.Input;
using Eunomia.UI.Shell.Views;

namespace Eunomia.UI.Shell;

/// <summary>
/// Paleta de comandos (Ctrl+Shift+P; roadmap 1.x #11): a lista é feita antes de abrir, com o foco ainda na janela, para
/// os comandos que dependem do controlo focado (grid, editor, árvore) aparecerem; ao escolher, o foco volta e o comando
/// corre como se viesse do menu.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Paleta aberta (testes).</summary>
    internal CommandPaletteWindow? CommandPalette { get; private set; }

    private void InitCommandPalette()
    {
        _context.Commands.SetHandler("tools.commandPalette", _ => new ValueTask(CommandPaletteAsync()));
    }

    private async Task CommandPaletteAsync()
    {
        var commands = _context.Commands;
        var focused = FocusManager?.GetFocusedElement();
        var available = commands.All
            .Where(c => c.Id != "tools.commandPalette" && !c.Hidden)
            .Select(c => (Command: c, State: commands.StateOf(c.Id)))
            .Where(x => x.State.IsImplemented && x.State.CanExecute)
            .Select(x => new CommandMatch(x.Command.Id, x.State.Caption, x.Command.Category, KeyChordMapper.ToGesture(commands.ShortcutOf(x.Command.Id))?.ToString(), x.State.IsChecked))
            .ToList();
        var palette = new CommandPaletteWindow(available);
        CommandPalette = palette;
        string? id;
        try
        {
            id = await palette.ShowDialog<string?>(this);
        }
        finally
        {
            CommandPalette = null;
        }
        focused?.Focus();
        if (id is not null)
        {
            await commands.ExecuteAsync(id);
        }
    }
}
