namespace Karry.ArchitectureTests;

/// <summary>
/// Boundary rules of the modular monolith (ADR-0001, llm_guidelines.md §5.1). Each rule returns human-readable
/// violations; an empty list means the rule holds.
/// </summary>
public static class ArchitectureRules
{
    private const string SharedKernel = "Karry.SharedKernel";
    private const string BuildingBlocks = "Karry.BuildingBlocks";
    private static readonly string[] Hosts = ["Karry.Host", "Karry.Workers"];

    private static readonly string[] InfrastructurePackagePrefixes =
        ["Microsoft.EntityFrameworkCore", "Npgsql", "Microsoft.AspNetCore", "Dapper", "Quartz", "AWSSDK"];

    /// <summary>Allowed project references for each layer, inside its own module.</summary>
    private static readonly Dictionary<string, string[]> AllowedLayerReferences = new(StringComparer.Ordinal)
    {
        ["Contracts"] = [],
        ["Domain"] = [],
        ["Application"] = ["Domain", "Contracts"],
        ["Infrastructure"] = ["Application", "Domain", "Contracts"],
        ["Api"] = ["Application", "Infrastructure", "Contracts"],
        ["Tests"] = ["Api", "Application", "Infrastructure", "Domain", "Contracts"],
    };

    public static IReadOnlyList<string> All(ProjectGraph graph) =>
    [
        .. ModulesReferenceOtherModulesOnlyThroughContracts(graph),
        .. LayersReferenceOnlyAllowedLayers(graph),
        .. DomainAndContractsHaveNoInfrastructureDependencies(graph),
        .. NothingReferencesAHost(graph),
        .. KernelProjectsStayAtTheBottom(graph),
        .. EveryProjectIsInTheSolution(graph),
        .. ModuleProjectsFollowTheNamingConvention(graph),
    ];

    public static IEnumerable<string> ModulesReferenceOtherModulesOnlyThroughContracts(ProjectGraph graph) =>
        from project in graph.Projects.Values
        where project.Module is not null && project.Layer != "Tests"
        from reference in project.ProjectReferences
        let target = graph.Projects.GetValueOrDefault(reference)
        where target?.Module is not null && target.Module != project.Module && target.Layer != "Contracts"
        select $"{project.Name} references {reference}: another module may only be used through its Contracts project.";

    public static IEnumerable<string> LayersReferenceOnlyAllowedLayers(ProjectGraph graph) =>
        from project in graph.Projects.Values
        where project.Module is not null && project.Layer is not null && AllowedLayerReferences.ContainsKey(project.Layer)
        from reference in project.ProjectReferences
        let target = graph.Projects.GetValueOrDefault(reference)
        where target?.Module == project.Module && !AllowedLayerReferences[project.Layer!].Contains(target.Layer)
        select $"{project.Name} references {reference}: layer {project.Layer} may not depend on layer {target.Layer}.";

    public static IEnumerable<string> DomainAndContractsHaveNoInfrastructureDependencies(ProjectGraph graph) =>
        graph.Projects.Values
            .Where(p => p.Layer is "Domain" or "Contracts")
            .SelectMany(p =>
                p.PackageReferences
                    .Where(package => InfrastructurePackagePrefixes.Any(prefix => package.StartsWith(prefix, StringComparison.Ordinal)))
                    .Select(package => $"{p.Name} references package {package}: {p.Layer} must stay free of infrastructure.")
                    .Concat(p.FrameworkReferences.Select(framework => $"{p.Name} references framework {framework}: {p.Layer} must stay free of infrastructure."))
                    .Concat(p.ProjectReferences
                        .Where(reference => reference != SharedKernel && graph.Projects.GetValueOrDefault(reference)?.Module is null)
                        .Select(reference => $"{p.Name} references {reference}: {p.Layer} may only reference {SharedKernel} outside its module.")));

    public static IEnumerable<string> NothingReferencesAHost(ProjectGraph graph) =>
        from project in graph.Projects.Values
        where !project.Name.EndsWith("Tests", StringComparison.Ordinal)
        from reference in project.ProjectReferences
        where Hosts.Contains(reference)
        select $"{project.Name} references {reference}: hosts are composition roots and must not be referenced.";

    public static IEnumerable<string> KernelProjectsStayAtTheBottom(ProjectGraph graph)
    {
        if (graph.Projects.TryGetValue(SharedKernel, out var kernel))
        {
            foreach (var reference in kernel.ProjectReferences)
            {
                yield return $"{SharedKernel} references {reference}: the shared kernel must not depend on any project.";
            }
        }

        if (graph.Projects.TryGetValue(BuildingBlocks, out var blocks))
        {
            foreach (var reference in blocks.ProjectReferences.Where(r => r != SharedKernel))
            {
                yield return $"{BuildingBlocks} references {reference}: building blocks may only depend on {SharedKernel}.";
            }
        }
    }

    public static IEnumerable<string> EveryProjectIsInTheSolution(ProjectGraph graph) =>
        graph.Projects.Keys
            .Where(name => !graph.SolutionProjects.Contains(name))
            .Select(name => $"{name} is not in Karry.slnx: it would not be built or tested by CI.");

    public static IEnumerable<string> ModuleProjectsFollowTheNamingConvention(ProjectGraph graph) =>
        graph.Projects.Keys
            .Where(name => name.StartsWith("Karry.Modules.", StringComparison.Ordinal))
            .Where(name => graph.Projects[name].Layer is not { } layer || !AllowedLayerReferences.ContainsKey(layer))
            .Select(name => $"{name} does not follow Karry.Modules.{{Module}}.{{Contracts|Domain|Application|Infrastructure|Api|Tests}}.");
}
