using Karry.Modules.Billing.Application;
using Karry.Modules.Billing.Domain;
using Microsoft.EntityFrameworkCore;

namespace Karry.Modules.Billing.Infrastructure;

internal sealed class UsageStore(BillingDbContext db) : IUsageStore
{
    public async Task<IReadOnlyList<UsageSnapshot>> OnDayAsync(DateOnly day, CancellationToken cancellationToken) =>
        await db.UsageSnapshots.Where(s => s.Day == day).ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<DateOnly?> LatestDayAsync(CancellationToken cancellationToken) =>
        await db.UsageSnapshots.MaxAsync(s => (DateOnly?)s.Day, cancellationToken).ConfigureAwait(false);

    public void Add(UsageSnapshot snapshot) => db.UsageSnapshots.Add(snapshot);

    public Task SaveAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
