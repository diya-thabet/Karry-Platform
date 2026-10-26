using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Idempotency;
using Karry.BuildingBlocks.Modules;
using Karry.Modules.Organization.Application;
using Karry.Modules.Organization.Domain;
using Karry.Modules.Organization.Infrastructure;
using Karry.SharedKernel.Authorization;
using Karry.SharedKernel.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Organization.Api;

/// <summary>
/// The company's structure (US-P1-ORG-01, ADR-0024): sites with their map pin and boundary. Routes under
/// <c>/api/v1/organization</c>.
/// </summary>
public sealed class OrganizationModule : IModule
{
    public string Name => "organization";

    public IReadOnlyList<PermissionDefinition> Permissions => OrganizationPermissions.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString("Karry");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Host without a database (some host tests): endpoints still map ([FromServices]) and fail only when called.
            return;
        }

        services.AddOrganizationInfrastructure(connectionString);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // The sites the caller may see (a site role: only its sites); archived ones on request.
        endpoints.MapGet("/sites", ListSitesAsync)
            .RequirePermission(OrganizationPermissions.SitesRead)
            .WithName("ListSites");

        endpoints.MapGet("/sites/{siteId:guid}", GetSiteAsync)
            .RequirePermission(OrganizationPermissions.SitesRead)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetSite");

        // A new site counts against the sites bought (soft limit, ADR-0023/0024).
        endpoints.MapPost("/sites", CreateSiteAsync)
            .RequirePermission(OrganizationPermissions.SitesManage)
            .RequireIdempotencyKey()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("CreateSite");

        endpoints.MapPut("/sites/{siteId:guid}", ChangeSiteAsync)
            .RequirePermission(OrganizationPermissions.SitesManage)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("ChangeSite");

        endpoints.MapPost("/sites/{siteId:guid}/archive", ArchiveSiteAsync)
            .RequirePermission(OrganizationPermissions.SitesManage)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("ArchiveSite");

        endpoints.MapPost("/sites/{siteId:guid}/restore", RestoreSiteAsync)
            .RequirePermission(OrganizationPermissions.SitesManage)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("RestoreSite");
    }

    private static async Task<Ok<SiteListResponse>> ListSitesAsync(
        [FromServices] SiteOperations sites, CancellationToken cancellationToken, bool includeArchived = false)
    {
        var items = await sites.ListAsync(includeArchived, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new SiteListResponse([.. items.Select(SiteResponse.From)]));
    }

    private static async Task<Results<Ok<SiteResponse>, ProblemHttpResult>> GetSiteAsync(
        Guid siteId, [FromServices] SiteOperations sites, CancellationToken cancellationToken) =>
        Respond(await sites.GetAsync(new SiteId(siteId), cancellationToken).ConfigureAwait(false));

    private static async Task<Results<Created<SiteResponse>, ProblemHttpResult>> CreateSiteAsync(
        SiteInput request, [FromServices] SiteOperations sites, CancellationToken cancellationToken)
    {
        var result = await sites.CreateAsync(request, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? TypedResults.Created($"/api/v1/organization/sites/{result.Value.Id}", SiteResponse.From(result.Value))
            : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<SiteResponse>, ProblemHttpResult>> ChangeSiteAsync(
        Guid siteId, SiteInput request, [FromServices] SiteOperations sites, CancellationToken cancellationToken) =>
        Respond(await sites.ChangeAsync(new SiteId(siteId), request, cancellationToken).ConfigureAwait(false));

    private static async Task<Results<Ok<SiteResponse>, ProblemHttpResult>> ArchiveSiteAsync(
        Guid siteId, [FromServices] SiteOperations sites, CancellationToken cancellationToken) =>
        Respond(await sites.ArchiveAsync(new SiteId(siteId), cancellationToken).ConfigureAwait(false));

    private static async Task<Results<Ok<SiteResponse>, ProblemHttpResult>> RestoreSiteAsync(
        Guid siteId, [FromServices] SiteOperations sites, CancellationToken cancellationToken) =>
        Respond(await sites.RestoreAsync(new SiteId(siteId), cancellationToken).ConfigureAwait(false));

    private static Results<Ok<SiteResponse>, ProblemHttpResult> Respond(Result<Site> result) =>
        result.IsSuccess ? TypedResults.Ok(SiteResponse.From(result.Value)) : result.Error.ToProblem();
}

public sealed record SiteListResponse(IReadOnlyList<SiteResponse> Items);

/// <summary>A site; <c>Boundary</c> is a GeoJSON polygon ([longitude, latitude]) or null.</summary>
public sealed record SiteResponse(
    Guid Id, string Code, string Name, string Type, AddressResponse Address, LocationResponse Location, PolygonResponse? Boundary,
    string TimeZone, string? Currency, string Status, DateTimeOffset CreatedAt, DateTimeOffset ChangedAt, DateTimeOffset? ArchivedAt)
{
    public static SiteResponse From(Site site)
    {
        ArgumentNullException.ThrowIfNull(site);
        return new(site.Id.Value, site.Code, site.Name, SiteRules.TypeCode(site.Type),
            new AddressResponse(site.AddressLine, site.City, site.Region, site.PostalCode, site.Country),
            new LocationResponse(site.Location.Y, site.Location.X),
            site.Boundary is { } boundary ? new PolygonResponse("Polygon", [[.. boundary.Shell.Coordinates.Select(c => (IReadOnlyList<double>)[c.X, c.Y])]]) : null,
            site.TimeZone, site.Currency, site.Status.ToString().ToLowerInvariant(), site.CreatedAt, site.ChangedAt, site.ArchivedAt);
    }
}

public sealed record AddressResponse(string Line, string City, string? Region, string? PostalCode, string Country);

public sealed record LocationResponse(double Latitude, double Longitude);

public sealed record PolygonResponse(string Type, IReadOnlyList<IReadOnlyList<IReadOnlyList<double>>> Coordinates);
