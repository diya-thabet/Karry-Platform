using Karry.BuildingBlocks.Modules;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.ModuleName.Api;

/// <summary>Entry point of the ModuleName module. Register it in <c>Karry.Host/ModuleCatalog.cs</c>.</summary>
public sealed class ModuleNameModule : IModule
{
    public string Name => "modulename";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        // Register handlers, repositories and the module's DbContext here.
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Endpoints are relative to /api/v1/modulename. Every endpoint needs authz, isolation and contract tests.
    }
}
