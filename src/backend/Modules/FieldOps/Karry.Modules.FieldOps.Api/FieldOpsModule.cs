using Karry.BuildingBlocks.Modules;
using Karry.Modules.FieldOps.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.FieldOps.Api;

/// <summary>
/// Field operations (ADR-0034): shift entries arrive as signed sync commands from the tablets and are stored as
/// append-only versions. Routes under <c>/api/v1/fieldops</c> come with the portal's read API.
/// </summary>
public sealed class FieldOpsModule : IModule
{
    public string Name => "fieldops";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString("Karry");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Host without a database (some host tests).
            return;
        }

        services.AddFieldOpsInfrastructure(connectionString);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // The portal's read API (P2.4 slice 4) maps here; every endpoint needs authz, isolation and contract tests.
    }
}
