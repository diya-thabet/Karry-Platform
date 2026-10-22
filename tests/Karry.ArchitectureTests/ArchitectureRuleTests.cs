namespace Karry.ArchitectureTests;

/// <summary>
/// Proves each rule catches the violation it exists for (and does not flag the legitimate shape),
/// so a green run on the real repository means something.
/// </summary>
[Trait("Story", "US-P1-PLT-01")]
public sealed class ArchitectureRuleTests
{
    [Fact]
    [Trait("TestCase", "TC-P1-PLT-001")]
    public void A_clean_modular_graph_has_no_violations()
    {
        ArchitectureRules.All(GraphBuilder.Clean().Build()).Should().BeEmpty();
    }

    [Theory]
    [Trait("TestCase", "TC-P1-PLT-001")]
    [InlineData("Karry.Modules.Fuel.Application", "Karry.Modules.Assets.Domain")]
    [InlineData("Karry.Modules.Fuel.Application", "Karry.Modules.Assets.Application")]
    [InlineData("Karry.Modules.Fuel.Infrastructure", "Karry.Modules.Assets.Infrastructure")]
    [InlineData("Karry.Modules.Fuel.Api", "Karry.Modules.Assets.Api")]
    public void A_module_reaching_into_another_modules_internals_is_a_violation(string from, string to)
    {
        var graph = GraphBuilder.Clean().AddReference(from, to).Build();

        ArchitectureRules.ModulesReferenceOtherModulesOnlyThroughContracts(graph)
            .Should().ContainSingle().Which.Should().Contain(from).And.Contain(to).And.Contain("Contracts");
    }

    [Theory]
    [Trait("TestCase", "TC-P1-PLT-014")]
    [InlineData("Karry.Modules.Fuel.Domain", "Karry.Modules.Fuel.Application")]
    [InlineData("Karry.Modules.Fuel.Domain", "Karry.Modules.Fuel.Infrastructure")]
    [InlineData("Karry.Modules.Fuel.Application", "Karry.Modules.Fuel.Infrastructure")]
    [InlineData("Karry.Modules.Fuel.Application", "Karry.Modules.Fuel.Api")]
    [InlineData("Karry.Modules.Fuel.Contracts", "Karry.Modules.Fuel.Domain")]
    public void A_layer_depending_on_a_layer_above_it_is_a_violation(string from, string to)
    {
        var graph = GraphBuilder.Clean().AddReference(from, to).Build();

        ArchitectureRules.LayersReferenceOnlyAllowedLayers(graph).Should().ContainSingle().Which.Should().Contain(from).And.Contain(to);
    }

    [Theory]
    [Trait("TestCase", "TC-P1-PLT-014")]
    [InlineData("Karry.Modules.Fuel.Domain", "Microsoft.EntityFrameworkCore")]
    [InlineData("Karry.Modules.Fuel.Domain", "Npgsql")]
    [InlineData("Karry.Modules.Fuel.Contracts", "Microsoft.AspNetCore.Http.Abstractions")]
    public void Domain_or_contracts_taking_an_infrastructure_package_is_a_violation(string project, string package)
    {
        var graph = GraphBuilder.Clean().AddPackage(project, package).Build();

        ArchitectureRules.DomainAndContractsHaveNoInfrastructureDependencies(graph).Should().ContainSingle().Which.Should().Contain(package);
    }

    [Fact]
    [Trait("TestCase", "TC-P1-PLT-014")]
    public void Domain_using_the_aspnetcore_framework_or_building_blocks_is_a_violation()
    {
        var graph = GraphBuilder.Clean()
            .AddFramework("Karry.Modules.Fuel.Domain", "Microsoft.AspNetCore.App")
            .AddReference("Karry.Modules.Assets.Domain", "Karry.BuildingBlocks")
            .Build();

        ArchitectureRules.DomainAndContractsHaveNoInfrastructureDependencies(graph).Should().HaveCount(2);
    }

    [Fact]
    [Trait("TestCase", "TC-P1-PLT-014")]
    public void Referencing_a_host_is_a_violation_but_tests_may_host_it()
    {
        var graph = GraphBuilder.Clean()
            .AddReference("Karry.Modules.Fuel.Api", "Karry.Host")
            .Project("Karry.Host.IntegrationTests", references: ["Karry.Host"])
            .Build();

        ArchitectureRules.NothingReferencesAHost(graph).Should().ContainSingle().Which.Should().StartWith("Karry.Modules.Fuel.Api");
    }

    [Fact]
    [Trait("TestCase", "TC-P1-PLT-014")]
    public void Kernel_projects_depending_upwards_are_violations()
    {
        var graph = GraphBuilder.Clean()
            .AddReference("Karry.SharedKernel", "Karry.BuildingBlocks")
            .AddReference("Karry.BuildingBlocks", "Karry.Modules.Fuel.Contracts")
            .Build();

        ArchitectureRules.KernelProjectsStayAtTheBottom(graph).Should().HaveCount(2);
    }

    [Fact]
    [Trait("TestCase", "TC-P1-PLT-014")]
    public void A_project_missing_from_the_solution_is_a_violation()
    {
        var graph = GraphBuilder.Clean().Build(notInSolution: ["Karry.Modules.Fuel.Tests"]);

        ArchitectureRules.EveryProjectIsInTheSolution(graph).Should().ContainSingle().Which.Should().Contain("Karry.Modules.Fuel.Tests");
    }

    [Theory]
    [Trait("TestCase", "TC-P1-PLT-014")]
    [InlineData("Karry.Modules.Fuel.Helpers")]
    [InlineData("Karry.Modules.Fuel")]
    [InlineData("Karry.Modules.Fuel.Api.Internal")]
    public void Module_projects_with_unknown_layer_names_are_violations(string name)
    {
        var graph = GraphBuilder.Clean().Project(name).Build();

        ArchitectureRules.ModuleProjectsFollowTheNamingConvention(graph).Should().ContainSingle().Which.Should().Contain(name);
    }
}
