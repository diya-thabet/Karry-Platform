using Karry.Modules.FieldOps.Domain;
using Karry.Modules.Verification.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Karry.Modules.FieldOps.Infrastructure;

/// <summary>
/// What separation of duties asks about shift entries (ADR-0037 §4): whether a person drove a machine during the time
/// an entry covers. A segment runs as for OVERLAP (<see cref="ShiftEntryRules.RunsUntil"/>); row-level security keeps it
/// in the company.
/// </summary>
internal sealed class ShiftEntryReviewFacts(FieldOpsDbContext db) : IReviewableRecords
{
    public string RecordType => ReviewRecordTypes.ShiftEntry;

    public async Task<bool> OperatedAsync(Guid person, Guid assetId, DateTimeOffset from, DateTimeOffset? to, CancellationToken cancellationToken)
    {
        // A segment that started more than a run before the window cannot reach into it; one starting after it cannot either.
        var earliest = from - ShiftEntryRules.MaxOpenRun - ShiftEntryRules.MaxOpenRun;
        var latest = to ?? from + ShiftEntryRules.MaxOpenRun;
        var versions = await db.ShiftEntries.AsNoTracking()
            .Where(e => e.OperatorId == person && e.AssetId == assetId && e.StartedAt >= earliest && e.StartedAt < latest)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return versions
            .GroupBy(e => e.RootId)
            .Select(g => g.MaxBy(e => e.Version)!)
            .Any(e => ShiftEntryRules.Overlaps(e.StartedAt, ShiftEntryRules.RunsUntil(e), from, to));
    }
}
