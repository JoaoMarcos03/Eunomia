using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;

namespace Eunomia.Architecture.Tests;

/// <summary>
/// "The core does not know MySQL" (section 21; roadmap 2.12): outside the driver (<c>Drivers.MySql</c>) and the language
/// layer (<c>Engine.Sql</c>, which keeps the MySQL lexer and words behind <c>ISqlLanguage</c>), no project uses <c>MySql*</c>
/// types or MySqlConnector. <c>App.Desktop</c> (the composition root) chooses the provider and is left out. Reads the type
/// references of the compiled assemblies, so it catches any use (qualified name, <c>using static</c>, attribute or
/// generic argument).
/// </summary>
public sealed class DialectBoundaryTests
{
    private static readonly HashSet<string> MayUseMySql = new(StringComparer.Ordinal) { "Drivers.MySql", "Engine.Sql", "App.Desktop" };

    public static TheoryData<string> CheckedProjects()
    {
        var data = new TheoryData<string>();
        foreach (var project in SourceProjects().Where(p => !MayUseMySql.Contains(p)))
        {
            data.Add(project);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(CheckedProjects))]
    public void ProjectDoesNotUseMySqlTypes(string project)
    {
        var (types, assemblies) = References(project);

        var mySqlTypes = types.Where(t => t.Name.StartsWith("MySql", StringComparison.Ordinal)).Select(t => $"{t.Namespace}.{t.Name}").ToList();
        Assert.True(mySqlTypes.Count == 0, $"{project} uses {string.Join(", ", mySqlTypes)}: go through ISqlLanguage or IDialect");
        Assert.DoesNotContain("MySqlConnector", assemblies);
    }

    [Fact]
    public void ReadingFindsTheMySqlTypesInTheDriver()
    {
        // Positive control: the driver uses MySqlConnector; if reading the metadata did not see them, the test above would always pass.
        var (types, assemblies) = References("Drivers.MySql");

        Assert.Contains(types, t => t.Namespace == "MySqlConnector" && t.Name == "MySqlConnection");
        Assert.Contains("MySqlConnector", assemblies);
    }

    /// <summary>Types from other assemblies that the project references, and the assemblies it references.</summary>
    private static (List<(string Namespace, string Name)> Types, List<string> Assemblies) References(string project)
    {
        var path = Path.Combine(AppContext.BaseDirectory, AssemblyName(project) + ".dll");
        Assert.True(File.Exists(path), $"{project}: {path} is missing (add the project to the Architecture.Tests ProjectReference items)");
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var types = metadata.TypeReferences
            .Select(metadata.GetTypeReference)
            .Select(t => (metadata.GetString(t.Namespace), metadata.GetString(t.Name)))
            .ToList();
        var assemblies = metadata.AssemblyReferences
            .Select(h => metadata.GetString(metadata.GetAssemblyReference(h).Name))
            .ToList();
        return (types, assemblies);
    }

    private static IEnumerable<string> SourceProjects() =>
        Directory.EnumerateDirectories(Path.Combine(RepoRoot, "src"))
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => File.Exists(Path.Combine(RepoRoot, "src", name, name + ".csproj")))
            .Order(StringComparer.Ordinal);

    /// <summary>The project's <c>AssemblyName</c>, or the project name.</summary>
    private static string AssemblyName(string project)
    {
        var csproj = XDocument.Load(Path.Combine(RepoRoot, "src", project, project + ".csproj"));
        return csproj.Descendants("AssemblyName").FirstOrDefault()?.Value ?? project;
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
