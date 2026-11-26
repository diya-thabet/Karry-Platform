using System.Data.Common;
using System.Runtime.CompilerServices;
using Karry.SharedKernel.Tenancy;

namespace Karry.BuildingBlocks.Ledger;

/// <summary>
/// Ledger entries of a long transaction, appended at its end (P2.12a burst finding, ADR-0008 as amended): appending
/// locks the tenant's ledger head until the commit, so a transaction that appends early and then works on (a tablet's
/// batch: several commands, each saved, flagged and recorded) makes every other writer of the tenant wait for it.
/// A transaction marked with <see cref="Begin"/> keeps what <see cref="ILedgerWriter"/> is given, and its owner calls
/// <see cref="FlushAsync"/> right before committing: the entries are still in the same transaction as their change
/// (they commit or roll back together), in the order they were given, and the head is locked only for the last moment.
/// Savepoints are followed: what was given after a savepoint goes when the transaction rolls back to it.
/// </summary>
public static class LedgerDeferral
{
    private static readonly ConditionalWeakTable<DbTransaction, Pending> Transactions = [];

    /// <summary>From now on, entries given for <paramref name="transaction"/> wait for <see cref="FlushAsync"/>.</summary>
    public static void Begin(DbTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        Transactions.AddOrUpdate(transaction, new Pending());
    }

    /// <summary>Keeps the records when the transaction defers its ledger; false when they must be appended now.</summary>
    public static bool TryDefer(DbTransaction transaction, TenantId? unitOfWorkTenant, IReadOnlyList<LedgerRecord> records)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(records);
        if (!Transactions.TryGetValue(transaction, out var pending))
        {
            return false;
        }

        lock (pending)
        {
            pending.Records.AddRange(records.Select(record => (unitOfWorkTenant, record)));
        }

        return true;
    }

    /// <summary>The transaction made a savepoint: what is given from now on can be rolled back with it.</summary>
    public static void Savepoint(DbTransaction transaction, string name) => With(transaction, pending => pending.Marks[name] = pending.Records.Count);

    /// <summary>The transaction rolled back to a savepoint: the records given since are dropped.</summary>
    public static void RollbackTo(DbTransaction transaction, string name) => With(transaction, pending =>
    {
        if (pending.Marks.TryGetValue(name, out var count))
        {
            pending.Records.RemoveRange(count, pending.Records.Count - count);
        }
    });

    /// <summary>The savepoint was released: its records stay, as part of the transaction.</summary>
    public static void Release(DbTransaction transaction, string name) => With(transaction, pending => pending.Marks.Remove(name));

    /// <summary>The records waiting for this transaction (tests and diagnostics).</summary>
    public static int Waiting(DbTransaction transaction) =>
        Transactions.TryGetValue(transaction, out var pending) ? pending.Records.Count : 0;

    /// <summary>
    /// Appends what waits, in order, in the transaction, and stops deferring: call it right before the commit. A
    /// transaction that rolls back instead simply drops what waited.
    /// </summary>
    public static async Task FlushAsync(ILedgerWriter writer, DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!Transactions.TryGetValue(transaction, out var pending))
        {
            return;
        }

        Transactions.Remove(transaction);
        List<(TenantId? Tenant, LedgerRecord Record)> records;
        lock (pending)
        {
            records = [.. pending.Records];
        }

        // Runs of the same unit-of-work tenant, in the order given (one run in practice: a batch has one tenant).
        var start = 0;
        while (start < records.Count)
        {
            var tenant = records[start].Tenant;
            var end = start;
            while (end < records.Count && records[end].Tenant == tenant)
            {
                end++;
            }

            await writer.AppendAsync(connection, transaction, tenant, [.. records.Skip(start).Take(end - start).Select(r => r.Record)], cancellationToken)
                .ConfigureAwait(false);
            start = end;
        }
    }

    /// <summary>Stops deferring without appending (the transaction rolled back).</summary>
    public static void Discard(DbTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        Transactions.Remove(transaction);
    }

    private static void With(DbTransaction transaction, Action<Pending> change)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (Transactions.TryGetValue(transaction, out var pending))
        {
            lock (pending)
            {
                change(pending);
            }
        }
    }

    private sealed class Pending
    {
        public List<(TenantId? Tenant, LedgerRecord Record)> Records { get; } = [];

        public Dictionary<string, int> Marks { get; } = new(StringComparer.Ordinal);
    }
}
