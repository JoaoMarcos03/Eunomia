using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;

namespace Eunomia.Architecture.Tests;

/// <summary>
/// "O core não conhece MySQL" (secção 21; roadmap 2.12): fora do driver (<c>Drivers.MySql</c>) e da camada de linguagem
/// (<c>Engine.Sql</c>, que tem o lexer e as palavras do MySQL atrás de <c>ISqlLanguage</c>), nenhum projeto usa tipos
/// <c>MySql*</c> nem o MySqlConnector. O <c>App.Desktop</c> (composition root) escolhe o provider e fica de fora. Lê as
/// referências de tipos dos assemblies compilados, por isso apanha qualquer uso (nome qualificado, <c>using static</c>,
/// atributo ou argumento genérico).
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
    public void ProjetoNaoUsaTiposDoMySql(string project)
    {
        var (types, assemblies) = References(project);

        var mySqlTypes = types.Where(t => t.Name.StartsWith("MySql", StringComparison.Ordinal)).Select(t => $"{t.Namespace}.{t.Name}").ToList();
        Assert.True(mySqlTypes.Count == 0, $"{project} usa {string.Join(", ", mySqlTypes)}: passar pelo ISqlLanguage ou pelo IDialect");
        Assert.DoesNotContain("MySqlConnector", assemblies);
    }

    [Fact]
    public void LeituraEncontraOsTiposDoMySqlNoDriver()
    {
        // Controlo positivo: o driver usa o MySqlConnector; se a leitura dos metadados não os visse, o teste acima passava sempre.
        var (types, assemblies) = References("Drivers.MySql");

        Assert.Contains(types, t => t.Namespace == "MySqlConnector" && t.Name == "MySqlConnection");
        Assert.Contains("MySqlConnector", assemblies);
    }

    /// <summary>Tipos de outros assemblies que o projeto referencia, e os assemblies que referencia.</summary>
    private static (List<(string Namespace, string Name)> Types, List<string> Assemblies) References(string project)
    {
        var path = Path.Combine(AppContext.BaseDirectory, AssemblyName(project) + ".dll");
        Assert.True(File.Exists(path), $"{project}: falta {path} (acrescentar o projeto às ProjectReference do Architecture.Tests)");
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

    /// <summary><c>AssemblyName</c> do projeto, ou o nome do projeto.</summary>
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
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repo (Eunomia.slnx) não encontrada.");
    }
}
