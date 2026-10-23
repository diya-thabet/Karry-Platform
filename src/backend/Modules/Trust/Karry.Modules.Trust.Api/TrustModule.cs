using Karry.BuildingBlocks.Modules;
using Karry.Modules.Trust.Application;
using Karry.Modules.Trust.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
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
            return; // host without a database (some host tests): endpoints still map ([FromServices])
        }

        services.AddTrustInfrastructure(connectionString);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Integrity check of the caller's tenant ledger (US-P1-AUD-03). Restricted to admins/auditors with roles (P1.9).
        endpoints.MapPost("/verify", VerifyAsync)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithName("VerifyLedger");
    }

    private static async Task<Ok<LedgerVerificationReport>> VerifyAsync([FromServices] VerifyLedgerHandler handler, CancellationToken cancellationToken) =>
        TypedResults.Ok(await handler.HandleAsync(cancellationToken).ConfigureAwait(false));
}
