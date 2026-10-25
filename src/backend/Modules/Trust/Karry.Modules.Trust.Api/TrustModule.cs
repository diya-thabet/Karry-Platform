using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Entitlements;
using Karry.BuildingBlocks.Modules;
using Karry.Modules.Trust.Application;
using Karry.Modules.Trust.Infrastructure;
using Karry.SharedKernel.Authorization;
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
    /// <summary>Run the integrity check of the tenant's ledger (role matrix row "Audit log / integrity check").</summary>
    public const string LedgerVerify = "audit.ledger.verify";

    public string Name => "audit";

    // Site kind: a controller or site manager of one site may run it; the report holds no site data, only integrity.
    public IReadOnlyList<PermissionDefinition> Permissions =>
    [
        new(LedgerVerify, ScopeKind.Site, privileged: false,
            DefaultRoles.TenantAdmin, DefaultRoles.Executive, DefaultRoles.Finance, DefaultRoles.Controller, DefaultRoles.SiteManager),
    ];

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString("Karry");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return; // host without a database (some host tests): endpoints still map ([FromServices])
        }

        services.AddTrustInfrastructure(connectionString);
        services.AddTrustAnchoring(configuration); // the verify endpoint also compares the chain with its WORM anchors
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Integrity check of the caller's tenant ledger (US-P1-AUD-03).
        endpoints.MapPost("/verify", VerifyAsync)
            .ExportAccess()
            .RequirePermission(LedgerVerify)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithName("VerifyLedger");
    }

    private static async Task<Ok<LedgerVerificationReport>> VerifyAsync([FromServices] VerifyLedgerHandler handler, CancellationToken cancellationToken) =>
        TypedResults.Ok(await handler.HandleAsync(cancellationToken).ConfigureAwait(false));
}
