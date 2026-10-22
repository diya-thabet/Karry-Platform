namespace Karry.ArchitectureTests;

/// <summary>Builds small synthetic project graphs so every rule can be shown to catch a violation.</summary>
internal sealed class GraphBuilder
{
    private readonly List<ProjectNode> projects = [];

    /// <summary>A clean graph: kernel, building blocks, hosts and two well-formed modules.</summary>
    public static GraphBuilder Clean()
    {
        var builder = new GraphBuilder()
            .Project("Karry.SharedKernel")
            .Project("Karry.BuildingBlocks", references: ["Karry.SharedKernel"], frameworks: ["Microsoft.AspNetCore.App"])
            .Project("Karry.Host", references: ["Karry.BuildingBlocks", "Karry.Modules.Fuel.Api", "Karry.Modules.Assets.Api"], packages: ["Npgsql"])
            .Project("Karry.Workers", references: ["Karry.BuildingBlocks"]);

        foreach (var module in new[] { "Fuel", "Assets" })
        {
            builder
                .Project($"Karry.Modules.{module}.Contracts", references: ["Karry.SharedKernel"])
                .Project($"Karry.Modules.{module}.Domain", references: ["Karry.SharedKernel"])
                .Project($"Karry.Modules.{module}.Application", references: [$"Karry.Modules.{module}.Domain", $"Karry.Modules.{module}.Contracts", "Karry.BuildingBlocks"])
                .Project($"Karry.Modules.{module}.Infrastructure", references: [$"Karry.Modules.{module}.Application"], packages: ["Microsoft.EntityFrameworkCore", "Npgsql.EntityFrameworkCore.PostgreSQL"])
                .Project($"Karry.Modules.{module}.Api", references: [$"Karry.Modules.{module}.Application", $"Karry.Modules.{module}.Infrastructure"], frameworks: ["Microsoft.AspNetCore.App"])
                .Project($"Karry.Modules.{module}.Tests", references: [$"Karry.Modules.{module}.Api"]);
        }

        // Fuel legitimately consumes the Assets module through its public contract.
        return builder.AddReference("Karry.Modules.Fuel.Application", "Karry.Modules.Assets.Contracts");
    }

    public GraphBuilder Project(string name, string[]? references = null, string[]? packages = null, string[]? frameworks = null)
    {
        projects.RemoveAll(p => p.Name == name);
        projects.Add(new ProjectNode(name, Set(references), Set(packages), Set(frameworks)));
        return this;
    }

    public GraphBuilder AddReference(string from, string to) => Modify(from, p => p with { ProjectReferences = Add(p.ProjectReferences, to) });

    public GraphBuilder AddPackage(string from, string package) => Modify(from, p => p with { PackageReferences = Add(p.PackageReferences, package) });

    public GraphBuilder AddFramework(string from, string framework) => Modify(from, p => p with { FrameworkReferences = Add(p.FrameworkReferences, framework) });

    public ProjectGraph Build(IEnumerable<string>? notInSolution = null)
    {
        var excluded = Set(notInSolution?.ToArray());
        return new ProjectGraph(projects, projects.Select(p => p.Name).Where(n => !excluded.Contains(n)));
    }

    private GraphBuilder Modify(string name, Func<ProjectNode, ProjectNode> change)
    {
        var index = projects.FindIndex(p => p.Name == name);
        if (index < 0)
        {
            throw new InvalidOperationException($"Unknown project {name}");
        }

        projects[index] = change(projects[index]);
        return this;
    }

    private static HashSet<string> Set(string[]? items) => new(items ?? [], StringComparer.Ordinal);

    private static HashSet<string> Add(IReadOnlySet<string> items, string item) => new(items.Append(item), StringComparer.Ordinal);
}
