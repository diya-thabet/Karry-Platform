using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Modules;
using Karry.Modules.Verification.Application;
using Karry.Modules.Verification.Domain;
using Karry.Modules.Verification.Infrastructure;
using Karry.SharedKernel.Authorization;
using Karry.SharedKernel.Results;
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
            .Produces(StatusCodes.Status304NotModified)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithName("SearchReviewItems");

        endpoints.MapGet("/policies", ListPoliciesAsync)
            .RequirePermission(VerificationPermissions.ItemsRead)
            .WithName("ListReviewPolicies");

        endpoints.MapPut("/policies/{recordType}", PublishPolicyAsync)
            .RequirePermission(ReviewPolicyPermissions.Manage)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("PublishReviewPolicy");

        endpoints.MapPost("/items/approve", ApproveManyAsync)
            .RequirePermission(VerificationPermissions.ItemsReview)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("ApproveReviewItems");

        endpoints.MapPost("/items/{itemId:guid}/approve", ApproveAsync)
            .RequirePermission(VerificationPermissions.ItemsReview)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("ApproveReviewItem");

        endpoints.MapPost("/items/{itemId:guid}/correct", CorrectAsync)
            .RequirePermission(VerificationPermissions.ItemsReview)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("CorrectReviewItem");

        endpoints.MapPost("/items/{itemId:guid}/second-approve", SecondApproveAsync)
            .RequirePermission(VerificationPermissions.ItemsSecondApprove)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("SecondApproveReviewItem");

        endpoints.MapPost("/items/{itemId:guid}/reject", RejectAsync)
            .RequirePermission(VerificationPermissions.ItemsReview)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("RejectReviewItem");
    }

    /// <summary>
    /// Approves an entry (US-P2-VER-02). <c>If-Match</c>: the item's <c>version</c> the reviewer saw; a newer one is a
    /// 409 (TC-P2-VER-008). One's own entry, or a machine one operated in its time, is a 403 (ADR-0037 §4).
    /// </summary>
    private static async Task<Results<Ok<ReviewItemResponse>, ProblemHttpResult>> ApproveAsync(
        Guid itemId, [FromHeader(Name = "If-Match")] string? ifMatch, HttpResponse response, [FromServices] ReviewDecisions decisions, CancellationToken cancellationToken)
    {
        var expected = RecordVersionHttp.Expected(ifMatch);
        return expected.IsSuccess
            ? Decided(await decisions.ApproveAsync(itemId, expected.Value, cancellationToken).ConfigureAwait(false), response)
            : expected.Error.ToProblem();
    }

    /// <summary>Rejects an entry with a reason code and a comment of 10 to 500 characters (US-P2-VER-03); same rules as approving.</summary>
    private static async Task<Results<Ok<ReviewItemResponse>, ProblemHttpResult>> RejectAsync(
        Guid itemId, RejectEntry request, [FromHeader(Name = "If-Match")] string? ifMatch, HttpResponse response, [FromServices] ReviewDecisions decisions,
        CancellationToken cancellationToken)
    {
        var expected = RecordVersionHttp.Expected(ifMatch);
        return expected.IsSuccess
            ? Decided(await decisions.RejectAsync(itemId, request, expected.Value, cancellationToken).ConfigureAwait(false), response)
            : expected.Error.ToProblem();
    }

    /// <summary>
    /// Approves 1 to 100 entries in one decision (US-P2-VER-02 AC1, AC2): each with the <c>version</c> the reviewer saw;
    /// all of them or none (a flagged entry, one decided or changed meanwhile, one outside the caller's sites or refused
    /// by separation of duties stops the whole batch). One ledger entry lists every item and its record hash.
    /// </summary>
    private static async Task<Results<Ok<BulkApprovalResponse>, ProblemHttpResult>> ApproveManyAsync(
        ApproveItems request, [FromServices] ReviewDecisions decisions, CancellationToken cancellationToken)
    {
        var items = new List<BulkItem>();
        foreach (var item in request.Items ?? [])
        {
            var expected = RecordVersionHttp.Expected(item.Version);
            if (!expected.IsSuccess)
            {
                return expected.Error.ToProblem();
            }

            items.Add(new BulkItem(item.Id, expected.Value));
        }

        var result = await decisions.ApproveManyAsync(items, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? TypedResults.Ok(new BulkApprovalResponse(result.Value.Batch.Id.Value, [.. result.Value.Items.Select(ReviewItemResponse.From)]))
            : result.Error.ToProblem();
    }

    /// <summary>
    /// Corrects an entry (US-P2-VER-04): <c>fields</c> by name with their new values, and a reason of 10 to 500
    /// characters; <c>If-Match</c> as for approving; a recent sign-in. Returns the corrected item and the new version's
    /// item, approved or awaiting a second approver.
    /// </summary>
    private static async Task<Results<Ok<CorrectionResponse>, ProblemHttpResult>> CorrectAsync(
        Guid itemId, CorrectEntry request, [FromHeader(Name = "If-Match")] string? ifMatch, [FromServices] ReviewDecisions decisions, CancellationToken cancellationToken)
    {
        var expected = RecordVersionHttp.Expected(ifMatch);
        if (!expected.IsSuccess)
        {
            return expected.Error.ToProblem();
        }

        var result = await decisions.CorrectAsync(itemId, request, expected.Value, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? TypedResults.Ok(new CorrectionResponse(ReviewItemResponse.From(result.Value.Corrected), ReviewItemResponse.From(result.Value.Next)))
            : result.Error.ToProblem();
    }

    /// <summary>Approves a correction beyond the thresholds (US-P2-VER-04 AC3): another person than the corrector.</summary>
    private static async Task<Results<Ok<ReviewItemResponse>, ProblemHttpResult>> SecondApproveAsync(
        Guid itemId, [FromHeader(Name = "If-Match")] string? ifMatch, HttpResponse response, [FromServices] ReviewDecisions decisions, CancellationToken cancellationToken)
    {
        var expected = RecordVersionHttp.Expected(ifMatch);
        return expected.IsSuccess
            ? Decided(await decisions.SecondApproveAsync(itemId, expected.Value, cancellationToken).ConfigureAwait(false), response)
            : expected.Error.ToProblem();
    }

    /// <summary>
    /// The review policy of every record type as the company runs it (US-P2-VER-05): the version in force (0 and the
    /// defaults when none was published) and every version published, oldest first.
    /// </summary>
    private static async Task<Ok<ReviewPolicyListResponse>> ListPoliciesAsync([FromServices] ReviewPolicies policies, CancellationToken cancellationToken)
    {
        var published = await policies.ListAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        return TypedResults.Ok(new ReviewPolicyListResponse([.. ReviewPolicies.RecordTypes.Select(type =>
        {
            var versions = published.Where(p => p.RecordType == type).ToList();
            var current = versions.Where(p => p.EffectiveFrom <= now).MaxBy(p => p.Version);
            return new ReviewPolicyResponse(type, current is null ? ReviewPolicyVersionResponse.Defaults : ReviewPolicyVersionResponse.From(current),
                [.. versions.Select(ReviewPolicyVersionResponse.From)]);
        })]));
    }

    /// <summary>
    /// Publishes a new version of a record type's policy (US-P2-VER-05 AC2): effective now or from a later date, never
    /// before; ledgered; items already created keep the version they started under (TC-P2-VER-007).
    /// </summary>
    private static async Task<Results<Ok<ReviewPolicyVersionResponse>, ProblemHttpResult>> PublishPolicyAsync(
        string recordType, PublishPolicy request, [FromServices] ReviewPolicies policies, CancellationToken cancellationToken)
    {
        var result = await policies.PublishAsync(recordType, request, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.Ok(ReviewPolicyVersionResponse.From(result.Value)) : result.Error.ToProblem();
    }

    private static Results<Ok<ReviewItemResponse>, ProblemHttpResult> Decided(Result<ReviewItem> result, HttpResponse response)
    {
        if (!result.IsSuccess)
        {
            return result.Error.ToProblem();
        }

        response.SetETag(result.Value.Version);
        return TypedResults.Ok(ReviewItemResponse.From(result.Value));
    }

    /// <summary>
    /// The review queue of the caller's sites (US-P2-VER-01): <c>status</c> pending (default), reviewed or all; by site,
    /// record type, asset, producer (<c>producedBy</c>), flag code and covered time (<c>coveredFrom</c> inclusive,
    /// <c>coveredBefore</c> exclusive); <c>overdue</c> only the items escalated for waiting past their due time;
    /// <c>sort</c> <c>created</c> (default, oldest first) or <c>risk</c> (overdue, then flagged, then oldest; ADR-0038 §1);
    /// <c>cursor</c> is the last item ID of the previous page. The page carries an <c>ETag</c>: with
    /// <c>If-None-Match</c> an unchanged page is a 304 without a body (the console polls, ADR-0038 §3).
    /// </summary>
    private static async Task<Results<Ok<ReviewItemListResponse>, StatusCodeHttpResult, ProblemHttpResult>> SearchItemsAsync(
        Guid? siteId, string? status, string? recordType, Guid? cursor, int? limit, bool? overdue, string? sort, Guid? assetId, Guid? producedBy, string? flag,
        DateTimeOffset? coveredFrom, DateTimeOffset? coveredBefore, [FromHeader(Name = "If-None-Match")] string? ifNoneMatch, HttpResponse response,
        [FromServices] ReviewReading items, CancellationToken cancellationToken)
    {
        var search = new ReviewSearch
        {
            SiteId = siteId,
            Status = status,
            RecordType = recordType,
            Cursor = cursor,
            Limit = limit,
            Overdue = overdue ?? false,
            Sort = sort,
            AssetId = assetId,
            ProducedBy = producedBy,
            Flag = flag,
            CoveredFrom = coveredFrom,
            CoveredBefore = coveredBefore,
        };
        var result = await items.SearchAsync(search, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return result.Error.ToProblem();
        }

        var tag = PageTag(result.Value);
        response.Headers.ETag = tag;
        response.Headers.CacheControl = "private, no-cache";
        return string.Equals(ifNoneMatch, tag, StringComparison.Ordinal)
            ? TypedResults.StatusCode(StatusCodes.Status304NotModified)
            : TypedResults.Ok(new ReviewItemListResponse([.. result.Value.Items.Select(ReviewItemResponse.From)], result.Value.NextCursor));
    }

    /// <summary>The page as it is now: each item and its row version, and where the next page starts.</summary>
    private static string PageTag(ReviewPage page)
    {
        var text = string.Join(';', page.Items.Select(i => $"{i.Id.Value:N}.{i.Version}")) + "|" + page.NextCursor?.ToString("N");
        return $"\"{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..32]}\"";
    }
}

/// <summary>The review policy of every record type.</summary>
public sealed record ReviewPolicyListResponse(IReadOnlyList<ReviewPolicyResponse> Items);

/// <summary>A record type's policy: the version in force and every published version.</summary>
public sealed record ReviewPolicyResponse(string RecordType, ReviewPolicyVersionResponse Current, IReadOnlyList<ReviewPolicyVersionResponse> Versions);

/// <summary>One policy version (0: the defaults, never published).</summary>
public sealed record ReviewPolicyVersionResponse(
    int Version, DateTimeOffset? EffectiveFrom, bool AutoApprove, decimal FourEyesHours, decimal FourEyesShare, int SlaHours, string EscalateTo,
    DateTimeOffset? PublishedAt, string? PublishedBy)
{
    public static ReviewPolicyVersionResponse Defaults { get; } = new(
        0, null, PolicyTerms.Defaults.AutoApprove, PolicyTerms.Defaults.FourEyesHours, PolicyTerms.Defaults.FourEyesShare, PolicyTerms.Defaults.SlaHours,
        PolicyTerms.Defaults.EscalateTo, null, null);

    public static ReviewPolicyVersionResponse From(ReviewPolicy p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return new(p.Version, p.EffectiveFrom, p.AutoApprove, p.FourEyesHours, p.FourEyesShare, p.SlaHours, p.EscalateTo, p.PublishedAt, p.PublishedBy);
    }
}

/// <summary>A correction: the item corrected and the item of the new version (approved, or awaiting a second approver).</summary>
public sealed record CorrectionResponse(ReviewItemResponse Corrected, ReviewItemResponse Next);

/// <summary>An entry of a bulk approval: the item and the <c>version</c> the reviewer saw.</summary>
public sealed record ApproveItem(Guid Id, string? Version);

/// <summary>The entries to approve at once.</summary>
public sealed record ApproveItems(IReadOnlyList<ApproveItem>? Items);

/// <summary>A bulk approval: its batch (the one ledger entry) and the items, now approved.</summary>
public sealed record BulkApprovalResponse(Guid BatchId, IReadOnlyList<ReviewItemResponse> Items);

/// <summary>A page of review items, oldest first; <c>nextCursor</c> when more follow.</summary>
public sealed record ReviewItemListResponse(IReadOnlyList<ReviewItemResponse> Items, Guid? NextCursor);

/// <summary>
/// A review item (ADR-0037 §2): the record version it is about, the hash it was signed with, who produced it, the asset
/// and time it covers, its flags when submitted, where it stands, and its <c>version</c> for <c>If-Match</c>.
/// </summary>
public sealed record ReviewItemResponse(
    Guid Id, string RecordType, Guid RecordId, int RecordVersion, string RecordHash, Guid SiteId, Guid ProducedBy, Guid? AssetId,
    DateTimeOffset CoveredFrom, DateTimeOffset? CoveredTo, IReadOnlyList<string> Flags, string Status, DateTimeOffset CreatedAt, string Version, string? CorrectedBy,
    int PolicyVersion, DateTimeOffset DueAt, DateTimeOffset? EscalatedAt)
{
    public static ReviewItemResponse From(ReviewItem i)
    {
        ArgumentNullException.ThrowIfNull(i);
        return new(i.Id.Value, i.RecordType, i.RecordId, i.RecordVersion, i.RecordHash, i.SiteId, i.ProducedBy, i.AssetId, i.CoveredFrom, i.CoveredTo,
            i.Flags, i.Status, i.CreatedAt, RecordVersionHttp.ETag(i.Version), i.CorrectedBy, i.PolicyVersion, i.DueAt, i.EscalatedAt);
    }
}
