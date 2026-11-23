using Karry.BuildingBlocks.Sync;
using Karry.Modules.FieldOps.Application;
using Karry.Modules.FieldOps.Domain;
using Microsoft.EntityFrameworkCore;

namespace Karry.Modules.FieldOps.Infrastructure;

internal sealed class ShiftEntryStore(FieldOpsDbContext db) : IShiftEntryStore
{
    public Task JoinAsync(ISyncTransaction transaction, CancellationToken cancellationToken) => db.JoinAsync(transaction, cancellationToken);

    public Task<ChecklistTemplate?> ChecklistInForceAsync(string assetType, DateTimeOffset instant, CancellationToken cancellationToken) =>
        db.ChecklistTemplates.AsNoTracking().Where(t => t.AssetType == assetType && t.PublishedAt <= instant)
            .OrderByDescending(t => t.Version).FirstOrDefaultAsync(cancellationToken);

    public Task<bool> CheckedAsync(Guid assetId, Guid operatorId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
        db.ChecklistSubmissions.AsNoTracking().AnyAsync(s => s.AssetId == assetId && s.OperatorId == operatorId && s.SubmittedAt >= from && s.SubmittedAt <= to, cancellationToken);

    public async Task<IReadOnlyList<ShiftEntryVersion>> StartsAsync(Guid assetId, Guid operatorId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
        await db.ShiftEntries.AsNoTracking()
            .Where(e => e.Version == 1 && e.AssetId == assetId && e.OperatorId == operatorId && e.StartedAt >= from && e.StartedAt <= to)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    public Task<ShiftEntryVersion?> LatestAsync(Guid rootId, CancellationToken cancellationToken) =>
        db.ShiftEntries.AsNoTracking().Where(e => e.RootId == rootId).OrderByDescending(e => e.Version).FirstOrDefaultAsync(cancellationToken);

    public Task<ShiftEntryVersion?> LastSegmentOfMeterAsync(Guid meterId, Guid exceptRootId, DateTimeOffset before, CancellationToken cancellationToken) =>
        db.ShiftEntries.AsNoTracking()
            .Where(e => e.MeterId == meterId && e.RootId != exceptRootId && e.StartedAt < before)
            .Where(e => !db.ShiftEntries.Any(later => later.RootId == e.RootId && later.Version > e.Version))
            .OrderByDescending(e => e.StartedAt).ThenByDescending(e => e.RootId)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<ShiftEntryVersion>> SegmentsOfOperatorAsync(
        Guid operatorId, Guid exceptRootId, DateTimeOffset from, DateTimeOffset? to, CancellationToken cancellationToken)
    {
        var query = db.ShiftEntries.AsNoTracking()
            .Where(e => e.OperatorId == operatorId && e.RootId != exceptRootId && (e.EndedAt == null || e.EndedAt > from))
            .Where(e => !db.ShiftEntries.Any(later => later.RootId == e.RootId && later.Version > e.Version));
        if (to is { } until)
        {
            query = query.Where(e => e.StartedAt < until);
        }

        return await query.ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Guid>> EntriesWithPhotoAsync(string photo, Guid exceptRootId, CancellationToken cancellationToken) =>
        await db.ShiftEntries.AsNoTracking()
            .Where(e => e.RootId != exceptRootId && (e.StartPhoto == photo || e.EndPhoto == photo))
            .Select(e => e.RootId).Distinct().OrderBy(r => r).Take(20)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<ShiftEntryVersion>> SegmentsOfMeterAsync(Guid meterId, CancellationToken cancellationToken) =>
        await db.ShiftEntries.AsNoTracking()
            .Where(e => e.MeterId == meterId)
            .Where(e => !db.ShiftEntries.Any(later => later.RootId == e.RootId && later.Version > e.Version))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    public Task<bool> HeldAtAsync(Guid assetId, DateTimeOffset instant, CancellationToken cancellationToken) =>
        db.SafetyHolds.AnyAsync(
            h => h.AssetId == assetId && h.OpenedAt <= instant && !db.SafetyHoldReleases.Any(r => r.Id == h.Id && r.ReleasedAt <= instant), cancellationToken);

    public void Add(ShiftEntryVersion version) => db.ShiftEntries.Add(version);

    public Task SaveAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
