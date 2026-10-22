using Karry.BuildingBlocks.Modules;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Fuel.Api;

/// <summary>Entry point of the Fuel module. Register it in <c>Karry.Host/ModuleCatalog.cs</c>.</summary>
public sealed class FuelModule : IModule
{
    public string Name => "fuel";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        // Register handlers, repositories and the module's DbContext here.
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Endpoints are relative to /api/v1/fuel. Every endpoint needs authz, isolation and contract tests.
    }
}
