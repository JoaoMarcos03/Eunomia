using Avalonia.Headless.XUnit;

namespace Eunomia.UI.Headless.Tests;

/// <summary>
/// Phase 13 inventory: catalog commands without a handler in the main window. The 27.2 ones (<c>AvailableIn</c>) and the
/// ones in this list stay hidden in the menus and the toolbar; a new command without a handler must be added here on purpose.
/// </summary>
public sealed class CommandInventoryTests
{
    /// <summary>Without a handler by decision (see "Provisional decisions" in progress.md).</summary>
    private static readonly string[] WithoutHandler =
    [
        "edit.copyFormatted",
        "help.changelog", "help.downloadPage", "help.forum", "help.general",
    ];

    [AvaloniaFact]
    public void OnlyLaterVersionAndDecidedCommandsLackAHandler()
    {
        // Configured sessions (without connecting): session commands only exist with them.
        var shell = RealServer.CreateShell("Server=127.0.0.1;Port=1;User ID=x;Password=y", string.Empty);
        var registry = shell.Context.Commands;

        var missing = registry.All
            .Where(c => !registry.StateOf(c.Id).IsImplemented && c.AvailableIn is null)
            .Select(c => c.Id)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(WithoutHandler.Order(StringComparer.Ordinal), missing);
        shell.Window.Close();
    }
}
