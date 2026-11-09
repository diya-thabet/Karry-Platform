using System.Data.Common;
using Karry.BuildingBlocks.Sync;
using Karry.Modules.Sync.Application;
using Karry.Modules.Sync.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Karry.Modules.Sync.Infrastructure;

internal sealed class InboxStore(SyncDbContext db) : IInboxStore
{
    public async Task<IInboxSession> OpenAsync(Guid deviceId, IReadOnlyCollection<CommandId> ids, CancellationToken cancellationToken)
    {
        // One upload of a tablet at a time (findings C-5, C-7): the lock is taken before anything is read, so the reads
        // below see every upload of this tablet that came before, and the lock is released with the transaction.
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var lockKey = $"sync.inbox:{deviceId}";
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", cancellationToken).ConfigureAwait(false);
            var stored = (await db.Inbox.Where(c => c.DeviceId == deviceId && ids.Contains(c.Id)).Select(c => c.Id).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
            var unhandled = (await db.Unhandled.Where(c => c.DeviceId == deviceId && ids.Contains(c.Id)).Select(c => c.Id).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
            var last = await db.Inbox.Where(c => c.DeviceId == deviceId && c.Status == InboxStatus.Received)
                .MaxAsync(c => (long?)c.Sequence, cancellationToken).ConfigureAwait(false) ?? 0;
            // Waiting rows whose command reached the inbox were handled: only the others still wait (bounded by MaxWaiting).
            var waiting = await db.Waiting.Where(w => w.DeviceId == deviceId && w.Sequence > last
                    && !db.Inbox.Any(i => i.DeviceId == w.DeviceId && i.Id == w.Id))
                .OrderBy(w => w.Sequence).Take(InboxRules.MaxWaiting).ToListAsync(cancellationToken).ConfigureAwait(false);
            return new Session(db, transaction, stored, unhandled, last, waiting);
        }
        catch
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class Session(
        SyncDbContext db, IDbContextTransaction transaction, IReadOnlySet<CommandId> stored, IReadOnlySet<CommandId> unhandled, long last,
        IReadOnlyList<WaitingCommand> waiting)
        : IInboxSession, ISyncTransaction
    {
        public IReadOnlySet<CommandId> Stored => stored;

        public IReadOnlySet<CommandId> Unhandled => unhandled;

        public long LastSequence => last;

        public IReadOnlyList<WaitingCommand> Waiting => waiting;

        public ISyncTransaction Transaction => this;

        public DbConnection Connection => db.Database.GetDbConnection();

        DbTransaction ISyncTransaction.Transaction => transaction.GetDbTransaction();

        public Task SavepointAsync(string name, CancellationToken cancellationToken) => transaction.CreateSavepointAsync(name, cancellationToken);

        public Task ReleaseAsync(string name, CancellationToken cancellationToken) => transaction.ReleaseSavepointAsync(name, cancellationToken);

        public Task RollbackToAsync(string name, CancellationToken cancellationToken) => transaction.RollbackToSavepointAsync(name, cancellationToken);

        public void Add(InboxCommand command) => db.Inbox.Add(command);

        public void Add(ProcessedCommand processed) => db.Processed.Add(processed);

        public void Add(UnhandledCommand unhandled) => db.Unhandled.Add(unhandled);

        public void Add(WaitingCommand waiting) => db.Waiting.Add(waiting);

        public async Task<bool> CommitAsync(CancellationToken cancellationToken)
        {
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

        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}
