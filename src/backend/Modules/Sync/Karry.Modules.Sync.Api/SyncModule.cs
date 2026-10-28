using System.Security.Claims;
using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Devices;
using Karry.BuildingBlocks.Modules;
using Karry.BuildingBlocks.Sync;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Sync.Application;
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
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<SyncChanges>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/changes", ChangesAsync)
            .RequireDevice()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("GetSyncChanges");
    }

    /// <summary>The tablet's reference data: a snapshot without <paramref name="cursor"/>, else what changed since it.</summary>
    private static async Task<Results<Ok<ChangeSet>, ProblemHttpResult>> ChangesAsync(
        ClaimsPrincipal user, [FromServices] SyncChanges changes, [FromQuery] string? cursor, CancellationToken cancellationToken)
    {
        var caller = DeviceCaller.From(user)!.Value;
        var result = await changes.PullAsync(new SyncScope(caller.DeviceId, caller.SiteId), cursor, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }
}
