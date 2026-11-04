using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Idempotency;
using Karry.BuildingBlocks.Modules;
using Karry.Modules.Materials.Application;
using Karry.Modules.Materials.Domain;
using Karry.Modules.Materials.Infrastructure;
using Karry.SharedKernel.Authorization;
using Karry.SharedKernel.Units;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Materials.Api;

/// <summary>
/// The company's materials (US-P1-AST-04) with their density versions, and conversions between tonnes and m³ in each
/// state (US-P1-UNI-01, ADR-0010/0025). Routes under <c>/api/v1/materials</c>.
/// </summary>
public sealed class MaterialsModule : IModule
{
    public string Name => "materials";

    public IReadOnlyList<PermissionDefinition> Permissions => MaterialsPermissions.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString("Karry");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Host without a database (some host tests): endpoints still map ([FromServices]) and fail only when called.
            return;
        }

        services.AddMaterialsInfrastructure(connectionString);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("", ListMaterialsAsync)
            .RequirePermission(MaterialsPermissions.CatalogRead)
            .WithName("ListMaterials");

        endpoints.MapGet("/{materialId:guid}", GetMaterialAsync)
            .RequirePermission(MaterialsPermissions.CatalogRead)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetMaterial");

        endpoints.MapPost("", CreateMaterialAsync)
            .RequirePermission(MaterialsPermissions.CatalogManage)
            .RequireIdempotencyKey()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("CreateMaterial");

        endpoints.MapPut("/{materialId:guid}", ChangeMaterialAsync)
            .RequirePermission(MaterialsPermissions.CatalogManage)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("ChangeMaterial");

        endpoints.MapPost("/{materialId:guid}/archive", ArchiveMaterialAsync)
            .RequirePermission(MaterialsPermissions.CatalogManage)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("ArchiveMaterial");

        endpoints.MapPost("/{materialId:guid}/restore", RestoreMaterialAsync)
            .RequirePermission(MaterialsPermissions.CatalogManage)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("RestoreMaterial");

        // Density versions (US-P1-AST-04 AC2): by the date they start on; only dates after the company's today change.
        endpoints.MapPut("/{materialId:guid}/densities/{effectiveFrom}", SetDensityVersionAsync)
            .RequirePermission(MaterialsPermissions.CatalogManage)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("SetDensityVersion");

        endpoints.MapDelete("/{materialId:guid}/densities/{effectiveFrom}", RemoveDensityVersionAsync)
            .RequirePermission(MaterialsPermissions.CatalogManage)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("RemoveDensityVersion");

        // A calculation, not a change: nothing is stored, so no idempotency key (US-P1-UNI-01 AC2, AC4).
        endpoints.MapPost("/{materialId:guid}/conversions", ConvertAsync)
            .RequirePermission(MaterialsPermissions.CatalogRead)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("ConvertQuantity");
    }

    private static async Task<Ok<MaterialListResponse>> ListMaterialsAsync(
        [FromServices] MaterialOperations materials, CancellationToken cancellationToken, bool includeArchived = false)
    {
        var list = await materials.ListAsync(includeArchived, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new MaterialListResponse([.. list.Select(MaterialResponse.From)]));
    }

    private static async Task<Results<Ok<MaterialResponse>, ProblemHttpResult>> GetMaterialAsync(
        Guid materialId, HttpResponse response, [FromServices] MaterialOperations materials, CancellationToken cancellationToken) =>
        Respond(await materials.GetAsync(new MaterialId(materialId), cancellationToken).ConfigureAwait(false), response);

    private static async Task<Results<Created<MaterialResponse>, ProblemHttpResult>> CreateMaterialAsync(
        MaterialInput request, [FromServices] MaterialOperations materials, CancellationToken cancellationToken)
    {
        var result = await materials.CreateAsync(request, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? TypedResults.Created($"/api/v1/materials/{result.Value.Id}", MaterialResponse.From(result.Value))
            : result.Error.ToProblem();
    }

    /// <summary><c>If-Match</c>: the <c>ETag</c> (or <c>version</c>) the form was filled from; a newer material is a 409 (H-C1).</summary>
    private static async Task<Results<Ok<MaterialResponse>, ProblemHttpResult>> ChangeMaterialAsync(
        Guid materialId, MaterialChangeInput request, [FromHeader(Name = "If-Match")] string? ifMatch, HttpResponse response,
        [FromServices] MaterialOperations materials, CancellationToken cancellationToken)
    {
        var expected = RecordVersionHttp.Expected(ifMatch);
        return expected.IsSuccess
            ? Respond(await materials.ChangeAsync(new MaterialId(materialId), request, cancellationToken, expected.Value).ConfigureAwait(false), response)
            : expected.Error.ToProblem();
    }

    private static async Task<Results<Ok<MaterialResponse>, ProblemHttpResult>> ArchiveMaterialAsync(
        Guid materialId, HttpResponse response, [FromServices] MaterialOperations materials, CancellationToken cancellationToken) =>
        Respond(await materials.ArchiveAsync(new MaterialId(materialId), cancellationToken).ConfigureAwait(false), response);

    private static async Task<Results<Ok<MaterialResponse>, ProblemHttpResult>> RestoreMaterialAsync(
        Guid materialId, HttpResponse response, [FromServices] MaterialOperations materials, CancellationToken cancellationToken) =>
        Respond(await materials.RestoreAsync(new MaterialId(materialId), cancellationToken).ConfigureAwait(false), response);

    private static async Task<Results<Ok<DensityVersionResponse>, ProblemHttpResult>> SetDensityVersionAsync(
        Guid materialId, DateOnly effectiveFrom, DensityInput request, [FromServices] MaterialOperations materials, CancellationToken cancellationToken)
    {
        var result = await materials.SetVersionAsync(new MaterialId(materialId), effectiveFrom, request, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.Ok(DensityVersionResponse.From(result.Value)) : result.Error.ToProblem();
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> RemoveDensityVersionAsync(
        Guid materialId, DateOnly effectiveFrom, [FromServices] MaterialOperations materials, CancellationToken cancellationToken)
    {
        var result = await materials.RemoveVersionAsync(new MaterialId(materialId), effectiveFrom, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<ConversionResponse>, ProblemHttpResult>> ConvertAsync(
        Guid materialId, ConversionInput request, [FromServices] MaterialOperations materials, CancellationToken cancellationToken)
    {
        var result = await materials.ConvertAsync(new MaterialId(materialId), request, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.Ok(ConversionResponse.From(result.Value)) : result.Error.ToProblem();
    }

    private static Results<Ok<MaterialResponse>, ProblemHttpResult> Respond(Karry.SharedKernel.Results.Result<Material> result, HttpResponse response)
    {
        if (!result.IsSuccess)
        {
            return result.Error.ToProblem();
        }

        response.SetETag(result.Value.Version);
        return TypedResults.Ok(MaterialResponse.From(result.Value));
    }
}

public sealed record MaterialListResponse(IReadOnlyList<MaterialResponse> Items);

/// <summary>A material; <c>Densities</c> lists its versions, oldest first (each valid until the next one).</summary>
public sealed record MaterialResponse(
    Guid Id, string Code, string Name, string Kind, IReadOnlyList<DensityVersionResponse> Densities, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset ChangedAt, DateTimeOffset? ArchivedAt, string Version)
{
    public static MaterialResponse From(Material material)
    {
        ArgumentNullException.ThrowIfNull(material);
        return new(material.Id.Value, material.Code, material.Name, MaterialRules.KindCode(material.Kind),
            [.. material.Versions.Select(DensityVersionResponse.From)], material.Status.ToString().ToLowerInvariant(),
            material.CreatedAt, material.ChangedAt, material.ArchivedAt, RecordVersionHttp.ETag(material.Version));
    }
}

/// <summary>Densities in t/m³ from <c>EffectiveFrom</c>; <c>Swell</c> = bank / loose − 1 when both are known.</summary>
public sealed record DensityVersionResponse(DateOnly EffectiveFrom, decimal? Bank, decimal? Loose, decimal? Compacted, decimal Moisture, decimal? Swell)
{
    public static DensityVersionResponse From(DensityVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return new(version.EffectiveFrom, version.Bank, version.Loose, version.Compacted, version.Moisture,
            version.Densities.Swell is { } swell ? decimal.Round(swell, 6, MidpointRounding.AwayFromZero) : null);
    }
}

/// <summary>A quantity on the wire: <c>State</c> is bank, loose or compacted for m³ and null otherwise.</summary>
public sealed record QuantityResponse(decimal Value, string Unit, string? State)
{
    public static QuantityResponse From(Quantity quantity) =>
        new(quantity.Value, quantity.Unit.Code, quantity.State is { } state ? MaterialStates.Code(state) : null);
}

public sealed record ConversionFactorResponse(string Name, decimal Value, string Unit);

/// <summary>The converted quantity (full precision: round only for display), the factors used and the version's date.</summary>
public sealed record ConversionResponse(QuantityResponse Result, IReadOnlyList<ConversionFactorResponse> Factors, DateOnly DensitiesFrom)
{
    public static ConversionResponse From(MaterialConversionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new(QuantityResponse.From(result.Conversion.Result),
            [.. result.Conversion.Factors.Select(f => new ConversionFactorResponse(f.Name, f.Value, f.Unit))], result.Version.EffectiveFrom);
    }
}
