using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Modules;
using Karry.Modules.Verification.Application;
using Karry.Modules.Verification.Domain;
using Karry.Modules.Verification.Infrastructure;
using Karry.SharedKernel.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Verification.Api;

/// <summary>
/// Verification (ADR-0037): a review item for every record version the other modules submit, decided by a controller.
/// Routes under <c>/api/v1/verification</c>.
/// </summary>
public sealed class VerificationModule : IModule
{
    public string Name => "verification";

    public IReadOnlyList<PermissionDefinition> Permissions => VerificationPermissions.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString("Karry");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Host without a database (some host tests): endpoints still map ([FromServices]) and fail only when called.
            return;
        }

        services.AddVerificationInfrastructure(connectionString);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/items", SearchItemsAsync)
            .RequirePermission(VerificationPermissions.ItemsRead)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithName("SearchReviewItems");
    }

    /// <summary>
    /// Review items of the caller's sites, oldest first: <c>status</c> pending (default), reviewed or all; by site and
    /// record type; <c>cursor</c> is the last item ID of the previous page.
    /// </summary>
    private static async Task<Results<Ok<ReviewItemListResponse>, ProblemHttpResult>> SearchItemsAsync(
        Guid? siteId, string? status, string? recordType, Guid? cursor, int? limit, [FromServices] ReviewReading items, CancellationToken cancellationToken)
    {
        var result = await items.SearchAsync(siteId, status, recordType, cursor, limit, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? TypedResults.Ok(new ReviewItemListResponse([.. result.Value.Items.Select(ReviewItemResponse.From)], result.Value.NextCursor))
            : result.Error.ToProblem();
    }
}

/// <summary>A page of review items, oldest first; <c>nextCursor</c> when more follow.</summary>
public sealed record ReviewItemListResponse(IReadOnlyList<ReviewItemResponse> Items, Guid? NextCursor);

/// <summary>
/// A review item (ADR-0037 §2): the record version it is about, the hash it was signed with, who produced it, the asset
/// and time it covers, its flags when submitted, and where it stands.
/// </summary>
public sealed record ReviewItemResponse(
    Guid Id, string RecordType, Guid RecordId, int RecordVersion, string RecordHash, Guid SiteId, Guid ProducedBy, Guid? AssetId,
    DateTimeOffset CoveredFrom, DateTimeOffset? CoveredTo, IReadOnlyList<string> Flags, string Status, DateTimeOffset CreatedAt)
{
    public static ReviewItemResponse From(ReviewItem i)
    {
        ArgumentNullException.ThrowIfNull(i);
        return new(i.Id.Value, i.RecordType, i.RecordId, i.RecordVersion, i.RecordHash, i.SiteId, i.ProducedBy, i.AssetId, i.CoveredFrom, i.CoveredTo,
            i.Flags, i.Status, i.CreatedAt);
    }
}
