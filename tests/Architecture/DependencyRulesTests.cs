using System.Text.Json;

namespace Eunomia.Architecture.Tests;

/// <summary>
/// Sections 21 and 22: no project in src/Engine.*, src/Drivers.* or src/App depends on UI
/// (Avalonia, WinForms, WPF or UI.*/App.* projects), neither directly nor through transitive dependencies.
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
                              // App (commands, navigation, jobs) also stays below Presentation (section 21).
                              || Path.GetFileName(d) == "App"
                              // Infra implements App/Engine ports; Presentation has stores testable without UI.
                              || Path.GetFileName(d).StartsWith("Infra.", StringComparison.Ordinal)
                              || Path.GetFileName(d) == "Presentation"))
        {
            data.Add(Path.GetFileName(dir));
        }
        return data;
    }

    [Fact]
    public void ThereAreCoreProjectsToCheck()
    {
        Assert.NotEmpty(CoreProjects());
    }

    [Theory]
    [MemberData(nameof(CoreProjects))]
    public void CoreProjectUsesNoUiFrameworks(string project)
    {
        var csproj = File.ReadAllText(Path.Combine(RepoRoot, "src", project, project + ".csproj"));

        Assert.DoesNotContain("UseWindowsForms", csproj, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UseWPF", csproj, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Microsoft.WindowsDesktop", csproj, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(CoreProjects))]
    public void CoreProjectHasNoUiDependencies(string project)
    {
        var assets = Path.Combine(RepoRoot, "src", project, "obj", "project.assets.json");
        Assert.True(File.Exists(assets), $"{project} was not restored: {assets} is missing");

        using var doc = JsonDocument.Parse(File.ReadAllText(assets));

        // "libraries" includes packages and projects, direct and transitive, in the "Name/version" format.
        var forbidden = doc.RootElement.GetProperty("libraries").EnumerateObject()
            .Select(l => l.Name.Split('/')[0])
            .Where(name => ForbiddenLibraryPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        Assert.Empty(forbidden);

        // Framework references (WinForms and WPF come through Microsoft.WindowsDesktop.App).
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
        return dir?.FullName ?? throw new InvalidOperationException("Repository root (Eunomia.slnx) not found.");
    }
}
