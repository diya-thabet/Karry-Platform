using Karry.SharedKernel.Authorization;
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

    /// <summary>The permissions this module's endpoints require (codes start with <see cref="Name"/>; ADR-0022).</summary>
    IReadOnlyList<PermissionDefinition> Permissions => [];

    /// <summary>The configurable sensitive fields of this module's API objects (US-P1-IAM-07).</summary>
    IReadOnlyList<SensitiveFieldDefinition> SensitiveFields => [];
}
