using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Entitlements;
using Karry.BuildingBlocks.Modules;
using Karry.BuildingBlocks.Outbox;
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
using Microsoft.Extensions.Options;

namespace Karry.Modules.Trust.Api;

/// <summary>Audit ledger (ADR-0008): written by every module's unit of work; verification and anchoring. Routes under /api/v1/audit.</summary>
public sealed class TrustModule : IModule
{
    /// <summary>Run the integrity check of the tenant's ledger (role matrix row "Audit log / integrity check").</summary>
    public const string LedgerVerify = "audit.ledger.verify";

    /// <summary>Read the audit log (US-P1-AUD-04; role matrix row "Audit log / integrity check").</summary>
    public const string LedgerRead = AuditLogQueries.ReadPermission;

    public string Name => "audit";

    // Site kind: a controller or site manager of one site may run it; the report holds no site data, only integrity.
    public IReadOnlyList<PermissionDefinition> Permissions =>
    [
        new(LedgerVerify, ScopeKind.Site, privileged: false,
            DefaultRoles.TenantAdmin, DefaultRoles.Executive, DefaultRoles.Finance, DefaultRoles.Controller, DefaultRoles.SiteManager),

        // Site kind: on some sites, only the entries of those sites (ADR-0029 §1). Not privileged: read-only, masked.
        new(LedgerRead, ScopeKind.Site, privileged: false,
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
        // Integrity check of the caller's tenant ledger (US-P1-AUD-03); each result is kept (AC2).
        endpoints.MapPost("/verify", VerifyAsync)
            .ExportAccess()
            .RequirePermission(LedgerVerify)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithName("VerifyLedger");

        // The last integrity check, for the portal's "Integrity check" screen (US-P1-AUD-03 AC2).
        endpoints.MapGet("/verifications/latest", LatestVerificationAsync)
            .ExportAccess()
            .RequirePermission(LedgerVerify)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithName("GetLatestLedgerVerification");

        // The audit log, newest first (US-P1-AUD-04). Readable whatever the subscription state, like exports.
        endpoints.MapGet("/entries", SearchEntriesAsync)
            .ExportAccess()
            .RequirePermission(LedgerRead)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithName("SearchAuditEntries");

        // The same entries as CSV for Excel (US-P1-AUD-04 AC2): same filters, reach and masking; the export is ledgered.
        endpoints.MapGet("/entries/export.csv", ExportEntriesAsync)
            .ExportAccess()
            .RequirePermission(LedgerRead)
            .Produces(StatusCodes.Status200OK, contentType: "text/csv")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithName("ExportAuditEntries");
    }

    private static async Task<Results<Ok<AuditEntryListResponse>, ProblemHttpResult>> SearchEntriesAsync(
        [FromServices] AuditLogQueries audit, [FromServices] EventSerializer events, [FromServices] IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json,
        CancellationToken cancellationToken, string? actor = null, string? entityType = null, string? entityId = null, Guid? siteId = null,
        string? eventType = null, DateTimeOffset? from = null, DateTimeOffset? to = null, string? cursor = null, int? limit = null)
    {
        var result = await audit.SearchAsync(new AuditSearch(actor, entityType, entityId, siteId, eventType, from, to, cursor, limit), cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? TypedResults.Ok(new AuditEntryListResponse([.. result.Value.Entries.Select(e => AuditEntryResponse.From(e, events, json.Value.SerializerOptions))], result.Value.NextCursor))
            : result.Error.ToProblem();
    }

    private static async Task<Results<PushStreamHttpResult, ProblemHttpResult>> ExportEntriesAsync(
        [FromServices] AuditExports exports, [FromServices] EventSerializer events, [FromServices] IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json,
        [FromServices] TimeProvider clock, CancellationToken cancellationToken, string? actor = null, string? entityType = null, string? entityId = null,
        Guid? siteId = null, string? eventType = null, DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        var run = await exports.StartAsync(new AuditSearch(actor, entityType, entityId, siteId, eventType, from, to, null, null), cancellationToken)
            .ConfigureAwait(false);
        if (!run.IsSuccess)
        {
            return run.Error.ToProblem();
        }

        var options = json.Value.SerializerOptions;
        var fileName = $"karry-audit-{clock.GetUtcNow():yyyyMMdd-HHmm}.csv";
        return TypedResults.Stream(async body =>
        {
            await using var writer = new StreamWriter(body, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true), leaveOpen: true);
            await writer.WriteAsync(AuditCsv.Header).ConfigureAwait(false);
            await foreach (var entry in run.Value.Entries.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                var shown = AuditEntryResponse.From(entry, events, options); // masked exactly as in the JSON API
                await writer.WriteAsync(AuditCsv.Row(entry, shown.Changes, shown.Event?.ToJsonString())).ConfigureAwait(false);
            }
        }, AuditCsv.ContentType, fileName);
    }

    private static async Task<Ok<LedgerVerificationReport>> VerifyAsync([FromServices] IntegrityChecks checks, CancellationToken cancellationToken) =>
        TypedResults.Ok(await checks.RunAsync(cancellationToken).ConfigureAwait(false));

    private static async Task<Ok<LatestVerificationResponse>> LatestVerificationAsync([FromServices] IntegrityChecks checks, CancellationToken cancellationToken)
    {
        var latest = await checks.LatestAsync(cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new LatestVerificationResponse(latest?.Report, latest?.VerifiedBy));
    }
}
