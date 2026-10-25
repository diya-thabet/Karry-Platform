using Karry.Modules.Billing.Application;
using Karry.Modules.Billing.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Karry.Modules.Billing.Infrastructure;

internal sealed class PlanStore(BillingDbContext db) : IPlanStore
{
    public async Task<IReadOnlyList<Plan>> LatestAsync(CancellationToken cancellationToken)
    {
        // A handful of plans with a few versions each: load them and pick the newest per code in memory.
        var all = await db.Plans.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. all.GroupBy(p => p.Code).Select(g => g.MaxBy(p => p.Version)!).OrderBy(p => p.PublishedAt).ThenBy(p => p.Code, StringComparer.Ordinal)];
    }

    public async Task<IReadOnlyList<Plan>> VersionsAsync(string code, CancellationToken cancellationToken) =>
        await db.Plans.AsNoTracking().Where(p => p.Code == code).OrderByDescending(p => p.Version).ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<bool> TryAddAsync(Plan plan, CancellationToken cancellationToken)
    {
        db.Plans.Add(plan);
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
