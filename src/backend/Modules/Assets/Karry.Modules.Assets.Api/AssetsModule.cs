using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Idempotency;
using Karry.BuildingBlocks.Modules;
using Karry.Modules.Assets.Application;
using Karry.Modules.Assets.Domain;
using Karry.Modules.Assets.Infrastructure;
using Karry.SharedKernel.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Assets.Api;

/// <summary>
/// The company's machines (US-P1-AST-01 … 03, ADR-0026): the type catalogue, the register with search, filters and moves
/// between sites, and the meters with their append-only readings. Routes under <c>/api/v1/assets</c>.
/// </summary>
public sealed class AssetsModule : IModule
{
    public string Name => "assets";

    public IReadOnlyList<PermissionDefinition> Permissions => AssetsPermissions.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString("Karry");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Host without a database (some host tests): endpoints still map ([FromServices]) and fail only when called.
            return;
        }

        services.AddAssetsInfrastructure(connectionString, configuration);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/types", ListAssetTypes)
            .RequirePermission(AssetsPermissions.RegistryRead)
            .WithName("ListAssetTypes");

        endpoints.MapGet("", SearchAssetsAsync)
            .RequirePermission(AssetsPermissions.RegistryRead)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithName("SearchAssets");

        endpoints.MapGet("/{assetId:guid}", GetAssetAsync)
            .RequirePermission(AssetsPermissions.RegistryRead)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetAsset");

        endpoints.MapPost("", RegisterAssetAsync)
            .RequirePermission(AssetsPermissions.RegistryManage)
            .RequireIdempotencyKey()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("RegisterAsset");

        endpoints.MapPut("/{assetId:guid}", ChangeAssetAsync)
            .RequirePermission(AssetsPermissions.RegistryManage)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("ChangeAsset");

        // A new arrival in the asset's site history (append-only); the caller manages both sites.
        endpoints.MapPost("/{assetId:guid}/move", MoveAssetAsync)
            .RequirePermission(AssetsPermissions.RegistryManage)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("MoveAsset");

        endpoints.MapPost("/{assetId:guid}/status", SetAssetStatusAsync)
            .RequirePermission(AssetsPermissions.RegistryManage)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("SetAssetStatus");

        // Photo (US-P1-AST-02 AC1): in object storage, streamed after the same checks as the asset.
        endpoints.MapPut("/{assetId:guid}/photo", SetPhotoAsync)
            .RequirePermission(AssetsPermissions.RegistryManage)
            .Accepts<IFormFile>("image/jpeg", "image/png", "image/webp")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("SetAssetPhoto");

        endpoints.MapGet("/{assetId:guid}/photo", PhotoAsync)
            .RequirePermission(AssetsPermissions.RegistryRead)
            .Produces(StatusCodes.Status200OK, contentType: "image/jpeg")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetAssetPhoto");

        endpoints.MapDelete("/{assetId:guid}/photo", RemovePhotoAsync)
            .RequirePermission(AssetsPermissions.RegistryManage)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("RemoveAssetPhoto");

        // QR labels (US-P1-AST-02 AC2, ADR-0026): a scanned label resolves only inside the caller's company and sites.
        endpoints.MapGet("/labels/{label}", ResolveLabelAsync)
            .RequirePermission(AssetsPermissions.RegistryRead)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("ResolveAssetLabel");

        endpoints.MapPost("/labels/sheet", LabelSheetAsync)
            .RequirePermission(AssetsPermissions.RegistryManage)
            .Produces(StatusCodes.Status200OK, contentType: "application/pdf")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("PrintAssetLabels");

        endpoints.MapPost("/{assetId:guid}/label", ReissueLabelAsync)
            .RequirePermission(AssetsPermissions.RegistryManage)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("ReissueAssetLabel");

        // Meters (US-P1-AST-03): readings are only ever added (TC-P1-AST-002); there is no route to change or remove one.
        endpoints.MapGet("/{assetId:guid}/meters", ListMetersAsync)
            .RequirePermission(AssetsPermissions.RegistryRead)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("ListMeters");

        endpoints.MapPost("/{assetId:guid}/meters", AddMeterAsync)
            .RequirePermission(AssetsPermissions.RegistryManage)
            .RequireIdempotencyKey()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("AddMeter");

        endpoints.MapPost("/{assetId:guid}/meters/{meterId:guid}/status", SetMeterStatusAsync)
            .RequirePermission(AssetsPermissions.RegistryManage)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("SetMeterStatus");

        endpoints.MapPost("/{assetId:guid}/meters/{meterId:guid}/readings", RecordReadingAsync)
            .RequirePermission(AssetsPermissions.RegistryManage)
            .RequireIdempotencyKey()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("RecordMeterReading");

        endpoints.MapGet("/{assetId:guid}/meters/{meterId:guid}/readings", ListReadingsAsync)
            .RequirePermission(AssetsPermissions.RegistryRead)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("ListMeterReadings");
    }

    private static Ok<AssetTypeListResponse> ListAssetTypes() =>
        TypedResults.Ok(new AssetTypeListResponse([.. AssetTypes.All.Select(AssetTypeResponse.From)]));

    private static async Task<Results<Ok<AssetListResponse>, ProblemHttpResult>> SearchAssetsAsync(
        [FromServices] AssetOperations assets, CancellationToken cancellationToken,
        string? q = null, Guid? siteId = null, string? type = null, string? status = null, string? cursor = null, int? limit = null)
    {
        var result = await assets.SearchAsync(new AssetSearch(q, siteId, type, status, cursor, limit), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? TypedResults.Ok(new AssetListResponse([.. result.Value.Items.Select(AssetResponse.From)], result.Value.NextCursor))
            : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<AssetResponse>, ProblemHttpResult>> GetAssetAsync(
        Guid assetId, [FromServices] AssetOperations assets, CancellationToken cancellationToken) =>
        Respond(await assets.GetAsync(new AssetId(assetId), cancellationToken).ConfigureAwait(false));

    private static async Task<Results<Created<AssetResponse>, ProblemHttpResult>> RegisterAssetAsync(
        AssetInput request, [FromServices] AssetOperations assets, CancellationToken cancellationToken)
    {
        var result = await assets.RegisterAsync(request, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? TypedResults.Created($"/api/v1/assets/{result.Value.Id}", AssetResponse.From(result.Value))
            : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<AssetResponse>, ProblemHttpResult>> ChangeAssetAsync(
        Guid assetId, AssetChangeInput request, [FromServices] AssetOperations assets, CancellationToken cancellationToken) =>
        Respond(await assets.ChangeAsync(new AssetId(assetId), request, cancellationToken).ConfigureAwait(false));

    private static async Task<Results<Ok<AssetResponse>, ProblemHttpResult>> MoveAssetAsync(
        Guid assetId, AssetMoveRequest request, [FromServices] AssetOperations assets, CancellationToken cancellationToken) =>
        Respond(await assets.MoveAsync(new AssetId(assetId), request.SiteId, cancellationToken).ConfigureAwait(false));

    private static async Task<Results<Ok<AssetResponse>, ProblemHttpResult>> SetAssetStatusAsync(
        Guid assetId, AssetStatusRequest request, [FromServices] AssetOperations assets, CancellationToken cancellationToken) =>
        Respond(await assets.SetStatusAsync(new AssetId(assetId), request.Status, cancellationToken).ConfigureAwait(false));

    private static async Task<Results<Ok<AssetResponse>, ProblemHttpResult>> SetPhotoAsync(
        Guid assetId, HttpRequest request, [FromServices] AssetOperations assets, CancellationToken cancellationToken)
    {
        // One byte more than allowed is enough to refuse; a larger body is never read into memory.
        var buffer = new byte[AssetPhotos.MaxBytes + 1];
        var length = 0;
        int read;
        while (length < buffer.Length && (read = await request.Body.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false)) > 0)
        {
            length += read;
        }

        return Respond(await assets.SetPhotoAsync(new AssetId(assetId), buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> PhotoAsync(Guid assetId, HttpContext context, [FromServices] AssetOperations assets, CancellationToken cancellationToken)
    {
        var result = await assets.PhotoAsync(new AssetId(assetId), cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return result.Error.ToProblem();
        }

        // An image only: never sniffed into something else, no active content even if opened directly; private to the caller.
        context.Response.Headers.CacheControl = "private, max-age=3600";
        context.Response.Headers.ContentSecurityPolicy = "default-src 'none'";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        var (photo, content) = result.Value;
        return TypedResults.File(content, photo.ContentType, entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{photo.Sha256}\""));
    }

    private static async Task<Results<Ok<AssetResponse>, ProblemHttpResult>> RemovePhotoAsync(
        Guid assetId, [FromServices] AssetOperations assets, CancellationToken cancellationToken) =>
        Respond(await assets.RemovePhotoAsync(new AssetId(assetId), cancellationToken).ConfigureAwait(false));

    private static async Task<Results<Ok<AssetResponse>, ProblemHttpResult>> ResolveLabelAsync(
        string label, [FromServices] AssetOperations assets, CancellationToken cancellationToken) =>
        Respond(await assets.ResolveLabelAsync(label, cancellationToken).ConfigureAwait(false));

    private static async Task<Results<FileContentHttpResult, ProblemHttpResult>> LabelSheetAsync(
        LabelSheetInput request, [FromServices] AssetOperations assets, CancellationToken cancellationToken)
    {
        var result = await assets.LabelSheetAsync(request, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.File(result.Value, "application/pdf", "asset-labels.pdf") : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<AssetResponse>, ProblemHttpResult>> ReissueLabelAsync(
        Guid assetId, [FromServices] AssetOperations assets, CancellationToken cancellationToken) =>
        Respond(await assets.ReissueLabelAsync(new AssetId(assetId), cancellationToken).ConfigureAwait(false));

    private static async Task<Results<Ok<MeterListResponse>, ProblemHttpResult>> ListMetersAsync(
        Guid assetId, [FromServices] MeterOperations meters, CancellationToken cancellationToken)
    {
        var result = await meters.ListAsync(new AssetId(assetId), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.Ok(new MeterListResponse([.. result.Value.Select(MeterResponse.From)])) : result.Error.ToProblem();
    }

    private static async Task<Results<Created<MeterResponse>, ProblemHttpResult>> AddMeterAsync(
        Guid assetId, MeterInput request, [FromServices] MeterOperations meters, CancellationToken cancellationToken)
    {
        var result = await meters.AddAsync(new AssetId(assetId), request, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? TypedResults.Created($"/api/v1/assets/{assetId}/meters/{result.Value.Id}", MeterResponse.From(result.Value))
            : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<MeterResponse>, ProblemHttpResult>> SetMeterStatusAsync(
        Guid assetId, Guid meterId, MeterStatusRequest request, [FromServices] MeterOperations meters, CancellationToken cancellationToken)
    {
        var result = await meters.SetStatusAsync(new AssetId(assetId), new MeterId(meterId), request.Status, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.Ok(MeterResponse.From(result.Value)) : result.Error.ToProblem();
    }

    private static async Task<Results<Created<ReadingResponse>, ProblemHttpResult>> RecordReadingAsync(
        Guid assetId, Guid meterId, ReadingInput request, [FromServices] MeterOperations meters, CancellationToken cancellationToken)
    {
        var result = await meters.RecordAsync(new AssetId(assetId), new MeterId(meterId), request, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? TypedResults.Created($"/api/v1/assets/{assetId}/meters/{meterId}/readings", ReadingResponse.From(result.Value))
            : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<ReadingListResponse>, ProblemHttpResult>> ListReadingsAsync(
        Guid assetId, Guid meterId, [FromServices] MeterOperations meters, CancellationToken cancellationToken, Guid? cursor = null, int? limit = null)
    {
        var result = await meters.ReadingsAsync(new AssetId(assetId), new MeterId(meterId), cursor, limit, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? TypedResults.Ok(new ReadingListResponse([.. result.Value.Items.Select(ReadingResponse.From)], result.Value.NextCursor))
            : result.Error.ToProblem();
    }

    private static Results<Ok<AssetResponse>, ProblemHttpResult> Respond(Karry.SharedKernel.Results.Result<Asset> result) =>
        result.IsSuccess ? TypedResults.Ok(AssetResponse.From(result.Value)) : result.Error.ToProblem();
}

public sealed record AssetMoveRequest(Guid? SiteId);

/// <summary><c>Status</c>: active, in-repair, idle or disposed.</summary>
public sealed record AssetStatusRequest(string? Status);

public sealed record AssetTypeListResponse(IReadOnlyList<AssetTypeResponse> Items);

/// <summary>A machine type: its default meters (<c>hours</c>, <c>km</c>) and component templates; the apps translate the codes.</summary>
public sealed record AssetTypeResponse(string Code, IReadOnlyList<string> DefaultMeters, IReadOnlyList<string> Components)
{
    public static AssetTypeResponse From(AssetType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return new(type.Code, [.. type.DefaultMeters.Select(AssetRules.MeterKindCode)], type.Components);
    }
}

/// <summary>A page of the register, ordered by code; <c>NextCursor</c> is null on the last page.</summary>
public sealed record AssetListResponse(IReadOnlyList<AssetResponse> Items, string? NextCursor);

/// <summary>
/// An asset; <c>Sites</c> lists its arrivals at sites, oldest first (each stay lasts until the next). <c>Label</c> is
/// what its QR label encodes (<c>KRY1:</c> and a random token), for the field app to match a scan offline.
/// </summary>
public sealed record AssetResponse(
    Guid Id, string Code, string Name, string Type, string? Make, string? Model, string? SerialNumber, string? Plate, int? Year, DateOnly? AcquiredOn,
    Guid SiteId, string Status, string Label, AssetPhotoResponse? Photo, IReadOnlyList<SiteAssignmentResponse> Sites, DateTimeOffset CreatedAt, DateTimeOffset ChangedAt,
    DateTimeOffset? DisposedAt)
{
    public static AssetResponse From(Asset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return new(asset.Id.Value, asset.Code, asset.Name, asset.Type, asset.Make, asset.Model, asset.SerialNumber, asset.Plate, asset.Year, asset.AcquiredOn,
            asset.SiteId, AssetRules.StatusCode(asset.Status), AssetLabels.Text(asset.LabelToken),
            asset.Photo is { } photo ? new AssetPhotoResponse(photo.Sha256, photo.ContentType, photo.Bytes) : null, [.. asset.Sites.OrderBy(s => s.From).Select(s => new SiteAssignmentResponse(s.SiteId, s.From))],
            asset.CreatedAt, asset.ChangedAt, asset.DisposedAt);
    }
}

/// <summary>The asset's photo, served at <c>GET /{assetId}/photo</c>; <c>Sha256</c> changes with it (use it to refresh a cached image).</summary>
public sealed record AssetPhotoResponse(string Sha256, string ContentType, int Bytes);

public sealed record SiteAssignmentResponse(Guid SiteId, DateTimeOffset From);

/// <summary><c>Status</c>: ok or failed (replacement comes with field operations).</summary>
public sealed record MeterStatusRequest(string? Status);

public sealed record MeterListResponse(IReadOnlyList<MeterResponse> Items);

/// <summary>
/// A meter: <c>Kind</c> hours or km, counted in <c>Unit</c> (h, km); <c>LastValue</c> and <c>LastReadAt</c> are its
/// latest reading (the initial one at first); <c>Offset</c> stays 0 until meter replacement (Phase 2).
/// </summary>
public sealed record MeterResponse(
    Guid Id, string Kind, string Unit, decimal InitialReading, DateOnly InstalledOn, string Status, decimal Offset, decimal LastValue, DateTimeOffset LastReadAt,
    DateTimeOffset CreatedAt)
{
    public static MeterResponse From(Meter meter)
    {
        ArgumentNullException.ThrowIfNull(meter);
        return new(meter.Id.Value, AssetRules.MeterKindCode(meter.Kind), MeterRules.UnitCode(meter.Kind), meter.InitialReading, meter.InstalledOn,
            MeterRules.StatusCode(meter.Status), meter.Offset, meter.LastValue, meter.LastReadAt, meter.CreatedAt);
    }
}

/// <summary>A page of readings, newest first; <c>NextCursor</c> is null on the last page.</summary>
public sealed record ReadingListResponse(IReadOnlyList<ReadingResponse> Items, Guid? NextCursor);

/// <summary>A reading: the value shown at <c>ReadAt</c>, received at <c>RecordedAt</c> from <c>RecordedBy</c> (a user subject).</summary>
public sealed record ReadingResponse(Guid Id, decimal Value, DateTimeOffset ReadAt, DateTimeOffset RecordedAt, string? RecordedBy)
{
    public static ReadingResponse From(MeterReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        return new(reading.Id, reading.Value, reading.ReadAt, reading.RecordedAt, reading.RecordedBy);
    }
}
