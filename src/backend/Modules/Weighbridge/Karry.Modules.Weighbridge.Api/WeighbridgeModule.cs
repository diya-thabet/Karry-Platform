using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Idempotency;
using Karry.BuildingBlocks.Modules;
using Karry.Modules.Weighbridge.Application;
using Karry.Modules.Weighbridge.Domain;
using Karry.Modules.Weighbridge.Infrastructure;
using Karry.SharedKernel.Authorization;
using Karry.SharedKernel.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Weighbridge.Api;

/// <summary>
/// The weighbridge (ADR-0042): haulers and trucks in P3.2 (US-P3-SAL-04); tickets, numbering and the desk follow. Routes
/// under <c>/api/v1/weighbridge</c>.
/// </summary>
public sealed class WeighbridgeModule : IModule
{
    public string Name => "weighbridge";

    public IReadOnlyList<PermissionDefinition> Permissions => WeighbridgePermissions.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString("Karry");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Host without a database (some host tests): endpoints still map ([FromServices]) and fail only when called.
            return;
        }

        services.AddWeighbridgeInfrastructure(connectionString);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/haulers", ListHaulersAsync).RequirePermission(WeighbridgePermissions.TrucksRead).WithName("ListHaulers");

        endpoints.MapPost("/haulers", CreateHaulerAsync)
            .RequirePermission(WeighbridgePermissions.TrucksManage)
            .RequireIdempotencyKey()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("CreateHauler");

        endpoints.MapPut("/haulers/{haulerId:guid}", ChangeHaulerAsync)
            .RequirePermission(WeighbridgePermissions.TrucksManage)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("ChangeHauler");

        // The desk's search: any part of the plate, spaces, dashes and case ignored (TC-P3-WB-016).
        endpoints.MapGet("/trucks", SearchTrucksAsync)
            .RequirePermission(WeighbridgePermissions.TrucksRead)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithName("SearchTrucks");

        endpoints.MapGet("/trucks/{truckId:guid}", GetTruckAsync)
            .RequirePermission(WeighbridgePermissions.TrucksRead)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetTruck");

        endpoints.MapPost("/trucks", RegisterTruckAsync)
            .RequirePermission(WeighbridgePermissions.TrucksManage)
            .RequireIdempotencyKey()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("RegisterTruck");

        // US-P3-SAL-04 AC2: a plate nobody knew, so the truck can be weighed; unverified and flagged (RULE-WB-09).
        endpoints.MapPost("/trucks/unknown", CreateUnknownTruckAsync)
            .RequirePermission(WeighbridgePermissions.TrucksManage)
            .RequireIdempotencyKey()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("CreateUnknownTruck");

        endpoints.MapPut("/trucks/{truckId:guid}", ChangeTruckAsync)
            .RequirePermission(WeighbridgePermissions.TrucksManage)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("ChangeTruck");

        endpoints.MapPost("/trucks/{truckId:guid}/verify", VerifyTruckAsync)
            .RequirePermission(WeighbridgePermissions.TrucksVerify)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("VerifyTruck");

        endpoints.MapPost("/trucks/{truckId:guid}/archive", ArchiveTruckAsync)
            .RequirePermission(WeighbridgePermissions.TrucksManage)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("ArchiveTruck");

        endpoints.MapPost("/trucks/{truckId:guid}/restore", RestoreTruckAsync)
            .RequirePermission(WeighbridgePermissions.TrucksManage)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("RestoreTruck");
    }

    private static async Task<Ok<HaulerListResponse>> ListHaulersAsync(
        [FromServices] HaulerOperations haulers, CancellationToken cancellationToken, bool includeArchived = false) =>
        TypedResults.Ok(new HaulerListResponse([.. (await haulers.ListAsync(includeArchived, cancellationToken).ConfigureAwait(false)).Select(HaulerResponse.From)]));

    private static async Task<Results<Created<HaulerResponse>, ProblemHttpResult>> CreateHaulerAsync(
        HaulerInput request, [FromServices] HaulerOperations haulers, CancellationToken cancellationToken)
    {
        var result = await haulers.CreateAsync(request, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? TypedResults.Created($"/api/v1/weighbridge/haulers/{result.Value.Id}", HaulerResponse.From(result.Value))
            : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<HaulerResponse>, ProblemHttpResult>> ChangeHaulerAsync(
        Guid haulerId, HaulerInput request, [FromHeader(Name = "If-Match")] string? ifMatch, HttpResponse response,
        [FromServices] HaulerOperations haulers, CancellationToken cancellationToken)
    {
        var expected = RecordVersionHttp.Expected(ifMatch);
        if (!expected.IsSuccess)
        {
            return expected.Error.ToProblem();
        }

        var result = await haulers.ChangeAsync(new HaulerId(haulerId), request, cancellationToken, expected.Value).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return result.Error.ToProblem();
        }

        response.SetETag(result.Value.Version);
        return TypedResults.Ok(HaulerResponse.From(result.Value));
    }

    /// <summary><c>plate</c>: any part of it; <c>status</c>: active, unverified or archived; at most 50, exact plate first.</summary>
    private static async Task<Results<Ok<TruckListResponse>, ProblemHttpResult>> SearchTrucksAsync(
        [FromServices] TruckOperations trucks, CancellationToken cancellationToken, string? plate = null, string? status = null, Guid? haulerId = null)
    {
        var found = await trucks.SearchAsync(plate, status, haulerId, cancellationToken).ConfigureAwait(false);
        return found.IsSuccess ? TypedResults.Ok(new TruckListResponse([.. found.Value.Select(TruckResponse.From)])) : found.Error.ToProblem();
    }

    private static async Task<Results<Ok<TruckResponse>, ProblemHttpResult>> GetTruckAsync(
        Guid truckId, HttpResponse response, [FromServices] TruckOperations trucks, CancellationToken cancellationToken) =>
        Respond(await trucks.GetAsync(new TruckId(truckId), cancellationToken).ConfigureAwait(false), response);

    private static async Task<Results<Created<TruckResponse>, ProblemHttpResult>> RegisterTruckAsync(
        TruckInput request, [FromServices] TruckOperations trucks, CancellationToken cancellationToken) =>
        Created(await trucks.RegisterAsync(request, cancellationToken).ConfigureAwait(false));

    private static async Task<Results<Created<TruckResponse>, ProblemHttpResult>> CreateUnknownTruckAsync(
        UnknownTruckInput request, [FromServices] TruckOperations trucks, CancellationToken cancellationToken) =>
        Created(await trucks.CreateUnknownAsync(request, cancellationToken).ConfigureAwait(false));

    private static async Task<Results<Ok<TruckResponse>, ProblemHttpResult>> ChangeTruckAsync(
        Guid truckId, TruckInput request, [FromHeader(Name = "If-Match")] string? ifMatch, HttpResponse response,
        [FromServices] TruckOperations trucks, CancellationToken cancellationToken)
    {
        var expected = RecordVersionHttp.Expected(ifMatch);
        return expected.IsSuccess
            ? Respond(await trucks.ChangeAsync(new TruckId(truckId), request, cancellationToken, expected.Value).ConfigureAwait(false), response)
            : expected.Error.ToProblem();
    }

    private static async Task<Results<Ok<TruckResponse>, ProblemHttpResult>> VerifyTruckAsync(
        Guid truckId, HttpResponse response, [FromServices] TruckOperations trucks, CancellationToken cancellationToken) =>
        Respond(await trucks.VerifyAsync(new TruckId(truckId), cancellationToken).ConfigureAwait(false), response);

    private static async Task<Results<Ok<TruckResponse>, ProblemHttpResult>> ArchiveTruckAsync(
        Guid truckId, HttpResponse response, [FromServices] TruckOperations trucks, CancellationToken cancellationToken) =>
        Respond(await trucks.ArchiveAsync(new TruckId(truckId), cancellationToken).ConfigureAwait(false), response);

    private static async Task<Results<Ok<TruckResponse>, ProblemHttpResult>> RestoreTruckAsync(
        Guid truckId, HttpResponse response, [FromServices] TruckOperations trucks, CancellationToken cancellationToken) =>
        Respond(await trucks.RestoreAsync(new TruckId(truckId), cancellationToken).ConfigureAwait(false), response);

    private static Results<Created<TruckResponse>, ProblemHttpResult> Created(Result<Truck> result) =>
        result.IsSuccess
            ? TypedResults.Created($"/api/v1/weighbridge/trucks/{result.Value.Id}", TruckResponse.From(result.Value))
            : result.Error.ToProblem();

    private static Results<Ok<TruckResponse>, ProblemHttpResult> Respond(Result<Truck> result, HttpResponse response)
    {
        if (!result.IsSuccess)
        {
            return result.Error.ToProblem();
        }

        response.SetETag(result.Value.Version);
        return TypedResults.Ok(TruckResponse.From(result.Value));
    }
}

public sealed record HaulerListResponse(IReadOnlyList<HaulerResponse> Items);

public sealed record HaulerResponse(Guid Id, string Name, string? Phone, string Status, DateTimeOffset CreatedAt, DateTimeOffset ChangedAt, string Version)
{
    public static HaulerResponse From(Hauler hauler)
    {
        ArgumentNullException.ThrowIfNull(hauler);
        return new(hauler.Id.Value, hauler.Name, hauler.Phone, hauler.Status.ToString().ToLowerInvariant(), hauler.CreatedAt, hauler.ChangedAt,
            RecordVersionHttp.ETag(hauler.Version));
    }
}

public sealed record TruckListResponse(IReadOnlyList<TruckResponse> Items);

/// <summary>A registered tare: <c>Kg</c> (whole kg), the day it was established and <c>Method</c> (weighed or declared).</summary>
public sealed record TareResponse(decimal Kg, DateOnly On, string Method);

/// <summary>
/// A truck. <c>Plate</c> as written; <c>PlateKey</c> what plates are compared on. <c>Status</c>: active, unverified
/// (created at the desk, flagged until verified) or archived. Masses in kg.
/// </summary>
public sealed record TruckResponse(
    Guid Id, string Plate, string PlateKey, Guid? HaulerId, TareResponse? Tare, decimal? MaxPayloadKg, string? Axles, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset ChangedAt, string Version)
{
    public static TruckResponse From(Truck truck)
    {
        ArgumentNullException.ThrowIfNull(truck);
        return new(truck.Id.Value, truck.Plate, truck.PlateKey, truck.HaulerId,
            truck.Tare is { } tare ? new TareResponse(tare.Kg, tare.On, TruckRules.MethodCode(tare.Method)) : null,
            truck.MaxPayloadKg, truck.Axles, truck.Status.ToString().ToLowerInvariant(), truck.CreatedAt, truck.ChangedAt, RecordVersionHttp.ETag(truck.Version));
    }
}
