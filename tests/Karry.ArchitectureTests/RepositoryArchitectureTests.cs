namespace Karry.ArchitectureTests;

/// <summary>Applies the rules to the real repository. Fails the build on any boundary violation.</summary>
[Trait("Story", "US-P1-PLT-01")]
[Trait("TestCase", "TC-P1-PLT-001")]
public sealed class RepositoryArchitectureTests
{
    private static readonly ProjectGraph Repository = ProjectGraph.Load(ProjectGraph.FindRepositoryRoot());

    [Fact]
    public void The_repository_graph_is_loaded()
    {
        // Guards against a vacuous pass: the rules below must be looking at the real projects.
        Repository.Projects.Keys.Should().Contain(["Karry.SharedKernel", "Karry.BuildingBlocks", "Karry.Host", "Karry.Workers", "Karry.ArchitectureTests"]);
        Repository.Projects["Karry.Host"].ProjectReferences.Should().Contain("Karry.BuildingBlocks");
    }

    [Fact]
    public void The_repository_respects_every_architecture_rule()
    {
        ArchitectureRules.All(Repository).Should().BeEmpty();
    }
}
