using Avalonia.Headless.XUnit;

namespace Eunomia.UI.Headless.Tests;

/// <summary>
/// Inventário da Fase 13: comandos do catálogo sem handler na janela principal. Os da 27.2 (<c>AvailableIn</c>) e os
/// desta lista ficam escondidos nos menus e na toolbar; um comando novo sem handler tem de entrar aqui de propósito.
/// </summary>
public sealed class CommandInventoryTests
{
    /// <summary>Sem handler por decisão (ver "Decisões provisórias" no progresso.md).</summary>
    private static readonly string[] WithoutHandler =
    [
        "edit.copyFormatted",
        "help.changelog", "help.downloadPage", "help.forum", "help.general",
    ];

    [AvaloniaFact]
    public void SoFicamSemHandlerOsDaVersaoSeguinteEOsDecididos()
    {
        // Sessões configuradas (sem ligar): os comandos de sessão só existem com elas.
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
