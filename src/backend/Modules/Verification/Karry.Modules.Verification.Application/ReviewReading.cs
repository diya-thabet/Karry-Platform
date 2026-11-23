using Karry.BuildingBlocks.Authorization;
using Karry.Modules.Verification.Domain;
using Karry.SharedKernel.Authorization;
using Karry.SharedKernel.Results;
using Roles = Karry.SharedKernel.Authorization.DefaultRoles;

namespace Karry.Modules.Verification.Application;

/// <summary>Verification permissions (ADR-0037). Site-scoped: a controller sees and decides on the items of their sites.</summary>
public static class VerificationPermissions
{
    /// <summary>Read the review items of the caller's sites (the queue; deciding on them comes with P2.8 slice 2).</summary>
    public const string ItemsRead = "verification.items.read";

    /// <summary>Approve or reject the entries of the caller's sites (US-P2-VER-02, 03), never their own (ADR-0037 §4).</summary>
    public const string ItemsReview = "verification.items.review";

    /// <summary>
    /// Approve a correction beyond the four-eyes thresholds (US-P2-VER-04 AC3): a second person, never the corrector.
    /// </summary>
    public const string ItemsSecondApprove = "verification.items.second-approve";

    public static IReadOnlyList<PermissionDefinition> All { get; } =
    [
        new(ItemsRead, ScopeKind.Site, privileged: false, Roles.TenantAdmin, Roles.Executive, Roles.Controller, Roles.SiteManager),
        new(ItemsReview, ScopeKind.Site, privileged: false, Roles.TenantAdmin, Roles.Controller, Roles.SiteManager),
        new(ItemsSecondApprove, ScopeKind.Site, privileged: true, Roles.TenantAdmin, Roles.SiteManager),
    ];
}

/// <summary>A checked search of review items; <c>Sites</c> null means every site of the company.</summary>
public sealed record ReviewQuery(IReadOnlySet<Guid>? Sites, Guid? SiteId, IReadOnlyList<string> Statuses, string? RecordType, Guid? After, int Take);

/// <summary>Reads review items for the queue (RLS: the current company only).</summary>
public interface IReviewItemReader
{
    /// <summary>Items matching the query, oldest first (UUIDv7 order): the longest-waiting entry comes first.</summary>
    Task<IReadOnlyList<ReviewItem>> SearchAsync(ReviewQuery query, CancellationToken cancellationToken);
}

/// <summary>A page of review items; <c>NextCursor</c> is the last item ID of the page when more follow.</summary>
public sealed record ReviewPage(IReadOnlyList<ReviewItem> Items, Guid? NextCursor);

/// <summary>The queue of review items (ADR-0037 §2), limited to the caller's sites (ADR-0022).</summary>
public sealed class ReviewReading(IReviewItemReader reader, ICallerAccess caller)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public static readonly Error LimitInvalid = Error.Validation("verification.limit_invalid", $"A page holds 1 to {MaxLimit} items.");

    public static readonly Error StatusInvalid = Error.Validation("verification.status_invalid", "The status is pending, reviewed or all.");

    /// <summary>
    /// Items of the caller's sites: <c>status</c> pending (default: waiting for a decision, pending or awaiting a second
    /// approval), reviewed (decided) or all; by site and record type; <c>cursor</c> is the last item ID of the previous page.
    /// </summary>
    public async Task<Result<ReviewPage>> SearchAsync(Guid? siteId, string? status, string? recordType, Guid? cursor, int? limit, CancellationToken cancellationToken)
    {
        var take = limit ?? DefaultLimit;
        if (take is < 1 or > MaxLimit)
        {
            return LimitInvalid;
        }

        IReadOnlyList<string>? statuses = status switch
        {
            null or "pending" => [ReviewStatuses.Pending, ReviewStatuses.AwaitingSecond],
            "reviewed" => [ReviewStatuses.Approved, ReviewStatuses.Rejected, ReviewStatuses.Corrected],
            "all" => ReviewStatuses.All,
            _ => null,
        };
        if (statuses is null)
        {
            return StatusInvalid;
        }

        var scope = caller.Current.ScopeOf(VerificationPermissions.ItemsRead);
        var found = await reader.SearchAsync(
            new ReviewQuery(scope.TenantWide ? null : scope.Sites, siteId, statuses, recordType, cursor, take + 1), cancellationToken).ConfigureAwait(false);
        return found.Count > take ? new ReviewPage([.. found.Take(take)], found[take - 1].Id.Value) : new ReviewPage(found, null);
    }
}
