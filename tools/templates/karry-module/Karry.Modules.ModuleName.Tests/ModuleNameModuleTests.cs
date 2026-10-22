using Karry.BuildingBlocks.Modules;
using Karry.Modules.ModuleName.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.ModuleName.Tests;

public sealed class ModuleNameModuleTests
{
    [Fact]
    public void Module_name_is_a_lowercase_route_segment()
    {
        var module = new ModuleNameModule();

        module.Name.Should().MatchRegex("^[a-z][a-z0-9-]*$");
    }

    [Fact]
    public void Module_registers_with_the_host_contract()
    {
        var services = new ServiceCollection();

        services.AddModules(new ConfigurationBuilder().Build(), [new ModuleNameModule()]);

        services.BuildServiceProvider().GetRequiredService<IReadOnlyList<IModule>>().Should().ContainSingle();
    }
}
