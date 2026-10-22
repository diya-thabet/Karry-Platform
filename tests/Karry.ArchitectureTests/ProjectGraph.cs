using System.Xml.Linq;

namespace Karry.ArchitectureTests;

/// <summary>A product project and what it depends on, read from its <c>.csproj</c>.</summary>
public sealed record ProjectNode(
    string Name,
    IReadOnlySet<string> ProjectReferences,
    IReadOnlySet<string> PackageReferences,
    IReadOnlySet<string> FrameworkReferences)
{
    /// <summary>Module name for <c>Karry.Modules.{Module}.{Layer}</c> projects, otherwise null.</summary>
    public string? Module => Parts is [_, "Modules", var module, _] ? module : null;

    /// <summary>Layer for <c>Karry.Modules.{Module}.{Layer}</c> projects, otherwise null.</summary>
    public string? Layer => Parts is [_, "Modules", _, var layer] ? layer : null;

    private string[] Parts => Name.Split('.');
}

/// <summary>The project graph of the product (<c>src/</c> and <c>tests/</c>), plus the projects listed in the solution.</summary>
public sealed class ProjectGraph
{
    private static readonly string[] ProductDirectories = ["src", "tests"];

    public ProjectGraph(IEnumerable<ProjectNode> projects, IEnumerable<string> solutionProjects)
    {
        Projects = projects.ToDictionary(p => p.Name, StringComparer.Ordinal);
        SolutionProjects = solutionProjects.ToHashSet(StringComparer.Ordinal);
    }

    public IReadOnlyDictionary<string, ProjectNode> Projects { get; }

    public IReadOnlySet<string> SolutionProjects { get; }

    public static ProjectGraph Load(string repositoryRoot)
    {
        var projectFiles = ProductDirectories
            .Select(dir => Path.Combine(repositoryRoot, dir))
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.csproj", SearchOption.AllDirectories))
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(segment => segment is "bin" or "obj"));

        var nodes = projectFiles.Select(ReadProject).ToList();

        var solution = XDocument.Load(Path.Combine(repositoryRoot, "Karry.slnx"));
        var solutionProjects = solution.Descendants("Project")
            .Select(p => Path.GetFileNameWithoutExtension((string?)p.Attribute("Path") ?? string.Empty));

        return new ProjectGraph(nodes, solutionProjects);
    }

    public static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Karry.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Karry.slnx not found above the test output directory.");
    }

    private static ProjectNode ReadProject(string path)
    {
        var document = XDocument.Load(path);
        IReadOnlySet<string> Includes(string element, Func<string, string> map) =>
            document.Descendants(element)
                .Select(e => (string?)e.Attribute("Include"))
                .OfType<string>()
                .Select(map)
                .ToHashSet(StringComparer.Ordinal);

        return new ProjectNode(
            Path.GetFileNameWithoutExtension(path),
            Includes("ProjectReference", include => Path.GetFileNameWithoutExtension(include.Replace('\\', '/'))),
            Includes("PackageReference", include => include),
            Includes("FrameworkReference", include => include));
    }
}
