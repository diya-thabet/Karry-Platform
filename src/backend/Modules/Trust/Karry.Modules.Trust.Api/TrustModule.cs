using Karry.BuildingBlocks.Modules;
using Karry.Modules.Trust.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Trust.Api;

/// <summary>Audit ledger (ADR-0008): written by every module's unit of work; verification and anchoring. Routes under /api/v1/audit.</summary>
public sealed class TrustModule : IModule
{
    public string Name => "audit";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString("Karry");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return; // host without a database (some host tests)
        }

        services.AddTrustInfrastructure(connectionString);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Verification endpoint: P1.6 slice 3.
    }
}
