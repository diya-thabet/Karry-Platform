using Karry.BuildingBlocks.Modules;
using Karry.Modules.Identity.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Identity.Tests;

public sealed class IdentityModuleTests
{
    [Fact]
    public void Module_name_is_a_lowercase_route_segment()
    {
        var module = new IdentityModule();

        module.Name.Should().MatchRegex("^[a-z][a-z0-9-]*$");
    }

    [Fact]
    public void Module_registers_with_the_host_contract()
    {
        var services = new ServiceCollection();

        services.AddModules(new ConfigurationBuilder().Build(), [new IdentityModule()]);

        services.BuildServiceProvider().GetRequiredService<IReadOnlyList<IModule>>().Should().ContainSingle();
    }
}
