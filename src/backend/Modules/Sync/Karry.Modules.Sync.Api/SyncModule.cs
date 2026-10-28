using System.Security.Claims;
using FluentValidation;
using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Devices;
using Karry.BuildingBlocks.Modules;
using Karry.BuildingBlocks.Sync;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Sync.Application;
using Karry.Modules.Sync.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Karry.Modules.Sync.Api;

/// <summary>
/// What enrolled tablets download and upload (ADR-0028 §5–§6). Routes under <c>/api/v1/sync</c>, for devices only.
/// </summary>
public sealed class SyncModule : IModule
{
    public string Name => "sync";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<SyncChanges>();
        services.AddScoped<SyncUploads>();
        services.AddScoped<IValidator<UploadBatch>, UploadBatchValidator>();
        var connectionString = configuration.GetConnectionString("Karry");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            // Host without a database (some host tests): endpoints still map ([FromServices]) and fail only when called.
            services.AddSyncInfrastructure(connectionString);
        }
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/changes", ChangesAsync)
            .RequireDevice()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("GetSyncChanges");

        // No Idempotency-Key: command IDs make a batch safe to send again, and the answer must be today's (the
        // receipts and the device's last sequence), not a stored copy of an earlier one.
        endpoints.MapPost("/batches", UploadAsync)
            .RequireDevice(allowRevoked: true)
            .Validate<UploadBatch>()
            .WithMetadata(new RequestSizeLimitAttribute(MaxBatchBytes))
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("UploadSyncBatch");
    }

    /// <summary>100 commands of up to 64 KB each, with room for their JSON framing.</summary>
    private const long MaxBatchBytes = 8 * 1024 * 1024;

    /// <summary>The tablet's reference data: a snapshot without <paramref name="cursor"/>, else what changed since it.</summary>
    private static async Task<Results<Ok<ChangeSet>, ProblemHttpResult>> ChangesAsync(
        ClaimsPrincipal user, [FromServices] SyncChanges changes, [FromQuery] string? cursor, CancellationToken cancellationToken)
    {
        var caller = DeviceCaller.From(user)!.Value;
        var result = await changes.PullAsync(new SyncScope(caller.DeviceId, caller.SiteId), cursor, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    /// <summary>
    /// Stores a batch of signed commands in the inbox and answers what happened to each. A revoked tablet's commands
    /// are kept quarantined and the answer is 403 <c>devices.revoked</c>.
    /// </summary>
    private static async Task<Results<Ok<BatchReceipt>, ProblemHttpResult>> UploadAsync(
        ClaimsPrincipal user, [FromServices] SyncUploads uploads, UploadBatch batch, CancellationToken cancellationToken)
    {
        var result = await uploads.UploadAsync(DeviceCaller.From(user)!.Value, batch, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }
}
