using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Modules;
using Karry.Modules.FieldOps.Application;
using Karry.Modules.FieldOps.Domain;
using Karry.Modules.FieldOps.Infrastructure;
using Karry.SharedKernel.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.FieldOps.Api;

/// <summary>
/// Field operations (ADR-0034): shift entries arrive as signed sync commands from the tablets and are stored as
/// append-only versions; the portal reads them here. Routes under <c>/api/v1/fieldops</c>.
/// </summary>
public sealed class FieldOpsModule : IModule
{
    public string Name => "fieldops";

    public IReadOnlyList<PermissionDefinition> Permissions => FieldOpsPermissions.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString("Karry");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Host without a database (some host tests): endpoints still map ([FromServices]) and fail only when called.
            return;
        }

        services.AddFieldOpsInfrastructure(connectionString);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/entries", SearchEntriesAsync)
            .RequirePermission(FieldOpsPermissions.EntriesRead)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithName("SearchShiftEntries");

        endpoints.MapGet("/entries/{entryId:guid}", GetEntryAsync)
            .RequirePermission(FieldOpsPermissions.EntriesRead)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetShiftEntry");

        endpoints.MapGet("/checklists", ListChecklistsAsync)
            .RequirePermission(FieldOpsPermissions.ChecklistsRead)
            .WithName("ListChecklists");

        endpoints.MapGet("/checklists/{assetType}/versions", ListChecklistVersionsAsync)
            .RequirePermission(FieldOpsPermissions.ChecklistsRead)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("ListChecklistVersions");

        endpoints.MapPost("/checklists/{assetType}/versions", PublishChecklistAsync)
            .RequirePermission(FieldOpsPermissions.ChecklistsManage)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("PublishChecklist");
    }

    private static async Task<Ok<ChecklistListResponse>> ListChecklistsAsync([FromServices] ChecklistTemplates checklists, CancellationToken cancellationToken) =>
        TypedResults.Ok(new ChecklistListResponse([.. (await checklists.LatestAsync(cancellationToken).ConfigureAwait(false)).Select(ChecklistResponse.From)]));

    private static async Task<Results<Ok<ChecklistListResponse>, ProblemHttpResult>> ListChecklistVersionsAsync(
        string assetType, [FromServices] ChecklistTemplates checklists, CancellationToken cancellationToken)
    {
        var result = await checklists.VersionsAsync(assetType, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.Ok(new ChecklistListResponse([.. result.Value.Select(ChecklistResponse.From)])) : result.Error.ToProblem();
    }

    private static async Task<Results<Created<ChecklistResponse>, ProblemHttpResult>> PublishChecklistAsync(
        string assetType, PublishChecklist body, [FromServices] ChecklistTemplates checklists, CancellationToken cancellationToken)
    {
        var result = await checklists.PublishAsync(assetType, body, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? TypedResults.Created($"/api/v1/fieldops/checklists/{result.Value.AssetType}/versions", ChecklistResponse.From(result.Value))
            : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<ShiftEntryListResponse>, ProblemHttpResult>> SearchEntriesAsync(
        [FromServices] EntryReading entries, CancellationToken cancellationToken,
        Guid? siteId = null, DateOnly? shiftDate = null, string? shiftCode = null, Guid? assetId = null, Guid? operatorId = null,
        string? status = null, bool? flagged = null, Guid? cursor = null, int? limit = null)
    {
        var result = await entries.SearchAsync(
            new EntrySearch(siteId, shiftDate, shiftCode, assetId, operatorId, status, flagged, cursor, limit), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? TypedResults.Ok(new ShiftEntryListResponse([.. result.Value.Items.Select(ShiftEntryResponse.From)], result.Value.NextCursor))
            : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<ShiftEntryDetailResponse>, ProblemHttpResult>> GetEntryAsync(
        Guid entryId, [FromServices] EntryReading entries, CancellationToken cancellationToken)
    {
        var result = await entries.GetAsync(entryId, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? TypedResults.Ok(new ShiftEntryDetailResponse(ShiftEntryResponse.From(result.Value[^1]), [.. result.Value.Select(ShiftEntryResponse.From)]))
            : result.Error.ToProblem();
    }
}

/// <summary>A page of shift entries (current versions), newest first; <c>nextCursor</c> asks for the next page.</summary>
public sealed record ShiftEntryListResponse(IReadOnlyList<ShiftEntryResponse> Items, Guid? NextCursor);

/// <summary>An entry as it stands (<c>entry</c>) and every version it went through, oldest first.</summary>
public sealed record ShiftEntryDetailResponse(ShiftEntryResponse Entry, IReadOnlyList<ShiftEntryResponse> Versions);

/// <summary>The shift an entry was resolved to when its start arrived (ADR-0034 §2).</summary>
public sealed record ShiftResponse(DateOnly Date, string Code, DateTimeOffset StartsAt, DateTimeOffset EndsAt);

/// <summary>Where the tablet was (WGS 84 degrees, accuracy in metres); null when location access was refused.</summary>
public sealed record LocationResponse(decimal Latitude, decimal Longitude, int AccuracyM)
{
    public static LocationResponse? From(Position? position) => position is null ? null : new(position.Latitude, position.Longitude, position.AccuracyM);
}

/// <summary>A stoppage during the segment.</summary>
public sealed record StoppageResponse(string Reason, int Minutes);

/// <summary>
/// One version of a shift entry. Meter values come with the meter's kind and unit (<c>h</c> or <c>km</c>), never bare
/// (ADR-0025); <c>usage</c> is end − start once the entry is closed.
/// </summary>
public sealed record ShiftEntryResponse(
    Guid Id, int Version, string Status, Guid SiteId, Guid AssetId, Guid MeterId, string MeterKind, string Unit, Guid OperatorId,
    int SegmentNo, Guid? PreviousEntryId, ShiftResponse? Shift, DateTimeOffset StartedAt, decimal StartValue, int FuelStartPercent,
    string StartPhoto, LocationResponse? StartLocation, DateTimeOffset? EndedAt, decimal? EndValue, decimal? Usage, int? FuelEndPercent, string? EndPhoto,
    LocationResponse? EndLocation,
    IReadOnlyList<StoppageResponse> Stoppages, string? Remarks, bool Estimated, IReadOnlyList<string> Flags, Guid EnvelopeId, Guid DeviceId,
    DateTimeOffset ReceivedAt)
{
    public static ShiftEntryResponse From(ShiftEntryVersion e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var shift = e is { ShiftDate: { } date, ShiftCode: { } code, ShiftStartsAt: { } starts, ShiftEndsAt: { } ends } ? new ShiftResponse(date, code, starts, ends) : null;
        return new ShiftEntryResponse(
            e.RootId, e.Version, EntryReading.StatusCode(e.Status), e.SiteId, e.AssetId, e.MeterId, e.MeterKind, e.MeterKind == "hours" ? "h" : "km", e.OperatorId,
            e.SegmentNo, e.PreviousRootId, shift, e.StartedAt, e.StartValue, e.FuelStartPercent,
            e.StartPhoto, LocationResponse.From(e.StartPosition), e.EndedAt, e.EndValue, e.EndValue - e.StartValue, e.FuelEndPercent, e.EndPhoto,
            LocationResponse.From(e.EndPosition),
            [.. e.Stoppages.Select(s => new StoppageResponse(s.Reason, s.Minutes))], e.Remarks, e.Estimated, e.Flags, e.EnvelopeId, e.DeviceId,
            e.ReceivedAt);
    }
}

/// <summary>Checklists: the newest version per asset type (list), or every version of one type, newest first.</summary>
public sealed record ChecklistListResponse(IReadOnlyList<ChecklistResponse> Items);

/// <summary>An item: its text per language (<c>fr</c>, <c>en</c>, <c>ar</c>, <c>emk</c>, <c>nqo</c>), critical, photo on Not OK.</summary>
public sealed record ChecklistItemResponse(string Code, IReadOnlyDictionary<string, string> Texts, bool Critical, bool PhotoOnNotOk);

/// <summary>A published version of an asset type's checklist (ADR-0035 §2); <c>required</c>: answered before every start.</summary>
public sealed record ChecklistResponse(
    Guid Id, string AssetType, int Version, bool Required, IReadOnlyList<ChecklistItemResponse> Items, DateTimeOffset PublishedAt)
{
    public static ChecklistResponse From(ChecklistTemplate c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return new(c.Id.Value, c.AssetType, c.Version, c.Required, [.. c.Items.Select(i => new ChecklistItemResponse(i.Code, i.Texts, i.Critical, i.PhotoOnNotOk))], c.PublishedAt);
    }
}
