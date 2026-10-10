using Avalonia.Input;
using Eunomia.App.Commands;
using Eunomia.UI.Shell.Input;
using Eunomia.UI.Shell.Views;

namespace Eunomia.UI.Shell;

/// <summary>
/// Command palette (Ctrl+Shift+P; roadmap 1.x #11): the list is built before opening, with the focus still on the window,
/// so commands that depend on the focused control (grid, editor, tree) show up; on choosing, the focus goes back and the
/// command runs as if it came from the menu.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Open palette (tests).</summary>
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
