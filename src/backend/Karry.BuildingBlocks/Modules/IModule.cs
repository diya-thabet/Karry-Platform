using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.BuildingBlocks.Modules;

/// <summary>
/// Entry point of a bounded-context module. Each module's <c>Api</c> project implements it once;
/// the host registers modules explicitly (no assembly scanning), so the module list is reviewable.
/// </summary>
public interface IModule
{
    /// <summary>Stable, lowercase module name used in routes (<c>/api/v1/{name}</c>), telemetry and logs.</summary>
    string Name { get; }

    void AddServices(IServiceCollection services, IConfiguration configuration);

    void MapEndpoints(IEndpointRouteBuilder endpoints);
}
