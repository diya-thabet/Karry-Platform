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
        ArgumentNullException.ThrowIfNull(commands);
        // One upload of a tablet stores at a time (hardening plan H-C3): two uploads giving the same sequence numbers to
        // different commands otherwise deadlocked on the unique index, which surfaced as a 503 instead of the conflict.
        // The lock is released with the transaction; the later upload then meets the stored rows.
        var lockKey = $"sync.inbox:{commands.First().DeviceId}";
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", cancellationToken).ConfigureAwait(false);
        db.Inbox.AddRange(commands);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            db.ChangeTracker.Clear();
            return false;
        }
    }
}
