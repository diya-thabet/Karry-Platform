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
/// The company's structure (US-P1-ORG-01/02, ADR-0024): sites with their map pin and boundary, their shift pattern
/// versions, non-working days and shift instances. Routes under <c>/api/v1/organization</c>.
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
            .ProducesProblem(StatusCodes.Status409Conflict)
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

        // Shift patterns (US-P1-ORG-02 AC1): versions by the local date they start on; only future ones change.
        endpoints.MapGet("/sites/{siteId:guid}/shift-patterns", ListShiftPatternsAsync)
            .RequirePermission(OrganizationPermissions.SitesRead)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("ListShiftPatterns");

        endpoints.MapPut("/sites/{siteId:guid}/shift-patterns/{effectiveFrom}", SetShiftPatternAsync)
            .RequirePermission(OrganizationPermissions.ShiftsManage)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("SetShiftPattern");

        endpoints.MapDelete("/sites/{siteId:guid}/shift-patterns/{effectiveFrom}", RemoveShiftPatternAsync)
            .RequirePermission(OrganizationPermissions.ShiftsManage)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("RemoveShiftPattern");

        // Non-working days (US-P1-ORG-02 AC2): public holidays, closures; no shift starts on them. Listed by range
        // (by default the year from the site's today).
        endpoints.MapGet("/sites/{siteId:guid}/non-working-days", ListNonWorkingDaysAsync)
            .RequirePermission(OrganizationPermissions.SitesRead)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("ListNonWorkingDays");

        endpoints.MapPut("/sites/{siteId:guid}/non-working-days/{date}", SetNonWorkingDayAsync)
            .RequirePermission(OrganizationPermissions.ShiftsManage)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("SetNonWorkingDay");

        endpoints.MapDelete("/sites/{siteId:guid}/non-working-days/{date}", RemoveNonWorkingDayAsync)
            .RequirePermission(OrganizationPermissions.ShiftsManage)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("RemoveNonWorkingDay");

        // Shift instances of any range up to a year, computed from the rules (US-P1-ORG-02 AC3); by default the next
        // 30 days from the site's today.
        endpoints.MapGet("/sites/{siteId:guid}/shift-instances", ComputeShiftInstancesAsync)
            .RequirePermission(OrganizationPermissions.SitesRead)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("ComputeShiftInstances");
    }

    private static async Task<Results<Ok<ShiftPatternListResponse>, ProblemHttpResult>> ListShiftPatternsAsync(
        Guid siteId, [FromServices] ShiftOperations shifts, CancellationToken cancellationToken)
    {
        var result = await shifts.PatternsAsync(new SiteId(siteId), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.Ok(new ShiftPatternListResponse([.. result.Value.Select(ShiftPatternResponse.From)])) : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<ShiftPatternResponse>, ProblemHttpResult>> SetShiftPatternAsync(
        Guid siteId, DateOnly effectiveFrom, ShiftPatternInput request, [FromServices] ShiftOperations shifts, CancellationToken cancellationToken)
    {
        var result = await shifts.SetPatternAsync(new SiteId(siteId), effectiveFrom, request, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.Ok(ShiftPatternResponse.From(result.Value)) : result.Error.ToProblem();
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> RemoveShiftPatternAsync(
        Guid siteId, DateOnly effectiveFrom, [FromServices] ShiftOperations shifts, CancellationToken cancellationToken)
    {
        var result = await shifts.RemovePatternAsync(new SiteId(siteId), effectiveFrom, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<NonWorkingDayListResponse>, ProblemHttpResult>> ListNonWorkingDaysAsync(
        Guid siteId, [FromServices] ShiftOperations shifts, CancellationToken cancellationToken, DateOnly? from = null, DateOnly? to = null)
    {
        var result = await shifts.NonWorkingDaysAsync(new SiteId(siteId), from, to, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.Ok(new NonWorkingDayListResponse([.. result.Value.Select(NonWorkingDayResponse.From)])) : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<NonWorkingDayResponse>, ProblemHttpResult>> SetNonWorkingDayAsync(
        Guid siteId, DateOnly date, NonWorkingDayInput request, [FromServices] ShiftOperations shifts, CancellationToken cancellationToken)
    {
        var result = await shifts.SetNonWorkingDayAsync(new SiteId(siteId), date, request, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.Ok(NonWorkingDayResponse.From(result.Value)) : result.Error.ToProblem();
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> RemoveNonWorkingDayAsync(
        Guid siteId, DateOnly date, [FromServices] ShiftOperations shifts, CancellationToken cancellationToken)
    {
        var result = await shifts.RemoveNonWorkingDayAsync(new SiteId(siteId), date, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<ShiftInstanceListResponse>, ProblemHttpResult>> ComputeShiftInstancesAsync(
        Guid siteId, [FromServices] ShiftOperations shifts, CancellationToken cancellationToken, DateOnly? from = null, DateOnly? to = null)
    {
        var result = await shifts.ComputeAsync(new SiteId(siteId), from, to, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.Ok(new ShiftInstanceListResponse([.. result.Value.Select(ShiftInstanceResponse.From)])) : result.Error.ToProblem();
    }

    private static async Task<Ok<SiteListResponse>> ListSitesAsync(
        [FromServices] SiteOperations sites, CancellationToken cancellationToken, bool includeArchived = false)
    {
        var items = await sites.ListAsync(includeArchived, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new SiteListResponse([.. items.Select(SiteResponse.From)]));
    }

    private static async Task<Results<Ok<SiteResponse>, ProblemHttpResult>> GetSiteAsync(
        Guid siteId, HttpResponse response, [FromServices] SiteOperations sites, CancellationToken cancellationToken) =>
        Respond(await sites.GetAsync(new SiteId(siteId), cancellationToken).ConfigureAwait(false), response);

    private static async Task<Results<Created<SiteResponse>, ProblemHttpResult>> CreateSiteAsync(
        SiteInput request, [FromServices] SiteOperations sites, CancellationToken cancellationToken)
    {
        var result = await sites.CreateAsync(request, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? TypedResults.Created($"/api/v1/organization/sites/{result.Value.Id}", SiteResponse.From(result.Value))
            : result.Error.ToProblem();
    }

    /// <summary><c>If-Match</c>: the <c>ETag</c> (or <c>version</c>) the form was filled from; a newer site is a 409 (H-C1).</summary>
    private static async Task<Results<Ok<SiteResponse>, ProblemHttpResult>> ChangeSiteAsync(
        Guid siteId, SiteInput request, [FromHeader(Name = "If-Match")] string? ifMatch, HttpResponse response, [FromServices] SiteOperations sites,
        CancellationToken cancellationToken)
    {
        var expected = RecordVersionHttp.Expected(ifMatch);
        return expected.IsSuccess
            ? Respond(await sites.ChangeAsync(new SiteId(siteId), request, cancellationToken, expected.Value).ConfigureAwait(false), response)
            : expected.Error.ToProblem();
    }

    private static async Task<Results<Ok<SiteResponse>, ProblemHttpResult>> ArchiveSiteAsync(
        Guid siteId, HttpResponse response, [FromServices] SiteOperations sites, CancellationToken cancellationToken) =>
        Respond(await sites.ArchiveAsync(new SiteId(siteId), cancellationToken).ConfigureAwait(false), response);

    private static async Task<Results<Ok<SiteResponse>, ProblemHttpResult>> RestoreSiteAsync(
        Guid siteId, HttpResponse response, [FromServices] SiteOperations sites, CancellationToken cancellationToken) =>
        Respond(await sites.RestoreAsync(new SiteId(siteId), cancellationToken).ConfigureAwait(false), response);

    private static Results<Ok<SiteResponse>, ProblemHttpResult> Respond(Result<Site> result, HttpResponse response)
    {
        if (!result.IsSuccess)
        {
            return result.Error.ToProblem();
        }

        response.SetETag(result.Value.Version);
        return TypedResults.Ok(SiteResponse.From(result.Value));
    }
}

public sealed record SiteListResponse(IReadOnlyList<SiteResponse> Items);

/// <summary>A site; <c>Boundary</c> is a GeoJSON polygon ([longitude, latitude]) or null.</summary>
public sealed record SiteResponse(
    Guid Id, string Code, string Name, string Type, AddressResponse Address, LocationResponse Location, PolygonResponse? Boundary,
    string TimeZone, string? Currency, string Status, DateTimeOffset CreatedAt, DateTimeOffset ChangedAt, DateTimeOffset? ArchivedAt, string Version)
{
    public static SiteResponse From(Site site)
    {
        ArgumentNullException.ThrowIfNull(site);
        return new(site.Id.Value, site.Code, site.Name, SiteRules.TypeCode(site.Type),
            new AddressResponse(site.AddressLine, site.City, site.Region, site.PostalCode, site.Country),
            new LocationResponse(site.Location.Y, site.Location.X),
            site.Boundary is { } boundary ? new PolygonResponse("Polygon", [[.. boundary.Shell.Coordinates.Select(c => (IReadOnlyList<double>)[c.X, c.Y])]]) : null,
            site.TimeZone, site.Currency, site.Status.ToString().ToLowerInvariant(), site.CreatedAt, site.ChangedAt, site.ArchivedAt, RecordVersionHttp.ETag(site.Version));
    }
}

public sealed record AddressResponse(string Line, string City, string? Region, string? PostalCode, string Country);

public sealed record LocationResponse(double Latitude, double Longitude);

public sealed record PolygonResponse(string Type, IReadOnlyList<IReadOnlyList<IReadOnlyList<double>>> Coordinates);

public sealed record ShiftPatternListResponse(IReadOnlyList<ShiftPatternResponse> Items);

/// <summary>A pattern version: its shifts from <c>EffectiveFrom</c> (a local date of the site) until the next version.</summary>
public sealed record ShiftPatternResponse(Guid Id, DateOnly EffectiveFrom, IReadOnlyList<ShiftResponse> Shifts, DateTimeOffset ChangedAt)
{
    public static ShiftPatternResponse From(ShiftPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return new(pattern.Id.Value, pattern.EffectiveFrom,
            [.. ShiftRules.Snapshot(pattern.Shifts).Select(s => new ShiftResponse(s.Code, s.Name, s.Start, s.End, s.Days))], pattern.ChangedAt);
    }
}

/// <summary>A shift: "HH:mm" on the site's clock (an end not after the start is the next day), days "mon".."sun".</summary>
public sealed record ShiftResponse(string Code, string Name, string Start, string End, IReadOnlyList<string> Days);

public sealed record NonWorkingDayListResponse(IReadOnlyList<NonWorkingDayResponse> Items);

/// <summary>A non-working day; <c>Kind</c> is "public-holiday", "closure" or "other".</summary>
public sealed record NonWorkingDayResponse(DateOnly Date, string Kind, string Name)
{
    public static NonWorkingDayResponse From(NonWorkingDay day)
    {
        ArgumentNullException.ThrowIfNull(day);
        return new(day.Date, ShiftRules.KindCode(day.Kind), day.Name);
    }
}

public sealed record ShiftInstanceListResponse(IReadOnlyList<ShiftInstanceResponse> Items);

/// <summary>A shift on a date (the local date it starts on), as UTC instants; its real length follows clock changes.</summary>
public sealed record ShiftInstanceResponse(DateOnly Date, string Code, string Name, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string TimeZone, int LengthMinutes)
{
    public static ShiftInstanceResponse From(ComputedShift shift)
    {
        ArgumentNullException.ThrowIfNull(shift);
        return new(shift.Date, shift.Code, shift.Name, shift.StartsAt, shift.EndsAt, shift.TimeZone, (int)shift.Length.TotalMinutes);
    }
}
