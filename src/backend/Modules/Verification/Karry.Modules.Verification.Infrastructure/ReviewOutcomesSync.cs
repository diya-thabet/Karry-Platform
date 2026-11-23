using Karry.BuildingBlocks.Sync;
using Karry.Modules.Verification.Contracts;
using Karry.Modules.Verification.Domain;
using Microsoft.EntityFrameworkCore;

namespace Karry.Modules.Verification.Infrastructure;

/// <summary>A controller's decision on a shift entry, as the tablet keeps it (ADR-0037 §8).</summary>
public sealed record ReviewOutcomeSyncRow(Guid Id, Guid RecordId, int RecordVersion, string Status, string? ReasonCode, string? Comment);

/// <summary>
/// The <c>reviewOutcomes</c> collection of the tablet's pull (ADR-0037 §8): the decided shift entries of the tablet's
/// site over the operator history's 14 days, approved or rejected with the reason and comment, whole at every pull
/// (<c>replace</c>): a few hundred rows at most, and an item decided again (a correction, later) replaces its row.
/// </summary>
internal sealed class ReviewOutcomesSync(VerificationDbContext db, TimeProvider clock) : ISyncSource
{
    public const string Name = "reviewOutcomes";

    /// <summary>The operator's history on the tablet (field app <c>historyDays</c>).</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(14);

    public async Task<IReadOnlyList<SyncDelta>> ChangesAsync(SyncScope scope, DateTimeOffset? since, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var from = clock.GetUtcNow() - Window;
        string[] decided = [ReviewStatuses.Approved, ReviewStatuses.Rejected];
        var items = await db.Items.AsNoTracking()
            .Where(i => i.SiteId == scope.SiteId && i.RecordType == ReviewRecordTypes.ShiftEntry && decided.Contains(i.Status) && i.CoveredFrom >= from)
            .OrderBy(i => i.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var rejected = items.Where(i => i.Status == ReviewStatuses.Rejected).Select(i => i.Id).ToList();
        var reasons = (await db.Actions.AsNoTracking()
                .Where(a => rejected.Contains(a.ItemId) && a.Kind == ReviewActionKinds.Reject)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(a => a.ItemId)
            .ToDictionary(g => g.Key, g => g.MaxBy(a => a.At)!);
        return
        [
            new SyncDelta(Name, [.. items.Select(i => new ReviewOutcomeSyncRow(
                i.Id.Value, i.RecordId, i.RecordVersion, i.Status, reasons.GetValueOrDefault(i.Id)?.ReasonCode, reasons.GetValueOrDefault(i.Id)?.Comment))], [], Replace: true),
        ];
    }
}
