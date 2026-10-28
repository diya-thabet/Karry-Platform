using Karry.Modules.Sync.Application;
using Karry.Modules.Sync.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Karry.Modules.Sync.Infrastructure;

internal sealed class InboxStore(SyncDbContext db) : IInboxStore
{
    public async Task<long> LastSequenceAsync(Guid deviceId, CancellationToken cancellationToken) =>
        await db.Inbox.Where(c => c.DeviceId == deviceId && c.Status == InboxStatus.Received)
            .MaxAsync(c => (long?)c.Sequence, cancellationToken).ConfigureAwait(false) ?? 0;

    public async Task<IReadOnlySet<CommandId>> StoredAsync(Guid deviceId, IReadOnlyCollection<CommandId> ids, CancellationToken cancellationToken) =>
        (await db.Inbox.Where(c => c.DeviceId == deviceId && ids.Contains(c.Id)).Select(c => c.Id).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();

    public async Task<bool> TryAddAsync(IReadOnlyCollection<InboxCommand> commands, CancellationToken cancellationToken)
    {
        db.Inbox.AddRange(commands);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }
}
