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
/// The company's machines (US-P1-AST-01, US-P1-AST-02, ADR-0026): the type catalogue and the register, with search,
/// filters and moves between sites. Routes under <c>/api/v1/assets</c>.
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

        services.AddAssetsInfrastructure(connectionString);
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

/// <summary>An asset; <c>Sites</c> lists its arrivals at sites, oldest first (each stay lasts until the next).</summary>
public sealed record AssetResponse(
    Guid Id, string Code, string Name, string Type, string? Make, string? Model, string? SerialNumber, string? Plate, int? Year, DateOnly? AcquiredOn,
    Guid SiteId, string Status, IReadOnlyList<SiteAssignmentResponse> Sites, DateTimeOffset CreatedAt, DateTimeOffset ChangedAt, DateTimeOffset? DisposedAt)
{
    public static AssetResponse From(Asset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return new(asset.Id.Value, asset.Code, asset.Name, asset.Type, asset.Make, asset.Model, asset.SerialNumber, asset.Plate, asset.Year, asset.AcquiredOn,
            asset.SiteId, AssetRules.StatusCode(asset.Status), [.. asset.Sites.OrderBy(s => s.From).Select(s => new SiteAssignmentResponse(s.SiteId, s.From))],
            asset.CreatedAt, asset.ChangedAt, asset.DisposedAt);
    }
}

public sealed record SiteAssignmentResponse(Guid SiteId, DateTimeOffset From);
