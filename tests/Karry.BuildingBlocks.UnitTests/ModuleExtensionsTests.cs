using System.Net;
using Karry.BuildingBlocks.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.BuildingBlocks.UnitTests;

[Trait("Story", "US-P1-PLT-01")]
[Trait("TestCase", "TC-P1-PLT-013")]
public sealed class ModuleExtensionsTests
{
    [Fact]
    public void Every_module_registers_its_services()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddModules(configuration, [new FakeModule("alpha"), new FakeModule("beta")]);

        services.Should().Contain(d => d.ServiceType == typeof(Marker) && (string?)d.ServiceKey == "alpha");
        services.Should().Contain(d => d.ServiceType == typeof(Marker) && (string?)d.ServiceKey == "beta");
    }

    [Fact]
    public void Two_modules_with_the_same_name_are_rejected()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        var act = () => services.AddModules(configuration, [new FakeModule("alpha"), new FakeModule("alpha")]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*'alpha'*more than once*");
    }

    [Fact]
    public async Task Module_endpoints_are_mapped_under_api_v1_and_the_module_name()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddModules(builder.Configuration, [new FakeModule("alpha")]);
        await using var app = builder.Build();
        app.MapModules();
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = app.GetTestClient();

        var mapped = await client.GetAsync(new Uri("/api/v1/alpha/ping", UriKind.Relative), TestContext.Current.CancellationToken);
        var unprefixed = await client.GetAsync(new Uri("/ping", UriKind.Relative), TestContext.Current.CancellationToken);

        mapped.StatusCode.Should().Be(HttpStatusCode.OK);
        (await mapped.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be("alpha");
        unprefixed.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private sealed class Marker;

    private sealed class FakeModule(string name) : IModule
    {
        public string Name => name;

        public void AddServices(IServiceCollection services, IConfiguration configuration) =>
            services.AddKeyedSingleton<Marker>(name);

        public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
            endpoints.MapGet("/ping", () => Results.Text(name));
    }
}
