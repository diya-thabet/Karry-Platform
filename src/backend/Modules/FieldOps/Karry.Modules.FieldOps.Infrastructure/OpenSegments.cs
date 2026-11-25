using Karry.BuildingBlocks.Sync;
using Karry.Modules.FieldOps.Application;
using Karry.Modules.FieldOps.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Karry.Modules.FieldOps.Infrastructure;

/// <summary>Unended segments of the current company (RLS) and a transaction on the FieldOps connection to flag them in.</summary>
internal sealed class OpenSegments(FieldOpsDbContext db) : IOpenSegments
{
    public async Task<IReadOnlyList<ShiftEntryVersion>> UnendedAsync(DateTimeOffset since, DateTimeOffset by, int take, CancellationToken cancellationToken) =>
        await db.ShiftEntries.AsNoTracking()
            .Where(e => !db.ShiftEntries.Any(later => later.RootId == e.RootId && later.Version > e.Version))
            .Where(e => e.Status == ShiftEntryStatus.Open && e.ShiftEndsAt != null && e.ShiftEndsAt >= since && e.ShiftEndsAt <= by)
            .OrderByDescending(e => e.ShiftEndsAt).Take(take).ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task InTransactionAsync(Func<ISyncTransaction, Task> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await work(new Joined(db.Database.GetDbConnection(), transaction.GetDbTransaction())).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record Joined(System.Data.Common.DbConnection Connection, System.Data.Common.DbTransaction Transaction) : ISyncTransaction;
}
