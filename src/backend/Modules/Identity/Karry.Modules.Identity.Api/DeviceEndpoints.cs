using System.Security.Claims;
using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Devices;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Identity.Application;
using Karry.Modules.Identity.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Karry.Modules.Identity.Api;

public sealed record DeviceResponse(
    Guid Id, string Name, string? Model, string? Platform, string? AppVersion, Guid SiteId, string Status,
    DateTimeOffset EnrolledAt, DateTimeOffset? LastSeenAt, DateTimeOffset? RevokedAt);

/// <summary>
/// Site tablets (US-P1-OFF-02, ADR-0028): enrollment codes, the device list and revocation for administrators; the
/// anonymous enrollment itself; and what an enrolled tablet asks for itself (its record, a field user's online unlock).
/// </summary>
internal static class DeviceEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/devices/enrollments", CreateEnrollmentAsync)
            .RequirePermission(IdentityPermissions.DevicesManage)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithName("CreateDeviceEnrollment");

        endpoints.MapGet("/devices", ListAsync)
            .RequirePermission(IdentityPermissions.DevicesManage)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithName("ListDevices");

        endpoints.MapPost("/devices/{id:guid}/revoke", RevokeAsync)
            .RequirePermission(IdentityPermissions.DevicesManage)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("RevokeDevice");

        // The tablet has no credential yet: the code is the credential, the slug names the company.
        endpoints.MapPost("/devices/enroll", EnrollAsync)
            .AllowAnonymous()
            .AllowWithoutTenant()
            .Validate<EnrollDevice>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("EnrollDevice");

        endpoints.MapGet("/devices/me", SelfAsync)
            .RequireDevice()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .WithName("GetOwnDevice");

        endpoints.MapPost("/devices/unlock", UnlockAsync)
            .RequireDevice()
            .Validate<UnlockOnTablet>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .WithName("UnlockTablet");
    }

    private static async Task<Results<Created<EnrollmentCreated>, ProblemHttpResult>> CreateEnrollmentAsync(
        CreateEnrollment request, [FromServices] DeviceAdministration devices, CancellationToken cancellationToken)
    {
        var result = await devices.CreateEnrollmentAsync(request, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? TypedResults.Created($"/api/v1/identity/devices/enrollments/{result.Value.EnrollmentId}", result.Value)
            : result.Error.ToProblem();
    }

    private static async Task<Ok<IReadOnlyList<DeviceResponse>>> ListAsync([FromServices] DeviceAdministration devices, CancellationToken cancellationToken) =>
        TypedResults.Ok<IReadOnlyList<DeviceResponse>>([.. (await devices.ListAsync(cancellationToken).ConfigureAwait(false)).Select(ToResponse)]);

    private static async Task<Results<Ok<DeviceResponse>, ProblemHttpResult>> RevokeAsync(
        Guid id, [FromServices] DeviceAdministration devices, CancellationToken cancellationToken)
    {
        var result = await devices.RevokeAsync(id, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.Ok(ToResponse(result.Value)) : result.Error.ToProblem();
    }

    private static async Task<Results<Created<DeviceEnrolledResponse>, ProblemHttpResult>> EnrollAsync(
        EnrollDevice request, [FromServices] EnrollDeviceHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(request, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? TypedResults.Created($"/api/v1/identity/devices/{result.Value.DeviceId}", result.Value)
            : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<DeviceSelf>, ProblemHttpResult>> SelfAsync(
        ClaimsPrincipal user, [FromServices] TabletSession session, CancellationToken cancellationToken)
    {
        var result = await session.SelfAsync(DeviceCaller.From(user)!.Value, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<TabletUnlocked>, ProblemHttpResult>> UnlockAsync(
        UnlockOnTablet request, ClaimsPrincipal user, [FromServices] TabletSession session, CancellationToken cancellationToken)
    {
        var result = await session.UnlockAsync(DeviceCaller.From(user)!.Value, request, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static DeviceResponse ToResponse(Device d) => new(
        d.Id.Value, d.Name, d.Model, d.Platform, d.AppVersion, d.SiteId, d.Status.ToString().ToLowerInvariant(), d.EnrolledAt, d.LastSeenAt, d.RevokedAt);
}
