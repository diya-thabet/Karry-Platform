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

    public void Add(ShiftEntryVersion version) => db.ShiftEntries.Add(version);

    public Task SaveAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
