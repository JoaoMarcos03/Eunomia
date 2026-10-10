using Eunomia.App.Commands;

namespace Eunomia.App.Tests;

/// <summary>Pesquisa da paleta de comandos (Ctrl+Shift+P).</summary>
public sealed class CommandSearchTests
{
    private static readonly CommandMatch[] Commands =
    [
        new("query.newTab", "New query tab", "SQL", "Ctrl+T", false),
        new("query.run", "Run", "SQL", "F9", false),
        new("tools.preferences", "Preferences", "Tools", null, false),
        new("export.gridRows", "Export grid rows", "Export/Import", null, false),
        new("query.wordWrap", "Wrap long lines", "SQL", null, true),
        new("tools.maintenance", "Maintenance", "Tools", null, false),
    ];

    [Fact]
    public void SemTextoMostraTudoPelaLegenda()
    {
        Assert.Equal(["Export grid rows", "Maintenance", "New query tab", "Preferences", "Run", "Wrap long lines"],
            CommandSearch.Filter(Commands, "  ").Select(c => c.Caption));
    }

    [Fact]
    public void TodasAsPalavrasNaLegendaOuNaCategoriaSemMaiusculas()
    {
        Assert.Equal(["query.newTab"], CommandSearch.Filter(Commands, "NEW tab").Select(c => c.Id));
        Assert.Equal(["tools.maintenance", "tools.preferences"], CommandSearch.Filter(Commands, "tools").Select(c => c.Id));
        Assert.Empty(CommandSearch.Filter(Commands, "drop"));
    }

    [Fact]
    public void QuemComecaPeloTextoVemPrimeiro()
    {
        // "r": "Run" começa pelo texto; "Export grid rows" tem uma palavra a começar; as outras só o contêm.
        Assert.Equal(["Run", "Export grid rows", "New query tab", "Preferences", "Wrap long lines"], CommandSearch.Filter(Commands, "r").Select(c => c.Caption));
    }

    [Fact]
    public void LimiteDeResultados()
    {
        Assert.Equal(2, CommandSearch.Filter(Commands, null, max: 2).Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => CommandSearch.Filter(Commands, null, max: 0));
    }
}
