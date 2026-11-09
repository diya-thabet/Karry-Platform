using Karry.BuildingBlocks.Sync;
using Karry.Modules.FieldOps.Application;
using Karry.Modules.FieldOps.Domain;
using Microsoft.EntityFrameworkCore;

namespace Karry.Modules.FieldOps.Infrastructure;

internal sealed class ShiftEntryStore(FieldOpsDbContext db) : IShiftEntryStore
{
    public Task JoinAsync(ISyncTransaction transaction, CancellationToken cancellationToken) => db.JoinAsync(transaction, cancellationToken);

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

    public void Add(ShiftEntryVersion version) => db.ShiftEntries.Add(version);

    public Task SaveAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
