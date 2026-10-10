using System.Text.Json;

namespace Eunomia.Architecture.Tests;

/// <summary>
/// Secções 21 e 22: nenhum projeto em src/Engine.*, src/Drivers.* ou src/App depende de UI
/// (Avalonia, WinForms, WPF ou projetos UI.*/App.*), nem diretamente nem por dependências transitivas.
/// </summary>
public sealed class DependencyRulesTests
{
    private static readonly string[] ForbiddenLibraryPrefixes = ["Avalonia", "UI.", "App.", "Spike."];

    public static TheoryData<string> CoreProjects()
    {
        var data = new TheoryData<string>();
        var src = Path.Combine(RepoRoot, "src");
        foreach (var dir in Directory.EnumerateDirectories(src)
                     .Where(d => Path.GetFileName(d).StartsWith("Engine.", StringComparison.Ordinal)
                              || Path.GetFileName(d).StartsWith("Drivers.", StringComparison.Ordinal)
                              // App (comandos, navegação, jobs) também fica abaixo de Presentation (secção 21).
                              || Path.GetFileName(d) == "App"
                              // Infra implementa portas de App/Engine; Presentation tem stores testáveis sem UI.
                              || Path.GetFileName(d).StartsWith("Infra.", StringComparison.Ordinal)
                              || Path.GetFileName(d) == "Presentation"))
        {
            data.Add(Path.GetFileName(dir));
        }
        return data;
    }

    [Fact]
    public void ExistemProjetosCoreParaVerificar()
    {
        Assert.NotEmpty(CoreProjects());
    }

    [Theory]
    [MemberData(nameof(CoreProjects))]
    public void ProjetoCoreNaoUsaFrameworksDeUi(string project)
    {
        var csproj = File.ReadAllText(Path.Combine(RepoRoot, "src", project, project + ".csproj"));

        Assert.DoesNotContain("UseWindowsForms", csproj, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UseWPF", csproj, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Microsoft.WindowsDesktop", csproj, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(CoreProjects))]
    public void ProjetoCoreNaoTemDependenciasDeUi(string project)
    {
        var assets = Path.Combine(RepoRoot, "src", project, "obj", "project.assets.json");
        Assert.True(File.Exists(assets), $"{project} não foi restaurado: falta {assets}");

        using var doc = JsonDocument.Parse(File.ReadAllText(assets));

        // "libraries" inclui pacotes e projetos, diretos e transitivos, no formato "Nome/versão".
        var forbidden = doc.RootElement.GetProperty("libraries").EnumerateObject()
            .Select(l => l.Name.Split('/')[0])
            .Where(name => ForbiddenLibraryPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        Assert.Empty(forbidden);

        // Framework references (WinForms e WPF vêm por Microsoft.WindowsDesktop.App).
        foreach (var framework in doc.RootElement.GetProperty("project").GetProperty("frameworks").EnumerateObject())
        {
            if (framework.Value.TryGetProperty("frameworkReferences", out var refs))
            {
                Assert.DoesNotContain(refs.EnumerateObject(),
                    r => r.Name.StartsWith("Microsoft.WindowsDesktop", StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    private static string RepoRoot { get; } = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eunomia.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repo (Eunomia.slnx) não encontrada.");
    }
}
