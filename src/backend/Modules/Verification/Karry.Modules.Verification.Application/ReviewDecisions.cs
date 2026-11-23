using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Persistence;
using Karry.Modules.Identity.Contracts;
using Karry.Modules.Verification.Contracts;
using Karry.Modules.Verification.Domain;
using Karry.SharedKernel.Results;

namespace Karry.Modules.Verification.Application;

/// <summary>The current company's review items for a decision (RLS), and the decisions themselves.</summary>
public interface IReviewItemStore
{
    Task<ReviewItem?> FindAsync(ReviewItemId id, CancellationToken cancellationToken);

    Task<IReadOnlyList<ReviewItem>> FindManyAsync(IReadOnlyCollection<ReviewItemId> ids, CancellationToken cancellationToken);

    void Add(ReviewAction action);

    void Add(ReviewBatch batch);

    /// <summary>False when someone else decided on the item first (its version moved): nothing is saved.</summary>
    Task<bool> TrySaveAsync(CancellationToken cancellationToken);
}

/// <summary>One entry of a bulk approval: the item and the version the reviewer saw (its <c>If-Match</c>).</summary>
public sealed record BulkItem(Guid Id, uint? Version);

/// <summary>What a reviewer sends to reject an entry (US-P2-VER-03 AC1).</summary>
public sealed record RejectEntry(string? ReasonCode, string? Comment);

/// <summary>
/// Approve and reject (ADR-0037 §2–§4): only on an item of the caller's sites, only on the version the reviewer saw
/// (<c>If-Match</c>, TC-P2-VER-008), never on one's own record or a machine one operated in the time it covers, with
/// the permissions as they are at the moment of the decision (TC-P2-VER-009). Every decision is ledgered with the
/// record version's hash.
/// </summary>
public sealed class ReviewDecisions(
    IReviewItemStore store, ICallerAccess caller, IReviewers reviewers, IEnumerable<IReviewableRecords> records, TimeProvider clock)
{
    public static readonly Error NotFound = Error.NotFound("verification.item_not_found", "No such entry to review.");

    public Task<Result<ReviewItem>> ApproveAsync(Guid itemId, uint? expectedVersion, CancellationToken cancellationToken) =>
        DecideAsync(itemId, expectedVersion, item => item.Approve(clock, caller.Subject), cancellationToken);

    public Task<Result<ReviewItem>> RejectAsync(Guid itemId, RejectEntry command, uint? expectedVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return DecideAsync(itemId, expectedVersion, item => item.Reject(clock, caller.Subject, command.ReasonCode, command.Comment), cancellationToken);
    }

    /// <summary>
    /// Approves several entries in one decision (US-P2-VER-02 AC1, AC2): every one must be of the caller's sites, at the
    /// version the reviewer saw, pending, without flags and allowed by separation of duties, or nothing is approved
    /// (TC-P2-VER-002). One ledger entry lists every item and the hash of its record version.
    /// </summary>
    public async Task<Result<(ReviewBatch Batch, IReadOnlyList<ReviewItem> Items)>> ApproveManyAsync(IReadOnlyList<BulkItem> requested, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requested);
        if (requested.Count is 0 or > ReviewRules.BulkMax || requested.Select(r => r.Id).Distinct().Count() != requested.Count)
        {
            return ReviewRules.BulkSize;
        }

        var found = await store.FindManyAsync([.. requested.Select(r => new ReviewItemId(r.Id))], cancellationToken).ConfigureAwait(false);
        var scope = caller.Current.ScopeOf(VerificationPermissions.ItemsReview);
        var items = new List<ReviewItem>();
        foreach (var request in requested)
        {
            var item = found.SingleOrDefault(i => i.Id.Value == request.Id);
            if (item is null || !scope.CoversSite(item.SiteId))
            {
                return NotFound;
            }

            if (!RecordVersions.Matches(item.Version, request.Version))
            {
                return RecordVersions.Conflict;
            }

            if (item.Status != ReviewStatuses.Pending)
            {
                return ReviewRules.NotPending;
            }

            items.Add(item);
        }

        var flagged = items.Where(i => i.Flags.Count > 0).Select(i => i.Id.Value).ToList();
        if (flagged.Count > 0)
        {
            return ReviewRules.BulkFlagged(flagged);
        }

        foreach (var item in items)
        {
            if (await SeparationOfDutiesAsync(item, cancellationToken).ConfigureAwait(false) is { } refused)
            {
                return refused;
            }
        }

        var batch = ReviewBatch.Approve(clock, items[0].TenantId, items, caller.Subject);
        foreach (var item in items)
        {
            var action = item.ApproveIn(clock, batch.Id, caller.Subject);
            if (!action.IsSuccess)
            {
                return action.Error;
            }

            store.Add(action.Value);
        }

        store.Add(batch);
        return await store.TrySaveAsync(cancellationToken).ConfigureAwait(false) ? (batch, items) : RecordVersions.Conflict;
    }

    private async Task<Result<ReviewItem>> DecideAsync(Guid itemId, uint? expectedVersion, Func<ReviewItem, Result<ReviewAction>> decide, CancellationToken cancellationToken)
    {
        // The item first: outside the caller's sites (or company) it does not exist, whatever the request says.
        var item = await store.FindAsync(new ReviewItemId(itemId), cancellationToken).ConfigureAwait(false);
        if (item is null || !caller.Current.ScopeOf(VerificationPermissions.ItemsReview).CoversSite(item.SiteId))
        {
            return NotFound;
        }

        if (!RecordVersions.Matches(item.Version, expectedVersion))
        {
            return RecordVersions.Conflict;
        }

        if (await SeparationOfDutiesAsync(item, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var action = decide(item);
        if (!action.IsSuccess)
        {
            return action.Error;
        }

        store.Add(action.Value);
        return await store.TrySaveAsync(cancellationToken).ConfigureAwait(false) ? item : RecordVersions.Conflict;
    }

    /// <summary>The reviewer as a person (their linked field account) against the record's producer and the asset's operators.</summary>
    private async Task<Error?> SeparationOfDutiesAsync(ReviewItem item, CancellationToken cancellationToken)
    {
        if (caller.Subject is not { } subject || await reviewers.FieldAccountOfAsync(subject, cancellationToken).ConfigureAwait(false) is not { } person)
        {
            return null; // an office account with no field account produced nothing and operated nothing
        }

        if (person == item.ProducedBy)
        {
            return ReviewRules.OwnRecord;
        }

        var owner = records.SingleOrDefault(r => r.RecordType == item.RecordType);
        if (item.AssetId is { } asset && owner is not null
            && await owner.OperatedAsync(person, asset, item.CoveredFrom, item.CoveredTo, cancellationToken).ConfigureAwait(false))
        {
            return ReviewRules.OperatedAsset;
        }

        return null;
    }
}
